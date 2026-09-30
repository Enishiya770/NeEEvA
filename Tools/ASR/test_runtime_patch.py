"""Patch/replay safety tests: temporary source files only; no service/model calls."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import replay_saved_evidence as replay

spec = importlib.util.spec_from_file_location("asr_runtime_patch", Path(__file__).with_name("apply-runtime-code.py"))
updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(updater)


class RuntimeCodePatchTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name) / "neeeva-voice/asr"
        (self.root / "src").mkdir(parents=True)
        self.target = self.root / "src/sensevoice_server.py"
        self.target.write_bytes(b"VERSION = 1\n")
        self.staged = self.root / "candidate.py"
        self.staged.write_bytes(b"VERSION = 2\n")
        self.backup = self.root / ".runtime/code-backups/test"
        self.argv = ["patch", "--root", str(self.root), "--backup", str(self.backup), "--staged", str(self.staged),
                     "--before", updater.digest(self.target.read_bytes()), "--after", updater.digest(self.staged.read_bytes())]

    def test_inspect_never_modifies_source_or_creates_backup(self):
        with patch("sys.argv", self.argv):
            updater.main()
        self.assertEqual(self.target.read_bytes(), b"VERSION = 1\n")
        self.assertFalse(self.backup.exists())

    def test_apply_and_rollback_preserve_exact_bytes(self):
        with patch.object(updater, "require_stopped") as stopped, patch("sys.argv", self.argv + ["--apply"]):
            updater.main()
        stopped.assert_called_once()
        self.assertEqual(self.target.read_bytes(), self.staged.read_bytes())
        self.assertEqual((self.backup / "sensevoice_server.py").read_bytes(), b"VERSION = 1\n")
        with patch.object(updater, "require_stopped"), patch("sys.argv", ["patch", "--root", str(self.root), "--backup", str(self.backup), "--rollback", "--apply"]):
            updater.main()
        self.assertEqual(self.target.read_bytes(), b"VERSION = 1\n")

    def test_running_service_prevents_mutation(self):
        with patch.object(updater, "require_stopped", side_effect=RuntimeError("running")), patch("sys.argv", self.argv + ["--apply"]):
            with self.assertRaises(RuntimeError):
                updater.main()
        self.assertEqual(self.target.read_bytes(), b"VERSION = 1\n")
        self.assertFalse(self.backup.exists())

    def test_changed_source_fails_closed_before_backup(self):
        self.target.write_bytes(b"VERSION = 99\n")
        with patch("sys.argv", self.argv + ["--apply"]):
            with self.assertRaises(ValueError):
                updater.main()
        self.assertEqual(self.target.read_bytes(), b"VERSION = 99\n")
        self.assertFalse(self.backup.exists())


class ReplayEvidenceTests(unittest.TestCase):
    def test_missing_raw_vad_is_failure_not_observed_false(self):
        errors, _ = replay.evaluate({"kind": "weak_tonal"}, 200, {"speaker_learning_allowed": False})
        self.assertIn("raw_vad_not_observed", errors)

    def test_true_raw_vad_reports_review_not_evidence_success(self):
        errors, verdict = replay.evaluate({"kind": "weak_tonal"}, 200,
             {"raw_vad_observed": True, "raw_vad_has_speech": True, "speaker_learning_allowed": False})
        self.assertEqual(errors, [])
        self.assertEqual(verdict, "raw_vad_speech_observed_requires_review")

    def test_evidence_only_must_have_empty_formal_text(self):
        errors, _ = replay.evaluate({"kind": "weak_tonal"}, 200,
             {"raw_vad_observed": True, "raw_vad_has_speech": False, "speaker_learning_allowed": False, "input_status": "evidence_only", "text": "wrong"})
        self.assertIn("weak_absent_vad_became_formal_input", errors)

    def test_confirmed_evidence_has_no_words(self):
        errors, verdict = replay.evaluate({"kind": "weak_tonal"}, 200,
             {"raw_vad_observed": True, "raw_vad_has_speech": False, "speaker_learning_allowed": False, "input_status": "evidence_only", "text": ""})
        self.assertEqual(errors, [])
        self.assertEqual(verdict, "evidence_only_verified")


if __name__ == "__main__":
    unittest.main()
