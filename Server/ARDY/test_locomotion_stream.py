"""No-weight stream protocol, prefix acknowledgements, cancellation and gate tests."""
from __future__ import annotations

import asyncio
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import httpx
import numpy as np
from pydantic import ValidationError

from Server.ARDY.motion_service.app import create_app
from Server.ARDY.motion_service.locomotion_protocol import CORE_NAMES, FullBodyFrame
from Server.ARDY.motion_service.locomotion_stream_protocol import StreamStep, StreamCancel
from Server.ARDY.motion_service.locomotion_stream_service import RoomStreamService
from Server.ARDY.motion_service.protocol import ServiceError
from Server.ARDY.motion_service.service import MotionService
from Server.ARDY.test_motion_service import FakeBackend, FakeProvider, request as upper_request
from Server.ARDY.test_room_locomotion import payload as room_payload


def payload(step=0, revision=1, character="stream", commit=8):
    base = room_payload(character, revision)
    value = dict(schema=1, characterId=character, revision=revision, sessionId=f"session-{revision}",
        requestId=f"{character}-{revision}-{step}", stepIndex=step, startFrame=step*commit,
        controlRevision=step, commitFrames=commit,
        targets=[dict(rootPosition=dict(x=0,y=0,z=(step*commit+i)*.02), headingDegrees=0) for i in range(40)])
    if step == 0:
        value.update(description="A person walks naturally, continuously following a path.", initialHistory=base["initialHistory"])
    else:
        value["previousRequestId"] = f"{character}-{revision}-{step-1}"
    return value


class ProtocolTests(unittest.TestCase):
    def test_unity_empty_optional_strings_keep_first_step_and_ack_semantics(self):
        value = payload(); value["previousRequestId"] = ""
        self.assertIsNone(StreamStep(**value).previousRequestId)
        value = payload(1); value["description"] = ""
        self.assertIsNone(StreamStep(**value).description)
        value["previousRequestId"] = ""
        with self.assertRaises(ValidationError): StreamStep(**value)

    def test_continuous_end_and_full_token_aligned_lookahead_are_allowed(self):
        value = payload()
        request = StreamStep(**value)
        self.assertNotEqual(request.targets[-1].rootPosition.z, request.targets[-20].rootPosition.z)
        value["targets"] += [value["targets"][-1]] * 144
        self.assertEqual(len(StreamStep(**value).targets), 184)

    def test_missing_history_replaced_history_initial_ack_and_invalid_bounds_are_rejected(self):
        mutations = []
        for field, value in [("commitFrames", True), ("commitFrames", 8.), ("commitFrames", 40),
                             ("startFrame", 8), ("description", " "), ("initialHistory", None),
                             ("previousRequestId", "unexpected")]:
            d = payload(); d[field] = value; mutations.append(d)
        d = payload(1); d["initialHistory"] = payload()["initialHistory"]; mutations.append(d)
        d = payload(1); del d["previousRequestId"]; mutations.append(d)
        d = payload(); d["targets"][1]["rootPosition"]["z"] = 1; mutations.append(d)
        d = payload(); d["targets"][1]["rootPosition"]["y"] = .1; mutations.append(d)
        d = payload(); d["targets"][1]["headingDegrees"] = 20; mutations.append(d)
        d = payload(); d["targets"].pop(); mutations.append(d)
        for i, d in enumerate(mutations):
            with self.subTest(case=i), self.assertRaises(ValidationError): StreamStep(**d)


class StreamTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.backend, self.provider = FakeBackend(), FakeProvider()
        self.shared = MotionService(self.backend, self.provider)
        self.entered, self.release = threading.Event(), threading.Event()
        self.release.set()
        self.calls = []
        def generate(backend, request, embedding, context, cancel):
            self.entered.set()
            self.release.wait(3)
            self.calls.append(dict(revision=request.revision, step=request.stepIndex,
                history=context.get("history"), cancelled=cancel.is_set()))
            frame = request.startFrame+request.commitFrames-1
            seam = FullBodyFrame(**dict(rootPosition=dict(x=0,y=.95,z=frame*.02),
                globalRotations=[dict(x=0,y=0,z=0,w=1)]*27))
            return {"clip": {"schema":1,"frames":[{"frame":i} for i in range(40)],"targets":[]},
                "history": f"accepted-through-{frame}", "condition": "opaque-condition", "seam": seam}
        self.stream = RoomStreamService(self.shared, generate, queue_limit=2, character_limit=4)
        await self.shared.start()
        await self.stream.start()

    async def asyncTearDown(self):
        self.release.set()
        self.backend.release.set()
        await self.stream.close()
        await self.shared.close()

    async def entered_event(self, event):
        for _ in range(200):
            if event.is_set(): return
            await asyncio.sleep(.005)
        self.fail("inference did not start")

    def submit(self, step=0, **changes):
        data = payload(step)
        data.update(changes)
        return self.stream.submit(StreamStep(**data))

    async def test_features_reused_with_original_provenance_and_only_prefix_history(self):
        for step in range(4):
            result = await self.submit(step).future
            self.assertEqual(result["startFrame"], step*8)
            self.assertEqual(result["generationFrames"], 40)
            self.assertEqual(result["nextStartFrame"], (step+1)*8)
            self.assertTrue(result["requiresClientAudit"])
            self.assertEqual(result["source"]["featureRequestId"], "stream-1-0")
            self.assertEqual(result["source"]["conditionReusedWithinSession"], bool(step))
            if step: self.assertEqual(result["timings"]["featureMs"], 0)
        self.assertEqual(self.provider.calls, 1)
        self.assertEqual([x["history"] for x in self.calls], [None, "accepted-through-7", "accepted-through-15", "accepted-through-23"])
        state = self.stream.states["stream"]
        self.assertEqual(state.head.candidate["history"], "accepted-through-31")
        self.assertEqual(state.head.context, {})

    async def test_exact_replay_is_idempotent_and_changed_request_is_rejected(self):
        first = self.submit()
        result = await first.future
        self.assertIs(self.submit(), first)
        self.assertIs(self.submit().future.result(), result)
        with self.assertRaises(ServiceError): self.submit(controlRevision=1)
        self.assertEqual(len(self.calls), 1)

    async def test_out_of_order_wrong_ack_frame_text_seed_control_and_seam_rejected_without_advancing(self):
        await self.submit().future
        for change in [dict(stepIndex=2), dict(previousRequestId="unknown"), dict(startFrame=40),
                       dict(description="A different action"), dict(seed=1), dict(sessionId="other"),
                       dict(targets=payload(8)["targets"]), dict(requestId="stream-1-0")]:
            with self.subTest(change=change), self.assertRaises(ServiceError): self.submit(1, **change)
        await self.submit(1, controlRevision=5).future
        with self.assertRaises(ServiceError): self.submit(2, controlRevision=4)
        self.assertEqual(len(self.calls), 2)

    async def test_next_step_while_previous_running_never_queues_or_restarts_feature(self):
        self.release.clear()
        first = self.submit()
        await self.entered_event(self.entered)
        with self.assertRaises(ServiceError): self.submit(1)
        self.assertIs(self.submit(), first)
        self.release.set()
        await first.future
        await self.submit(1).future
        self.assertEqual(self.provider.calls, 1)

    async def test_cancel_running_discards_late_candidate_and_new_session_uses_new_feature(self):
        self.release.clear()
        old = self.submit()
        await self.entered_event(self.entered)
        self.stream.cancel(StreamCancel(characterId="stream", revision=1, sessionId="session-1"))
        with self.assertRaises(ServiceError): await old.future
        with self.assertRaises(ServiceError): self.submit()
        new = self.stream.submit(StreamStep(**payload(revision=2)))
        self.release.set()
        result = await new.future
        self.assertEqual(result["revision"], 2)
        self.assertIsNone(old.candidate)
        self.assertEqual(self.calls[0]["cancelled"], True)
        self.assertEqual(self.calls[1]["history"], None)
        self.assertEqual(self.provider.calls, 2)

    async def test_new_revision_fences_old_inflight_and_wrong_session_cancel_is_harmless(self):
        self.release.clear()
        old = self.submit()
        await self.entered_event(self.entered)
        new = self.stream.submit(StreamStep(**payload(revision=2)))
        with self.assertRaises(ServiceError): await old.future
        self.assertFalse(self.stream.cancel(StreamCancel(characterId="stream", revision=1, sessionId="session-1"))["cancelled"])
        with self.assertRaises(ServiceError):
            self.stream.cancel(StreamCancel(characterId="stream", revision=2, sessionId="other"))
        self.release.set()
        self.assertEqual((await new.future)["revision"], 2)

    async def test_cancelled_or_disconnected_candidate_cannot_be_acknowledged(self):
        first = self.submit(); await first.future
        self.stream.abort(first, ServiceError(499, "client_disconnected", "test"))
        with self.assertRaises(ServiceError): self.submit(1)

    async def test_cancel_stream_keeps_gpu_gate_until_inflight_finishes(self):
        self.release.clear()
        first = self.submit()
        await self.entered_event(self.entered)
        upper = self.shared.submit(upper_request())
        self.stream.cancel(StreamCancel(characterId="stream", revision=1, sessionId="session-1"))
        await asyncio.sleep(.04)
        self.assertFalse(self.backend.entered.is_set())
        self.release.set()
        await upper.future
        self.assertIsNone(first.candidate)

    async def test_expired_gate_wait_never_generates_and_does_not_advance_history(self):
        async with self.shared.backend_gate:
            job = self.submit(timeoutMs=100)
            await asyncio.sleep(.13)
        with self.assertRaises(ServiceError): await job.future
        with self.assertRaises(ServiceError): self.submit(1)
        self.assertEqual(self.calls, [])

    async def test_quality_failure_preserves_diagnostics_without_acknowledgeable_history(self):
        await self.submit().future
        original_history = self.stream.states["stream"].head.candidate["history"]
        diagnostics = {"timings":{"qualityAttempts":3,"qualityCandidates":[{"accepted":False}]*3}}
        def reject(backend, request, embedding, context, cancelled):
            self.assertEqual(context["history"],original_history)
            raise ServiceError(422,"stream_quality_rejected","All candidates failed",diagnostics)
        self.stream.generator = reject
        failed = self.submit(1)
        with self.assertRaises(ServiceError) as caught: await failed.future
        self.assertEqual(caught.exception.details,diagnostics)
        self.assertIsNone(failed.candidate)
        self.assertEqual(failed.context["history"],original_history)
        self.assertEqual(self.provider.calls,1)
        with self.assertRaises(ServiceError): self.submit(2)

    async def test_queue_and_tombstones_are_bounded(self):
        async with self.shared.backend_gate:
            jobs = [self.stream.submit(StreamStep(**payload(character=c))) for c in ("a", "b")]
            with self.assertRaises(ServiceError): self.stream.submit(StreamStep(**payload(character="c")))
            self.stream.cancel(StreamCancel(characterId="never",revision=2,sessionId="never-session"))
            self.stream.cancel(StreamCancel(characterId="last",revision=2,sessionId="last-session"))
            with self.assertRaises(ServiceError):
                self.stream.cancel(StreamCancel(characterId="overflow",revision=2,sessionId="overflow-session"))
        await asyncio.gather(*(j.future for j in jobs))

    async def test_http_step_root_clip_response_and_cancel(self):
        app = create_app(self.shared)
        service = app.state.room_stream_service
        service.generator = self.stream.generator
        await service.start()
        try:
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url="http://test") as client:
                bad = await client.post("/v1/locomotion/stream/step",json={})
                self.assertEqual(bad.status_code,400)
                result = await client.post("/v1/locomotion/stream/step",json=payload())
                self.assertEqual(result.status_code,200,result.text)
                self.assertEqual(result.json()["streamProtocol"],"ardy-room-stream-v1")
                self.assertEqual(len(result.json()["frames"]),40)
                result = await client.post("/v1/locomotion/stream/step",json=payload(1))
                self.assertEqual(result.status_code,200,result.text)
                self.assertTrue(result.json()["source"]["conditionReusedWithinSession"])
                def rejected(*args):
                    raise ServiceError(422,"stream_quality_rejected","All candidates failed",{"timings":{"qualityAttempts":3}})
                service.generator = rejected
                failed = await client.post("/v1/locomotion/stream/step",json=payload(2))
                self.assertEqual(failed.status_code,422)
                self.assertEqual(failed.json()["error"]["code"],"stream_quality_rejected")
                self.assertEqual(failed.json()["error"]["details"]["timings"]["qualityAttempts"],3)
                result = await client.post("/v1/locomotion/stream/cancel",json=dict(characterId="stream",revision=1,sessionId="session-1"))
                self.assertEqual(result.status_code,200)
                again = await client.post("/v1/locomotion/stream/step",json=payload(2))
                self.assertEqual(again.status_code,409)
        finally:
            await service.close()


