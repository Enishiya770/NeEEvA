"""Optional speaker inference cannot turn a valid full transcript into silence.

No model downloads/inference, service calls, voice-profile files or audio dumps.
Real final pipeline and identity guards execute around injected model boundaries.
"""
import json
import threading
import copy
from types import SimpleNamespace
import unittest
from contextlib import ExitStack
from unittest.mock import Mock, patch

import numpy as np
import sensevoice_server as server


class SpeakerFailureIsolationTests(unittest.TestCase):
    def setUp(self):
        self.store = Mock()
        self.store.identify_only.return_value = {
            "speaker_id": "fixture-owner", "speaker_kind": "owner",
            "speaker_status": "confirmed", "speaker_self_confidence": .01,
        }
        self.store.identify_and_learn.return_value = {
            "speaker_id": "fixture-owner", "speaker_kind": "owner", "speaker_status": "confirmed",
        }
        self.model = Mock()
        self.model.generate.return_value = [{"spk_embedding": np.ones(192, np.float32)}]

    def final(self, seconds=2.0, text="这段反馈必须完整保留，最后一句也在这里。", emotion="NEUTRAL", speech=True,
              analysis=None, expect_singing=False):
        wav = np.full(round(seconds * 16000), .1, np.float32)
        with ExitStack() as stack:
            for name, value in {"_speaker_model": self.model, "_speaker_store": self.store,
                                "_singing_analyzer": SimpleNamespace(analyze=lambda *a, **k: copy.deepcopy(analysis)) if analysis else None}.items():
                stack.enter_context(patch.object(server, name, value))
            stack.enter_context(patch.object(server, "decode_wav", return_value=wav))
            stack.enter_context(patch.object(server, "run_vad", return_value=(speech, round(seconds * 1000) if speech else 0,
                                                                             [[0, round(seconds * 1000)]], .001)))
            stack.enter_context(patch.object(server, "trim_to_speech_with_offset", return_value=(wav, 0.0)))
            recognizer = stack.enter_context(patch.object(server, "generate_final_asr", return_value=[{"text": text}]))
            stack.enter_context(patch.object(server, "parse_output", return_value=(text, "zh", emotion, "Speech")))
            stack.enter_context(patch.object(server, "analyze_singing_boundary_extras", return_value={}))
            stack.enter_context(patch.object(server, "transcribe_segments", return_value={}))
            stack.enter_context(patch.object(server, "transcribe_singing_segment", return_value={}))
            stack.enter_context(patch.object(server, "transcribe_singing_tail", return_value=""))
            stack.enter_context(patch.object(server, "build_song_recall", return_value={}))
            stack.enter_context(patch.object(server, "dump_mixed_sample"))
            stack.enter_context(patch.object(server, "dump_band_sample"))
            result = server.recognize_final_audio(b"isolated fixture", expect_singing=expect_singing)
            return result, recognizer, wav

    def test_observed_xbyak_failure_preserves_complete_long_asr_without_learning(self):
        self.model.generate.side_effect = RuntimeError("illegal immediate parameter (range error)")
        result, recognizer, wav = self.final(49.35)
        self.assertEqual(result["input_status"], "accepted")
        self.assertTrue(result["raw_vad_observed"])
        self.assertTrue(result["raw_vad_has_speech"])
        self.assertFalse(result["no_speech"])
        self.assertIn("最后一句也在这里", result["text"])
        self.assertIs(recognizer.call_args.args[0], wav, "Do not truncate ASR to fix speaker inference")
        self.assertEqual(wav.size, 789600)
        self.assertEqual(result["speaker_id"], "unknown")
        self.assertEqual(result["speaker_kind"], "unknown")
        self.assertEqual(result["speaker_status"], "error")
        self.assertEqual(result["speaker_voiceprint_status"], "error")
        self.assertEqual(result["speaker_error_code"], "speaker_inference_failed")
        self.assertFalse(result["speaker_verification_available"])
        self.assertFalse(result["speaker_learning_allowed"])
        self.store.auto_organize.assert_not_called()
        self.store.identify_only.assert_not_called()
        self.store.identify_and_learn.assert_not_called()

    def test_short_model_failure_has_same_unknown_contract(self):
        self.model.generate.side_effect = RuntimeError("backend operator failed")
        result, recognizer, _ = self.final(.8)
        self.assertEqual(result["input_status"], "accepted")
        self.assertFalse(result["speaker_verification_available"])
        self.assertEqual(result["speaker_confidence"], 0)
        recognizer.assert_called_once()

    def test_normal_identity_and_learning_still_work(self):
        result, recognizer, _ = self.final()
        self.assertEqual(result["speaker_id"], "fixture-owner")
        self.assertTrue(result["speaker_verification_available"])
        self.assertTrue(result["speaker_learning_allowed"])
        self.assertEqual(result["speaker_error_code"], "")
        self.store.identify_and_learn.assert_called_once()
        self.assertEqual(result["input_status"], "accepted")

    def test_ai_echo_still_rejects_before_transcription_or_learning(self):
        self.store.identify_only.return_value = {"speaker_id": "ai_self", "speaker_kind": "ai", "speaker_confidence": .99}
        result, recognizer, _ = self.final()
        self.assertEqual(result["no_speech_reason"], "self-speech")
        self.assertEqual(result["input_status"], "no_speech")
        self.assertEqual(result["audio_event"], "SelfSpeech")
        self.assertEqual(result["text"], "")
        recognizer.assert_not_called()
        self.store.identify_and_learn.assert_not_called()

    def test_read_only_identity_does_not_organize_or_learn_profiles(self):
        with patch.object(server, "_speaker_store", self.store), patch.object(server, "_speaker_model", self.model):
            result, _, pending = server.identify_speaker(np.ones(32000, np.float32), 2000, learn=False)
        self.assertEqual(result["speaker_id"], "fixture-owner")
        self.assertTrue(result["speaker_verification_available"])
        self.assertFalse(result["speaker_learning_allowed"])
        self.assertIsNone(pending)
        self.store.identify_only.assert_called_once()
        self.store.auto_organize.assert_not_called()
        self.store.identify_and_learn.assert_not_called()

    def test_weak_fragment_does_not_gain_owner_exemption_on_speaker_error(self):
        self.model.generate.side_effect = RuntimeError("illegal immediate parameter (range error)")
        result, _, _ = self.final(text="I.", emotion="EMO_UNKNOWN")
        self.assertTrue(result["no_speech"])
        self.assertEqual(result["input_status"], "no_speech")
        self.assertEqual(result["no_speech_reason"], "hallucination-rejected")
        self.assertEqual(result["text"], "")
        self.assertEqual(result["rejected_text"], "I.")
        self.assertEqual(result["speaker_error_code"], "speaker_inference_failed")
        self.store.identify_and_learn.assert_not_called()

    def test_vad_silence_still_precedes_optional_speaker(self):
        self.model.generate.side_effect = RuntimeError("should not run")
        result, recognizer, _ = self.final(speech=False)
        self.assertEqual(result["input_status"], "no_speech")
        self.assertEqual(result["no_speech_reason"], "vad-insufficient-speech")
        self.model.generate.assert_not_called()
        recognizer.assert_not_called()

    def test_unknown_model_program_exception_remains_real_error_not_silence(self):
        self.model.generate.side_effect = TypeError("fixture programming bug")
        result, recognizer, _ = self.final()
        self.assertEqual(result.status_code, 500)
        body = json.loads(result.body)
        self.assertEqual(body["input_status"], "error")
        self.assertEqual(body["input_error_code"], "asr_pipeline_failed")
        self.assertEqual(body["input_error_stage"], "speaker_identity")
        self.assertFalse(body["no_speech"])
        self.assertFalse(body["input_error_retryable"])
        recognizer.assert_not_called()

    def test_identity_store_failure_is_not_mislabeled_as_optional_model_failure(self):
        self.store.identify_only.side_effect = RuntimeError("repository invariant")
        result, recognizer, _ = self.final()
        body = json.loads(result.body)
        self.assertEqual(result.status_code, 500)
        self.assertEqual(body["input_status"], "error")
        self.assertEqual(body["input_error_stage"], "speaker_identity")
        self.assertFalse(body["no_speech"])
        recognizer.assert_not_called()
        self.store.identify_and_learn.assert_not_called()

    def test_failure_releases_speaker_lock_and_next_request_recovers(self):
        lock = threading.Lock()
        self.model.generate.side_effect = [RuntimeError("operator failed"), [{"spk_embedding": np.ones(192, np.float32)}]]
        with patch.object(server, "_speaker_model_lock", lock):
            first, _, _ = self.final()
            self.assertTrue(lock.acquire(blocking=False))
            lock.release()
            second, _, _ = self.final()
        self.assertEqual(first["speaker_status"], "error")
        self.assertEqual(second["speaker_id"], "fixture-owner")
        self.store.identify_and_learn.assert_called_once()

    def test_learning_guard_rejects_injected_embedding_when_status_is_error(self):
        identity = server.unknown_speaker_meta()
        identity.update(speaker_status="error", speaker_error_code="speaker_inference_failed")
        with patch.object(server, "_speaker_store", self.store):
            self.assertIs(server.commit_speaker_learning(np.ones(192), 1000, identity), identity)
        self.store.identify_and_learn.assert_not_called()

    def test_invalid_embedding_is_unknown_and_not_learnable(self):
        self.model.generate.return_value = [{"spk_embedding": np.full(192, np.nan)}]
        result, _, _ = self.final()
        self.assertEqual(result["speaker_kind"], "unknown")
        self.assertFalse(result["speaker_verification_available"])
        self.store.identify_and_learn.assert_not_called()

    def test_structured_bad_audio_is_not_silence(self):
        with patch.object(server, "decode_wav", side_effect=ValueError("invalid fixture")):
            result = server.recognize_final_audio(b"invalid")
        body = json.loads(result.body)
        self.assertEqual(result.status_code, 400)
        self.assertEqual(body["input_error_code"], "invalid_audio")
        self.assertFalse(body["no_speech"])
        self.assertFalse(body["input_error_retryable"])


if __name__ == "__main__":
    unittest.main()
