"""No model/HTTP tests for shared standing quality and bounded native retries."""
import asyncio
import json
from pathlib import Path
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np
from scipy.spatial.transform import Rotation

from Server.ARDY.motion_service.locomotion_backend import _sample_standing_window, generate_room_motion
from Server.ARDY.motion_service.locomotion_quality import standing_quality, QUALITY_SEED_STRIDE
from Server.ARDY.motion_service.locomotion_protocol import LocomotionRequest
from Server.ARDY.motion_service.locomotion_service import RoomLocomotionService
from Server.ARDY.motion_service.protocol import ServiceError
from Server.ARDY import test_locomotion_stream as stream_testfixtures
from Server.ARDY.test_room_locomotion import payload


class StandingWindowTests(unittest.TestCase):
    def fixture(self, modes, cancelled=None):
        return stream_testfixtures.NativePrefixTests().fixture(modes, cancelled)

    def sample(self, backend, history, condition, cancelled=None):
        return _sample_standing_window(backend, SimpleNamespace(seed=2147483640), history, condition,
            None, None, 56, cancelled or threading.Event(), .95, 0, None)

    def test_full_window_checks_tail_and_seam_from_retained_history(self):
        for bad in ("height", "root", "internal", "tail", "seam", "after-eight"):
            with self.subTest(bad=bad):
                backend, history, condition, calls = self.fixture([bad, "good"])
                before = history.clone()
                motion, decoded, candidates = self.sample(backend, history, condition)
                self.assertEqual(len(calls), 2)
                self.assertEqual(motion[0, -1, 0].item(), 155)
                self.assertTrue(history.equal(before))
                self.assertTrue(all(call["init_history_sequence"] is history for call in calls))
                self.assertTrue(all(call["text_feat"] is condition for call in calls))
                self.assertFalse(candidates[0]["accepted"])
                self.assertTrue(candidates[1]["accepted"])
                self.assertEqual(candidates[1]["seed"], (2147483640 + QUALITY_SEED_STRIDE) % 2147483648)
                self.assertEqual(candidates[1]["reasons"], [])
                self.assertEqual(decoded["root_positions"].shape[1], 56)

    def test_all_three_fail_without_advancing_or_repairing_history(self):
        backend, history, condition, calls = self.fixture(["height"])
        before = history.clone()
        with self.assertRaises(ServiceError) as caught:
            self.sample(backend, history, condition)
        self.assertEqual(caught.exception.status, 422)
        self.assertEqual(caught.exception.code, "locomotion_quality_rejected")
        records = caught.exception.details["timings"]["qualityCandidates"]
        self.assertEqual(len(records), 3)
        self.assertTrue(all(not record["accepted"] for record in records))
        self.assertTrue(all("standing_height" in [r["code"] for r in record["reasons"]] for record in records))
        self.assertTrue(history.equal(before))
        self.assertTrue(all(call["init_history_sequence"] is history for call in calls))

    def test_cancel_during_first_candidate_never_retries(self):
        cancelled = threading.Event()
        backend, history, condition, calls = self.fixture(["height"], cancelled)
        with self.assertRaises(ServiceError) as caught:
            self.sample(backend, history, condition, cancelled)
        self.assertEqual(caught.exception.code, "cancelled_revision")
        self.assertEqual(len(calls), 1)

    def run_full(self, backend, history, request, exporter):
        backend.adapter = lambda _: backend.torch.ones(1, 4096)
        with patch("Server.ARDY.motion_service.locomotion_backend.project_full_body", return_value=(history, {"fixture": True})), \
             patch("Server.ARDY.motion_service.locomotion_backend.constraint_tensors", return_value=(None, None, 56)), \
             patch("Server.ARDY.motion_service.locomotion_backend.export_locomotion", side_effect=exporter):
            return generate_room_motion(backend, request, np.ones(2048), threading.Event())

    def test_first_valid_full_window_is_exported_unchanged(self):
        backend, history, _, calls = self.fixture(["good"])
        exported = []
        def export(arrays, route, *args):
            exported.append(arrays)
            return {"frames": [], "source": args[-1]}
        result = self.run_full(backend, history, LocomotionRequest(**payload()), export)
        self.assertEqual(len(calls), 1)
        self.assertEqual(len(exported), 1)
        self.assertEqual(exported[0]["root_positions"].shape, (40, 3))
        np.testing.assert_allclose(exported[0]["root_positions"][:, 1], .95)
        np.testing.assert_allclose(exported[0]["root_positions"][:, 2], np.arange(16, 56)*.001, atol=1e-7)
        self.assertEqual(result["timings"]["qualityAttempts"], 1)
        self.assertFalse(result["source"]["naturalnessAccepted"])

    def test_later_rejected_window_cannot_export_partial_route(self):
        backend, history, _, calls = self.fixture(["good", "height", "height", "height"])
        data = payload(); data["targets"] += [dict(target) for target in data["targets"][-40:]]
        # Keep the later planned window stationary and continuous.
        for target in data["targets"][40:]:
            target["rootPosition"] = dict(data["targets"][39]["rootPosition"])
        with self.assertRaises(ServiceError) as caught:
            self.run_full(backend, history, LocomotionRequest(**data), lambda *args: self.fail("Rejected route was exported"))
        self.assertEqual(caught.exception.details["windowStartFrame"], 40)
        self.assertEqual(len(caught.exception.details["previousAcceptedWindows"]), 1)
        self.assertEqual(len(calls), 4)
        self.assertIsNot(calls[1]["init_history_sequence"], history)
        self.assertTrue(all(call["init_history_sequence"] is calls[1]["init_history_sequence"] for call in calls[1:]))

    def test_typed_service_error_preserves_bounded_quality_reasons(self):
        loop = asyncio.new_event_loop()
        try:
            job = SimpleNamespace(cancelled=threading.Event(), feature_task=None, future=loop.create_future())
            details = {"timings": {"qualityAttempts": 3}, "windowStartFrame": 0}
            RoomLocomotionService.abort(None, job, ServiceError(422, "locomotion_quality_rejected", "failed", details))
            self.assertEqual(job.future.exception().payload()["error"]["details"], details)
        finally:
            loop.close()

    def test_archived_real_crouch_is_rejected_without_reconstruction_adjustment(self):
        folder = Path(__file__).parent / "runtime/scene-view-position-20260914/native-r1/view-position-native"
        response_path = folder / "farther-move.response.json"
        if not response_path.is_file():
            self.skipTest("The optional preserved native-r1 artifact is unavailable")
        response = json.loads(response_path.read_text(encoding="utf-8-sig"))
        request = json.loads((folder / "farther-move.request.json").read_text(encoding="utf-8-sig"))
        def qrot(frames):
            q = np.asarray([[[q[k] for k in "xyzw"] for q in frame["globalRotations"]] for frame in frames])
            return Rotation.from_quat(q.reshape(-1, 4)).as_matrix().reshape(-1, 27, 3, 3)
        roots = np.asarray([[f["rootPosition"][k] for k in "xyz"] for f in response["frames"]])
        history = request["initialHistory"]["frames"]
        previous = np.asarray([history[-1]["rootPosition"][k] for k in "xyz"])
        reasons = standing_quality(roots, qrot(response["rotationClip"]["frames"]), previous, qrot(history)[-1], response["sourceRootHeight"])
        self.assertIn("standing_height", [reason["code"] for reason in reasons])
        self.assertLess(roots[-1, 1], response["sourceRootHeight"]*.72)


if __name__ == "__main__":
    unittest.main()