class NativePrefixTests(unittest.TestCase):
    @staticmethod
    def neutral():
        value = np.zeros((27, 3), dtype=np.float32)
        for name in ("LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase"):
            value[CORE_NAMES.index(name), 1] = -.95
        return value

    def test_one_h40_step_retains_original_history_and_only_selected_new_prefix(self):
        import torch
        from Server.ARDY.motion_service.locomotion_stream_backend import generate_stream_step
        history = torch.arange(16, dtype=torch.float32).reshape(1,16,1)
        calls = []
        def step(**kwargs):
            calls.append(kwargs)
            # Deliberately altered decoded past must never replace the actual retained history.
            return torch.cat((kwargs["init_history_sequence"]+1000, torch.arange(16,56,dtype=torch.float32).reshape(1,40,1)),1)
        def inverse(motion, **kwargs):
            length = motion.shape[1]
            root = torch.zeros(1,length,3); root[:,:,1] = .95; root[:,:,2] = motion[0,:,0]*.001
            return dict(root_positions=root, global_rot_mats=torch.eye(3).reshape(1,1,1,3,3).repeat(1,length,27,1,1))
        model = SimpleNamespace(gen_horizon_len=40, motion_rep=SimpleNamespace(fps=20,inverse=inverse),
            diffusion=SimpleNamespace(num_base_steps=10), autoregressive_step=step)
        backend = SimpleNamespace(torch=torch, device=torch.device("cpu"), model=model,
            adapter=lambda x: torch.ones(1,4096), names=list(CORE_NAMES), neutral=self.neutral(),
            parents=[], lock={"ardy":"test"}, adapter_sha="test")
        def export(arrays, route, backend, clip_id, source):
            return dict(frames=[dict(rootPosition=dict(zip("xyz",map(float,p)))) for p in arrays["root_positions"]], source=source)
        with patch("Server.ARDY.motion_service.locomotion_stream_backend.project_full_body", return_value=(history,{"kind":"fixture"})) as project, \
             patch("Server.ARDY.motion_service.locomotion_stream_backend.constraint_tensors", return_value=(None,None,56)), \
             patch("Server.ARDY.motion_service.locomotion_stream_backend.export_locomotion", side_effect=export):
            first = generate_stream_step(backend, StreamStep(**payload()), np.ones(2048), {"description":"walk"}, threading.Event())
            self.assertEqual(len(calls),1)
            self.assertEqual(first["history"].flatten().tolist(), list(range(8,24)))
            self.assertEqual(len(first["clip"]["frames"]),40)
            self.assertAlmostEqual(first["seam"].rootPosition.z,.023,places=6)
            # Session continuation reuses the exact previous condition and history;
            # it must not silently project a new pose or re-run the adapter.
            backend.adapter = lambda x: self.fail("Continuation recalculated the cached condition")
            continued = generate_stream_step(backend, StreamStep(**payload(1)), np.ones(2048),
                {"description":"walk", "history":first["history"], "condition":first["condition"]}, threading.Event())
            self.assertIs(calls[1]["init_history_sequence"], first["history"])
            self.assertEqual(project.call_count,1)
            self.assertIs(continued["condition"],first["condition"])
            # Prefix length sixteen keeps precisely the sixteen newly chosen frames.
            backend.adapter = lambda x: torch.ones(1,4096)
            second = generate_stream_step(backend, StreamStep(**payload(commit=16)), np.ones(2048), {"description":"walk"}, threading.Event())
            self.assertEqual(second["history"].flatten().tolist(), list(range(16,32)))

    def fixture(self, modes, cancelled=None):
        import torch
        from scipy.spatial.transform import Rotation
        calls = []
        history = torch.arange(16, dtype=torch.float32).reshape(1,16,1)
        condition = torch.ones(1,1,4096)
        def step(**kwargs):
            calls.append(dict(kwargs, seed=torch.initial_seed()))
            if cancelled is not None:
                cancelled.set()
            # Encode attempt identity in the new tensor; projected geometry below
            # removes this marker. A successful retry's history must carry it.
            future = torch.arange(16,56,dtype=torch.float32).reshape(1,40,1)+(len(calls)-1)*100
            return torch.cat((kwargs["init_history_sequence"]+1000, future), 1)
        def inverse(motion, **kwargs):
            length = motion.shape[1]
            root = torch.zeros(1,length,3); root[:,:,1] = .95; root[:,:,2] = motion[0,:,0].remainder(100)*.001
            rotations = torch.eye(3).reshape(1,1,1,3,3).repeat(1,length,27,1,1)
            if length == 56:
                mode = modes[min(len(calls)-1, len(modes)-1)]
                turn = torch.tensor(Rotation.from_euler("x",60,degrees=True).as_matrix(), dtype=torch.float32)
                toe = CORE_NAMES.index("RightToeBase")
                if mode == "seam":
                    # The reconstructed past pretends the toe was already turned;
                    # comparison must instead use original retained history.
                    rotations[0,15:,toe] = turn
                elif mode == "internal": rotations[0,19,toe] = turn
                elif mode == "root": root[0,16,0] = .2
                elif mode == "height": root[0,16:24,1] = .5
                elif mode == "tail":
                    root[0,30,1] = .1; rotations[0,30,toe] = turn
                elif mode == "after-eight": rotations[0,24,toe] = turn
                elif mode == "auxiliary":
                    for name in ("Spine1","RightHandEnd","RightHandThumb1","LeftHandEnd","LeftHandThumb1"):
                        rotations[0,16:,CORE_NAMES.index(name)] = turn
            return dict(root_positions=root, global_rot_mats=rotations)
        backend = SimpleNamespace(torch=torch, device=torch.device("cpu"),
            model=SimpleNamespace(gen_horizon_len=40, motion_rep=SimpleNamespace(fps=20,inverse=inverse),
                diffusion=SimpleNamespace(num_base_steps=10), autoregressive_step=step),
            adapter=lambda x:self.fail("Cached condition must be reused across attempts"),
            names=list(CORE_NAMES), parents=[], neutral=self.neutral(), lock={"ardy":"test"}, adapter_sha="test")
        return backend, history, condition, calls

    def generate_fixture(self, backend, history, condition, request=None, cancelled=None):
        from Server.ARDY.motion_service.locomotion_stream_backend import generate_stream_step
        def export(arrays, route, backend, clip_id, source):
            return dict(frames=[dict(rootPosition=dict(zip("xyz",map(float,p)))) for p in arrays["root_positions"]], source=source)
        with patch("Server.ARDY.motion_service.locomotion_stream_backend.project_full_body", side_effect=AssertionError("History was reprojected")), \
             patch("Server.ARDY.motion_service.locomotion_stream_backend.constraint_tensors", return_value=(None,None,56)), \
             patch("Server.ARDY.motion_service.locomotion_stream_backend.export_locomotion", side_effect=export):
            return generate_stream_step(backend, request or StreamStep(**payload()), None,
                {"description":"walk", "history":history, "condition":condition}, cancelled or threading.Event())

    def test_bad_seam_retries_with_same_real_history_condition_and_only_good_prefix_retained(self):
        from Server.ARDY.motion_service.locomotion_stream_backend import QUALITY_SEED_STRIDE
        backend, history, condition, calls = self.fixture(["seam", "good"])
        before = history.clone()
        request = StreamStep(**dict(payload(),seed=2147483640))
        result = self.generate_fixture(backend, history, condition, request)
        self.assertEqual(len(calls), 2)
        self.assertEqual([c["seed"] for c in calls], [2147483640, (2147483640+QUALITY_SEED_STRIDE)%2147483648])
        self.assertTrue(all(c["init_history_sequence"] is history and c["text_feat"] is condition for c in calls))
        self.assertTrue(history.equal(before))
        self.assertEqual(result["history"].flatten().tolist(), list(range(8,16))+list(range(116,124)))
        self.assertIs(result["condition"], condition)
        timings = result["clip"]["timings"]
        self.assertEqual(timings["qualityAttempts"], 2)
        self.assertEqual(len(timings["windowsMs"]), 2)
        self.assertEqual(timings["qualityCandidates"][0]["reasons"][0]["joint"], "RightToeBase")
        self.assertEqual(timings["qualityCandidates"][0]["reasons"][0]["frame"], 0)
        self.assertEqual(timings["qualityCandidates"][1]["reasons"], [])

    def test_internal_joint_root_height_and_sixteen_frame_prefix_failures_are_retried(self):
        cases = [("internal",8,"joint_step"),("root",8,"root_step"),("height",8,"standing_height"),
                 ("after-eight",16,"joint_step")]
        for mode, commit, reason in cases:
            with self.subTest(mode=mode):
                backend, history, condition, calls = self.fixture([mode, "good"])
                result = self.generate_fixture(backend,history,condition,StreamStep(**payload(commit=commit)))
                self.assertEqual(len(calls),2)
                self.assertIn(reason,[r["code"] for r in result["clip"]["timings"]["qualityCandidates"][0]["reasons"]])
                if mode == "after-eight":
                    self.assertEqual(result["history"].flatten().tolist(),list(range(116,132)))

    def test_auxiliary_joints_and_uncommitted_tail_do_not_reject_a_valid_prefix(self):
        for mode in ["auxiliary", "tail", "after-eight"]:
            with self.subTest(mode=mode):
                backend, history, condition, calls = self.fixture([mode])
                result = self.generate_fixture(backend,history,condition)
                self.assertEqual(len(calls),1)
                self.assertEqual(result["clip"]["timings"]["qualityAttempts"],1)

    def test_all_candidates_rejected_typed_error_contains_bounded_reasons_and_history_unchanged(self):
        backend, history, condition, calls = self.fixture(["internal"])
        before = history.clone()
        with self.assertRaises(ServiceError) as caught:
            self.generate_fixture(backend,history,condition)
        self.assertEqual(caught.exception.status,422)
        self.assertEqual(caught.exception.code,"stream_quality_rejected")
        details = caught.exception.payload()["error"]["details"]["timings"]
        self.assertEqual(details["qualityAttempts"],3)
        self.assertEqual(len(details["qualityCandidates"]),3)
        self.assertEqual(len(calls),3)
        self.assertTrue(history.equal(before))
        self.assertTrue(all(c["init_history_sequence"] is history for c in calls))
        self.assertTrue(all(not c["accepted"] and c["reasons"][0]["frame"] == 3 for c in details["qualityCandidates"]))

    def test_cancellation_during_first_candidate_never_samples_retry(self):
        cancelled = threading.Event()
        backend, history, condition, calls = self.fixture(["seam"], cancelled)
        with self.assertRaises(ServiceError) as caught:
            self.generate_fixture(backend,history,condition,cancelled=cancelled)
        self.assertEqual(caught.exception.code,"cancelled_revision")
        self.assertEqual(len(calls),1)


if __name__ == "__main__": unittest.main()
