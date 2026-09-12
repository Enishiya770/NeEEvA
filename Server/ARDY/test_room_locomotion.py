"""Protocol, cancellation and shared-GPU ownership tests without model weights."""
import asyncio
import copy
import threading
import unittest

import httpx
from pydantic import ValidationError

from Server.ARDY.motion_service.app import create_app
from Server.ARDY.motion_service.locomotion_protocol import CORE_NAMES, LocomotionRequest, LocomotionCancel
from Server.ARDY.motion_service.locomotion_service import RoomLocomotionService
from Server.ARDY.motion_service.protocol import ServiceError
from Server.ARDY.motion_service.service import MotionService
from Server.ARDY.test_motion_service import FakeBackend, FakeProvider, request as upper_request


def payload(character="room", revision=1):
    return dict(schema=1, characterId=character, requestId=f"{character}-{revision}", revision=revision,
        description="A person walks and stops.", targets=[dict(rootPosition=dict(x=0,y=0,z=min(i,19)*.02), headingDegrees=0) for i in range(40)],
        initialHistory=dict(kind="unity-full-body-projection-v1", fps=20, jointNames=CORE_NAMES,
            frames=[dict(rootPosition=dict(x=0,y=.95,z=0), globalRotations=[dict(x=0,y=0,z=0,w=1) for _ in range(27)]) for _ in range(16)]))


class ProtocolTests(unittest.TestCase):
    def test_valid_full_body_and_serialized_bound(self):
        data = payload()
        data["initialHistory"]["frames"][-1]["globalRotations"][19] = dict(x=.6,y=0,z=0,w=.8)
        self.assertLess(len(LocomotionRequest(**data).model_dump_json()), 131072)

    def test_rejects_missing_actual_history_and_unbounded_or_nonflat_routes(self):
        for change in ("history", "order", "stairs", "speed", "heading", "stop", "initial", "window", "quat", "jump", "initial-heading"):
            data = copy.deepcopy(payload())
            if change == "history": del data["initialHistory"]
            if change == "order": data["initialHistory"]["jointNames"] = list(reversed(CORE_NAMES))
            if change == "stairs": data["targets"][0]["rootPosition"]["y"] = .2
            if change == "speed": data["targets"][1]["rootPosition"]["x"] = 1
            if change == "heading": data["targets"][1]["headingDegrees"] = 180
            if change == "stop": data["targets"][-1]["rootPosition"]["z"] += .02
            if change == "initial": data["initialHistory"]["frames"] = [dict(f,rootPosition=dict(x=4,y=.95,z=0)) for f in data["initialHistory"]["frames"]]
            if change == "window": data["targets"].append(data["targets"][-1])
            if change == "quat": data["initialHistory"]["frames"][0]["globalRotations"][0]["w"] = 0
            if change == "jump": data["initialHistory"]["frames"][-1]["rootPosition"]["y"] = 2
            if change == "initial-heading": data["initialHistory"]["frames"][-1]["globalRotations"][0] = dict(x=0,y=.6,z=0,w=.8)
            with self.subTest(change=change), self.assertRaises(ValidationError): LocomotionRequest(**data)


class RoomTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.backend, self.provider = FakeBackend(), FakeProvider()
        self.shared = MotionService(self.backend, self.provider)
        self.entered, self.release = threading.Event(), threading.Event()
        self.release.set()
        self.calls = []
        def generate(backend, request, embedding, cancel):
            self.entered.set()
            self.release.wait(3)
            self.calls.append((request.revision, cancel.is_set()))
            return dict(schema=1,revision=request.revision,requestId=request.requestId)
        self.room = RoomLocomotionService(self.shared, generate, queue_limit=2, character_limit=4)
        await self.shared.start()
        await self.room.start()

    async def asyncTearDown(self):
        self.release.set()
        self.backend.release.set()
        await self.room.close()
        await self.shared.close()

    async def entered_event(self, event):
        for _ in range(200):
            if event.is_set(): return
            await asyncio.sleep(.005)
        self.fail("inference did not start")

    def submit(self, character="room", revision=1):
        return self.room.submit(LocomotionRequest(**payload(character,revision)))

    async def test_exact_replay_and_revision_binding(self):
        first = self.submit()
        result = await first.future
        self.assertIs(self.submit(), first)
        changed = payload(); changed["seed"] = 12
        with self.assertRaises(ServiceError): self.room.submit(LocomotionRequest(**changed))
        self.assertEqual(result["source"]["featureSource"], "test-fake")
        self.assertEqual(len(self.calls), 1)

    async def test_cancel_running_discards_late_result_and_keeps_tombstone(self):
        self.release.clear()
        old = self.submit()
        await self.entered_event(self.entered)
        self.room.cancel(LocomotionCancel(characterId="room",revision=1))
        with self.assertRaises(ServiceError): await old.future
        with self.assertRaises(ServiceError): self.submit()
        new = self.submit(revision=2)
        self.release.set()
        self.assertEqual((await new.future)["revision"], 2)
        self.assertEqual(self.calls, [(1,True),(2,False)])

    async def test_newer_revision_cancels_queued_route_and_stale_cancel_is_harmless(self):
        async with self.shared.backend_gate:
            old = self.submit()
            new = self.submit(revision=2)
            self.assertFalse(self.room.cancel(LocomotionCancel(characterId="room",revision=1))["cancelled"])
        with self.assertRaises(ServiceError): await old.future
        self.assertEqual((await new.future)["revision"], 2)

    async def test_capacity_and_cancel_before_arrival(self):
        async with self.shared.backend_gate:
            a,b = self.submit("a"),self.submit("b")
            with self.assertRaises(ServiceError): self.submit("c")
            self.room.cancel(LocomotionCancel(characterId="never-arrived",revision=9))
            with self.assertRaises(ServiceError): self.submit("never-arrived",9)
        await a.future; await b.future

    async def test_upper_inference_blocks_room_and_cancel_never_unlocks_inflight(self):
        self.backend.release.clear()
        upper = self.shared.submit(upper_request())
        await self.entered_event(self.backend.entered)
        room = self.submit()
        await asyncio.sleep(.04)
        self.assertFalse(self.entered.is_set())
        self.shared.abort(upper, ServiceError(499,"cancelled","test"))
        await asyncio.sleep(.04)
        self.assertFalse(self.entered.is_set())
        self.backend.release.set()
        await room.future
        self.assertTrue(self.entered.is_set())

    async def test_room_inference_blocks_upper_and_cancel_never_unlocks_inflight(self):
        self.release.clear()
        room = self.submit()
        await self.entered_event(self.entered)
        upper = self.shared.submit(upper_request())
        self.room.abort(room, ServiceError(499,"cancelled","test"))
        await asyncio.sleep(.04)
        self.assertFalse(self.backend.entered.is_set())
        self.release.set()
        await upper.future

    async def test_expired_backend_wait_never_runs_generation(self):
        data = payload(); data["timeoutMs"] = 100
        async with self.shared.backend_gate:
            job = self.room.submit(LocomotionRequest(**data))
            await asyncio.sleep(.13)
        with self.assertRaises(ServiceError): await job.future
        self.assertEqual(self.calls, [])

    async def test_http_schema_and_identity_response(self):
        app = create_app(self.shared)
        # Explicit no-weight generator for HTTP testing; existing workers use the same lock.
        app.state.room_service.generator = self.room.generator
        await app.state.room_service.start()
        try:
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url="http://test") as client:
                bad = await client.post("/v1/locomotion/generate",json={})
                self.assertEqual(bad.status_code,400)
                response = await client.post("/v1/locomotion/generate",json=payload())
                self.assertEqual(response.status_code,200,response.text)
                self.assertEqual(response.json()["requestId"],"room-1")
        finally: await app.state.room_service.close()


if __name__ == "__main__": unittest.main()
