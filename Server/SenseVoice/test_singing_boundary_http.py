import asyncio
import hashlib
import io
import json
import unittest

import numpy as np
from starlette.datastructures import UploadFile

from asr_scheduler import AnalysisScheduler
from singing_boundary_http import BoundaryHttpService, install_boundary_routes
from test_singing_boundary_refinement import manifest, waveform


class BoundaryHttpTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.wav = waveform()
        self.data = b"fake-wav-envelope-for-transport-contract"
        self.manifest = manifest(audio_sha256=hashlib.sha256(self.data).hexdigest())
        self.calls = []
        self.scheduler = AnalysisScheduler()
        def recognize(piece, language):
            self.calls.append((len(piece), language))
            words = []
            if np.count_nonzero(piece == np.float32(.1)) > 4000:
                words.append(self.manifest["exclude_text"])
            if np.count_nonzero(piece == np.float32(.2)) > 4000:
                words.append(self.manifest["keep_text"])
            return dict(text=" ".join(words))
        self.service = BoundaryHttpService(lambda data: self.wav.copy(), recognize,
                                            self.scheduler, lambda: True)

    async def send(self, payload=None):
        upload = UploadFile(io.BytesIO(self.data), filename="test.wav")
        return await self.service.handle(upload, json.dumps(payload or self.manifest))

    async def test_exact_upload_and_source_echo_and_short_probes(self):
        result = await self.send()
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["audio_sha256"], hashlib.sha256(self.data).hexdigest())
        self.assertEqual(result["source_id"], self.manifest["source_id"])
        self.assertEqual(result["source_revision"], "r1")
        self.assertEqual(len(self.calls), 2)
        self.assertEqual(self.service.active, {})

    async def test_wrong_upload_hash_has_no_inference(self):
        result = await self.send(dict(self.manifest, audio_sha256="b" * 64))
        self.assertEqual(result.status_code, 400)
        self.assertEqual(json.loads(result.body)["reason"], "audio_hash_mismatch")
        self.assertEqual(self.calls, [])

    async def test_model_unavailable_is_explicit(self):
        self.service.ready = lambda: False
        result = await self.send()
        self.assertEqual(result.status_code, 503)
        self.assertEqual(json.loads(result.body)["reason"], "model_not_ready")

    async def test_cancel_before_upload_does_not_start_probe(self):
        self.scheduler.cancel(self.manifest["request_id"])
        result = await self.send()
        self.assertEqual(result["status"], "cancelled")
        self.assertIsNone(result["range"])
        self.assertEqual(self.calls, [])

    async def test_http_disconnect_never_authorizes_range(self):
        class Disconnected:
            async def is_disconnected(self):
                return True
        upload = UploadFile(io.BytesIO(self.data), filename="test.wav")
        result = await self.service.handle(upload, json.dumps(self.manifest), Disconnected())
        self.assertEqual(result["status"], "cancelled")
        self.assertEqual(self.calls, [])

    async def test_duplicate_id_preserves_original_active_state(self):
        state = dict(cancelled=False, child="")
        self.service.active[self.manifest["request_id"]] = state
        result = await self.send()
        self.assertEqual(result.status_code, 409)
        self.assertIs(self.service.active[self.manifest["request_id"]], state)
        self.assertFalse(state["cancelled"])

    async def test_no_more_than_two_active_requests(self):
        self.service.active.update({"one": {}, "two": {}})
        result = await self.send()
        self.assertEqual(result.status_code, 429)
        self.assertEqual(self.calls, [])

    async def test_invalid_json_returns_typed_failure(self):
        result = await self.service.handle(UploadFile(io.BytesIO(self.data)), "{bad")
        self.assertEqual(json.loads(result.body)["reason"], "invalid_manifest")
        self.assertEqual(self.calls, [])

    async def test_real_multipart_route_preserves_unicode_and_response_contract(self):
        from fastapi import FastAPI
        import httpx
        app = FastAPI()
        install_boundary_routes(app, self.service.decode, self.service.recognize,
                                self.scheduler, self.service.ready)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://fixture") as client:
            response = await client.post("/singing/refine-boundary",
                    data={"manifest": json.dumps(self.manifest, ensure_ascii=False)},
                    files={"audio_file": ("source.wav", self.data, "audio/wav")})
        self.assertEqual(response.status_code, 200)
        result = response.json()
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["status"], "resolved")
        self.assertEqual(result["evidence"][0]["removed_text"], self.manifest["exclude_text"])
        self.assertEqual(set(result["range"]), {"start_seconds", "end_seconds"})


if __name__ == "__main__":
    unittest.main()
