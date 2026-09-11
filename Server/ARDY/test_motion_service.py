"""Concurrency and protocol regressions; no GPU or language model is loaded."""
from __future__ import annotations

import asyncio
import threading
import time
import unittest
from unittest.mock import patch

import httpx
import numpy as np
from pydantic import ValidationError

from Server.ARDY.motion_service.app import create_app
from Server.ARDY.motion_service.protocol import (CancelRequest, FEATURE_CONTRACT,
    FEATURE_TEMPLATE, GenerateRequest, InitialHistory, QWEN_MODEL_SHA256, ServiceError)
from Server.ARDY.motion_service.service import LiveFeatureProvider, MotionService


def request(character="neva", revision=1, chunk=0, **changes):
    values = dict(characterId=character, turnId="turn-1", revision=revision,
                  requestId=f"{character}-{revision}-{chunk}", chunkIndex=chunk,
                  description="A person gently waves while standing in place.")
    values.update(changes)
    return GenerateRequest(**values)


class FakeProvider:
    info = {"source": "test-fake", "verified": True}

    def __init__(self):
        self.calls = 0
        self.failure = None

    async def fetch(self, value, remaining):
        self.calls += 1
        if self.failure:
            raise self.failure
        return dict(embedding=np.ones(2048, dtype=np.float32), source="test-fake",
                    modelSha256=None, featureRequestId=value.requestId, featureContract=FEATURE_CONTRACT)


class FakeBackend:
    def __init__(self):
        self.info = {"ready": False, "languageModelsLoaded": False}
        self.calls = []
        self.entered = threading.Event()
        self.release = threading.Event()
        self.release.set()
        self.delay = 0

    def load(self):
        self.info["ready"] = True

    def generate(self, value, embedding, previous):
        self.entered.set()
        self.release.wait(3)
        time.sleep(self.delay)
        self.calls.append((value.characterId, value.revision, value.chunkIndex, previous.copy()))
        return dict(clip={"schema": 1, "frames": [{"index":i} for i in range(40)]},
                    history=f"{value.characterId}-{value.revision}-{value.chunkIndex}",
                    rng={"stream": value.characterId, "window": value.chunkIndex},
                    historySource="ardy-self-history-last-16" if previous else "ardy-cold-start-no-live-pose",
                    historyFrames=16 if previous else 0, generationMs=10)


class ServiceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.backend, self.provider = FakeBackend(), FakeProvider()
        self.service = MotionService(self.backend, self.provider, queue_limit=2, character_limit=4)
        await self.service.start()

    async def asyncTearDown(self):
        self.backend.release.set()
        await self.service.close()

    async def wait_entered(self):
        for _ in range(100):
            if self.backend.entered.is_set():
                return
            await asyncio.sleep(.005)
        self.fail("Backend did not start")

    async def test_three_chunks_have_private_history_and_live_condition_reuse(self):
        for chunk in range(3):
            result = await self.service.submit(request(chunk=chunk)).future
            self.assertEqual(result["startFrame"], chunk*40)
            self.assertEqual(result["newFrames"], 40)
            self.assertEqual(result["historyFrames"], 16 if chunk else 0)
            self.assertEqual(result["final"], chunk == 2)
            self.assertEqual(result["provenance"]["conditionReusedWithinRevision"], bool(chunk))
        self.assertEqual(self.provider.calls, 1)
        self.assertEqual(self.backend.calls[1][3]["history"], "neva-1-0")
        other = await self.service.submit(request(character="other")).future
        self.assertEqual(other["historyFrames"], 0)
        self.assertEqual(self.backend.calls[-1][3], {})

    async def test_success_idempotency_and_request_binding(self):
        first = await self.service.submit(request()).future
        second = await self.service.submit(request()).future
        self.assertIs(first, second)
        self.assertEqual(len(self.backend.calls), 1)
        with self.assertRaisesRegex(ServiceError, "different content"):
            self.service.submit(request(seed=2))
        with self.assertRaisesRegex(ServiceError, "Expected chunkIndex 1"):
            self.service.submit(request(chunk=2))
        with self.assertRaisesRegex(ServiceError, "new revision"):
            self.service.submit(request(chunk=1, description="A different action"))

    async def test_running_old_revision_cannot_publish_or_advance_new_rng(self):
        self.backend.release.clear()
        old = self.service.submit(request())
        await self.wait_entered()
        newer = self.service.submit(request(revision=2))
        with self.assertRaises(ServiceError):
            await old.future
        self.backend.release.set()
        result = await newer.future
        self.assertEqual(result["revision"], 2)
        self.assertEqual(self.backend.calls[-1][3], {})
        self.assertEqual(self.service.characters["neva"].backend_state["rng"]["window"], 0)
        self.assertEqual(self.service.counts["staleResultsDiscarded"], 1)
        with self.assertRaisesRegex(ServiceError, "newer character revision"):
            self.service.submit(request())

    async def test_cancel_during_inference_and_tombstone_before_generate(self):
        self.backend.release.clear()
        old = self.service.submit(request())
        await self.wait_entered()
        self.service.cancel(CancelRequest(characterId="neva", turnId="turn-1", revision=1))
        with self.assertRaises(ServiceError):
            await old.future
        with self.assertRaisesRegex(ServiceError, "cannot be restarted"):
            self.service.submit(request())
        self.service.cancel(CancelRequest(characterId="future", turnId="turn-1", revision=10))
        with self.assertRaisesRegex(ServiceError, "cannot be restarted"):
            self.service.submit(request(character="future", revision=10))
        self.backend.release.set()
        await asyncio.sleep(.04)
        self.assertEqual(self.service.characters["neva"].backend_state, {})

    async def test_queue_and_character_capacity_are_bounded(self):
        self.backend.release.clear()
        running = self.service.submit(request())
        await self.wait_entered()
        first = self.service.submit(request(character="b"))
        second = self.service.submit(request(character="c"))
        with self.assertRaisesRegex(ServiceError, "queue is full"):
            self.service.submit(request(character="d"))
        self.assertEqual(self.service.health()["queue"]["waiting"], 2)
        self.backend.release.set()
        await asyncio.gather(running.future, first.future, second.future)
        await self.service.submit(request(character="d")).future
        with self.assertRaisesRegex(ServiceError, "capacity reached"):
            self.service.submit(request(character="e"))

    async def test_timeout_and_feature_failure_do_not_commit(self):
        self.backend.delay = .13
        job = self.service.submit(request(timeoutMs=100))
        with self.assertRaisesRegex(ServiceError, "expired during generation"):
            await job.future
        state = self.service.characters["neva"]
        self.assertEqual(state.next_chunk, 0)
        self.assertEqual(state.backend_state, {})
        self.assertIsNone(state.feature)
        self.provider.failure = ServiceError(503, "unavailable", "Live Qwen unavailable")
        with self.assertRaisesRegex(ServiceError, "Live Qwen unavailable"):
            await self.service.submit(request(revision=2)).future
        self.assertEqual(len(self.backend.calls), 1)

    async def test_cancel_aborts_feature_wait_without_killing_worker(self):
        started = asyncio.Event()
        fetch = self.provider.fetch
        async def slow(value, remaining):
            if value.revision == 1:
                started.set()
                await asyncio.sleep(5)
            return await fetch(value, remaining)
        self.provider.fetch = slow
        old = self.service.submit(request())
        await started.wait()
        self.service.cancel(CancelRequest(characterId="neva", turnId="turn-1", revision=1))
        with self.assertRaises(ServiceError):
            await old.future
        result = await asyncio.wait_for(self.service.submit(request(revision=2)).future, 1)
        self.assertEqual(result["revision"], 2)
        self.assertEqual(len(self.backend.calls), 1)

    async def test_http_validation_cancel_and_deadline(self):
        app = create_app(self.service)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
            self.assertEqual((await client.get("/health")).status_code, 200)
            self.assertEqual((await client.post("/v1/motion/generate", json={"mask":"LeftArm"})).status_code, 400)
            self.assertEqual((await client.post("/v1/motion/generate", content=b" "*131073)).status_code, 413)
            self.backend.delay = .13
            response = await client.post("/v1/motion/generate", json=request(timeoutMs=100).model_dump())
            self.assertEqual(response.status_code, 504)
            self.assertEqual(self.service.characters["neva"].next_chunk, 0)
            response = await client.post("/v1/motion/cancel", json=dict(characterId="neva",turnId="turn-1",revision=1,requestId="unity-cancel-request",reason="new speech"))
            self.assertEqual(response.status_code, 200)


