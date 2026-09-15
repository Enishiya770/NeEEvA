"""Real ASGI deadline paths with blocked fake resources; no model or network calls."""
from __future__ import annotations

import asyncio
import json
import threading
import unittest

import httpx

from Server.ARDY.motion_service.app import create_app
from Server.ARDY.motion_service.protocol import ServiceError
from Server.ARDY.motion_service.service import MotionService
from Server.ARDY.test_locomotion_stream import payload
from Server.ARDY.test_motion_service import FakeBackend, FakeProvider


class StreamDeadlineDiagnosticsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.shared = MotionService(FakeBackend(), FakeProvider())
        await self.shared.start()
        self.app = create_app(self.shared)
        self.stream = self.app.state.room_stream_service
        self.calls = []
        self.release = threading.Event()
        self.release.set()

        def generate(backend, request, embedding, context, cancelled):
            self.calls.append(request.requestId)
            self.release.wait(2)
            raise ServiceError(422, "controlled-generation-rejection", "Controlled rejection",
                               {"timings": {"qualityAttempts": 3}})

        self.stream.generator = generate
        await self.stream.start()
        self.client = httpx.AsyncClient(transport=httpx.ASGITransport(app=self.app), base_url="http://test")

    async def asyncTearDown(self):
        self.release.set()
        await self.client.aclose()
        await self.stream.close()
        await self.shared.close()

    async def request(self):
        value = payload()
        value["description"] = "PRIVATE_DESCRIPTION_NOT_FOR_DIAGNOSTIC_LOGS"
        value["timeoutMs"] = 100
        return await self.client.post("/v1/locomotion/stream/step", json=value)

    def diagnostic(self, response, stage, generated):
        self.assertEqual(response.status_code, 504, response.text)
        value = response.json()["error"]["details"]["streamDiagnostic"]
        self.assertEqual(value["stage"], stage)
        self.assertEqual(value["deadlineMs"], 100)  # No hidden timeout extension.
        self.assertEqual(value["generationStarted"], generated)
        self.assertEqual(value["requestId"], "stream-1-0")
        self.assertGreater(value["stageElapsedMs"][stage], 0)
        self.assertNotIn("PRIVATE_DESCRIPTION", response.text)
        self.assertNotIn("embedding", value)
        return value

    async def test_feature_wait_deadline_identifies_unresolved_feature_without_claiming_gpu_compute(self):
        async def blocked(*args):
            await asyncio.Event().wait()
        self.shared.provider.fetch = blocked
        with self.assertLogs("Server.ARDY.motion_service.locomotion_stream_service", level="WARNING") as logs:
            response = await self.request()
        value = self.diagnostic(response, "feature-wait", False)
        self.assertFalse(value["featureResolved"])
        self.assertEqual(self.calls, [])
        self.assertEqual(len(logs.output), 1)
        self.assertNotIn("PRIVATE_DESCRIPTION", logs.output[0])
        # Repeated HTTP/cancellation paths must not overwrite the first failure evidence.
        job = self.stream.states["stream"].head
        first = job.future.exception().payload()
        self.stream.abort(job, ServiceError(499, "client_disconnected", "Later cancellation"))
        self.assertEqual(job.future.exception().payload(), first)

    async def test_waiting_for_ardy_gate_is_distinct_from_feature_wait(self):
        async with self.shared.backend_gate:
            response = await self.request()
        value = self.diagnostic(response, "backend-gate", False)
        self.assertTrue(value["featureResolved"])
        self.assertEqual(self.calls, [])

    async def test_timeout_during_thread_generation_retains_actual_start_not_completed_motion(self):
        self.release.clear()
        response = await self.request()
        value = self.diagnostic(response, "generation", True)
        self.assertTrue(value["featureResolved"])
        self.assertEqual(self.calls, ["stream-1-0"])
        self.assertIsNone(self.stream.states["stream"].head.candidate)
        self.assertIn("does not prove", value["scope"])

    async def test_existing_quality_details_survive_additional_diagnostics(self):
        response = await self.request()
        self.assertEqual(response.status_code, 422, response.text)
        details = response.json()["error"]["details"]
        self.assertEqual(details, {"timings": {"qualityAttempts": 3}})
        self.assertNotIn("PRIVATE_DESCRIPTION", json.dumps(details))


if __name__ == "__main__":
    unittest.main()
