"""CPU input-boundary checks for explicit initial history; no ARDY model load."""
import hashlib
import inspect
import json
from pathlib import Path
from types import SimpleNamespace
import unittest

import numpy as np
from pydantic import ValidationError
from scipy.spatial.transform import Rotation
import torch

from Server.ARDY.motion_service.backend import ArdyBackend, ROOT
from Server.ARDY.motion_service.protocol import GenerateRequest, InitialHistory
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY, load_core27_reference

REFERENCE = ROOT / "Tools/MotionAdapter/runtime/conditioning-history-before.npz"


class CaptureMotionRepresentation:
    """Capture the exact official representation input boundary and reconstruct FK."""
    def __init__(self, parents):
        self.parents = parents

    def __call__(self, local, root, **options):
        assert options == {"to_normalize": True, "to_canonicalize": False}
        self.local, self.root = local.clone(), root.clone()
        return local

    def inverse(self, value, **options):
        assert options == {"is_normalized": True}
        global_rot = value.clone()
        for joint, parent in enumerate(self.parents):
            if parent >= 0:
                global_rot[:, :, joint] = global_rot[:, :, parent] @ value[:, :, joint]
        return {"global_rot_mats": global_rot}


def backend():
    value = ArdyBackend("cpu")
    value.torch, value.device = torch, torch.device("cpu")
    lock = json.loads((ROOT / "Tools/MotionAdapter/upstream-lock.json").read_text(encoding="utf-8-sig"))
    value.names, value.parents, value.neutral, _ = load_core27_reference(ROOT / "Server/ARDY/vendor" / ("ardy-" + lock["ardy"]))
    value.model = SimpleNamespace(motion_rep=CaptureMotionRepresentation(value.parents))
    return value


def history(size=16, **extra):
    frames = []
    for frame in range(size):
        q = np.tile([0., 0., 0., 1.], (27, 1))
        for joint in range(1, 19):
            q[joint] = Rotation.from_euler("xyz", [joint+frame, frame*3-joint, joint*2-frame], degrees=True).as_quat()
        frames.append({"globalRotations": [dict(zip("xyzw", values)) for values in q]})
    return InitialHistory(kind="unity-upper-body-projection-v1", frames=frames, **extra)


def capture_legacy_reference():
    """Run once before the backend edit to bind unchanged default tensors."""
    if REFERENCE.exists():
        raise FileExistsError(REFERENCE)
    value = backend()
    arrays = {"project_history_source_sha256": np.asarray(hashlib.sha256(inspect.getsource(ArdyBackend.project_history).encode()).hexdigest())}
    for size in (1, 5, 16):
        value.project_history(history(size))
        arrays[f"local_{size}"] = value.model.motion_rep.local.numpy()
        arrays[f"root_{size}"] = value.model.motion_rep.root.numpy()
    np.savez(REFERENCE, **arrays)


class ConditioningHistoryTests(unittest.TestCase):
    def test_schema_defaults_and_strict_integer_lengths(self):
        self.assertEqual(history().conditioningFrames, 16)
        for count in (4, 8, 16):
            self.assertEqual(history(conditioningFrames=count).conditioningFrames, count)
        for count in (0, 2, -4, 12, 32, 4., 8.5, "4", None, True, False):
            with self.subTest(value=count), self.assertRaises(ValidationError):
                history(conditioningFrames=count)

    def test_default_actual_representation_inputs_equal_prechange_bytes(self):
        value = backend()
        with np.load(REFERENCE, allow_pickle=False) as reference:
            for size in (1, 5, 16):
                value.project_history(history(size))
                np.testing.assert_array_equal(value.model.motion_rep.local.numpy(), reference[f"local_{size}"])
                np.testing.assert_array_equal(value.model.motion_rep.root.numpy(), reference[f"root_{size}"])

    def test_latest_real_frames_and_truthful_projection_counts(self):
        value = backend()
        for size in (1, 5, 16):
            for count in (4, 8, 16):
                with self.subTest(supplied=size, conditioning=count):
                    supplied = history(size, conditioningFrames=count)
                    result, info = value.project_history(supplied)
                    self.assertEqual(result.shape[1], count)
                    self.assertEqual(value.model.motion_rep.root.shape, (1, count, 3))
                    self.assertEqual(info["suppliedFrames"], size)
                    self.assertEqual(info["usedFrames"], min(size, count))
                    self.assertEqual(info["discardedFrames"], max(0, size-count))
                    self.assertEqual(info["paddedFrames"], max(0, count-size))
                    self.assertEqual(info["conditioningFrames"], count)
                    self.assertLess(info["rotationRoundtripMaxAbs"], 1e-6)
                    decoded = value.model.motion_rep.inverse(result, is_normalized=True)["global_rot_mats"][0].numpy()
                    quats = [[[getattr(q, key) for key in "xyzw"] for q in frame.globalRotations] for frame in supplied.frames]
                    observed = Rotation.from_quat(np.asarray(quats).reshape(-1, 4)).as_matrix().reshape(size, 27, 3, 3)
                    observed = ARDY_TO_UNITY @ observed @ ARDY_TO_UNITY
                    used = min(size, count)
                    np.testing.assert_allclose(decoded[-used:], observed[-used:], atol=1e-6)
                    if count > size:
                        np.testing.assert_allclose(decoded[:count-size], np.repeat(observed[:1], count-size, axis=0), atol=1e-6)

    def test_continuation_cannot_supply_short_initial_history(self):
        for count in (4, 8, 16):
            with self.assertRaises(ValidationError):
                GenerateRequest(characterId="cpu", turnId="t", revision=1, requestId="r", chunkIndex=1,
                                description="A person raises both arms.", initialHistory=history(conditioningFrames=count))


if __name__ == "__main__":
    unittest.main()