class ProtocolTests(unittest.TestCase):
    def test_grounded_source_height_uses_feet_relative_to_hips(self):
        from Server.ARDY.motion_service.backend import grounded_source_root
        names = ["Hips", "LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase"]
        neutral = np.array([[0,0,0],[.1,-.9,0],[.1,-.95,.15],[-.1,-.9,0],[-.1,-.95,.15]])
        root, feet = grounded_source_root(neutral,names)
        self.assertAlmostEqual(root[1],.95)
        self.assertAlmostEqual(min(feet.values()),0)
        self.assertTrue(all(y >= 0 for y in feet.values()))
        # A translated reference asset must yield the same relative body height.
        shifted, shifted_feet = grounded_source_root(neutral+[3,7,2],names)
        np.testing.assert_allclose(root,shifted,atol=1e-12)
        np.testing.assert_allclose(list(feet.values()),list(shifted_feet.values()),atol=1e-12)
        with self.assertRaises(ValueError):
            grounded_source_root(np.zeros_like(neutral),names)

    def test_projected_history_rejects_fake_leg_observations_and_bad_quaternions(self):
        identity = dict(x=0,y=0,z=0,w=1)
        frames = [{"globalRotations": [identity.copy() for _ in range(27)]}]
        value = InitialHistory(kind="unity-upper-body-projection-v1",frames=frames)
        self.assertEqual(len(value.frames), 1)
        frames[0]["globalRotations"][19] = dict(x=0,y=.1,z=0,w=(.99)**.5)
        with self.assertRaises(ValidationError):
            InitialHistory(kind="unity-upper-body-projection-v1",frames=frames)
        with self.assertRaises(ValidationError):
            request(chunk=1, initialHistory=value)
        with self.assertRaises(ValidationError):
            request(description=" ")


class FeatureTests(unittest.IsolatedAsyncioTestCase):
    async def test_live_response_requires_request_binding_model_and_raw_contract(self):
        req = request()
        valid = dict(embedding=[1.0]*2048, dimension=2048, feature_contract=FEATURE_CONTRACT,
            protocol="qwen-motion-raw-last-v1", feature_source="live-qwen", request_id=req.requestId,
            text=req.description, pooling="last_input_token_raw", template=FEATURE_TEMPLATE,
            shared_model=True, model_sha256=QWEN_MODEL_SHA256, context_tokens=512)
        original_client = httpx.AsyncClient
        value = valid.copy()
        transport = httpx.MockTransport(lambda r: httpx.Response(200, json=value))
        with patch("Server.ARDY.motion_service.service.httpx.AsyncClient", side_effect=lambda **kw: original_client(transport=transport, **kw)):
            provider = LiveFeatureProvider()
            result = await provider.fetch(req, 1)
            self.assertEqual(result["source"], "live-qwen")
            for key, bad in (("text", "different"), ("request_id", "wrong"), ("model_sha256", "f"*64),
                             ("feature_source", "cache"), ("embedding", [0.0]*2048), ("dimension", 4096),
                             ("shared_model", False), ("pooling", "mean")):
                value = {**valid, key: bad}
                with self.assertRaises(ServiceError, msg=key):
                    await provider.fetch(req, 1)


if __name__ == "__main__":
    unittest.main()
