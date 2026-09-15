"""Final rejection reasons must distinguish silence, self echo and text conflict."""
import unittest
from contextlib import ExitStack
from unittest.mock import patch
import numpy as np
import sensevoice_server as server


class ClosedSpeechRejectionTests(unittest.TestCase):
    def run_final(self, speech, speaker, text="停一下。"):
        with ExitStack() as stack:
            stack.enter_context(patch.object(server, "decode_wav", return_value=np.ones(24000, np.float32) * .1))
            stack.enter_context(patch.object(server, "run_vad", return_value=(speech, 480 if speech else 0, [[0, 900]], .01)))
            stack.enter_context(patch.object(server, "_singing_analyzer", None))
            stack.enter_context(patch.object(server, "identify_speaker", return_value=(speaker, .01, None)))
            stack.enter_context(patch.object(server, "generate_asr", return_value=[{"text": text}]))
            stack.enter_context(patch.object(server, "parse_output", return_value=(text, "zh", "EMO_UNKNOWN", "Speech")))
            learn = stack.enter_context(patch.object(server, "commit_speaker_learning"))
            result = server.recognize_final_audio(b"closed speech regression")
            self.assertEqual(learn.call_count, 0, "Rejected audio must not change speaker identities")
            return result

    def test_vad_rejection_retains_its_actual_reason(self):
        result = self.run_final(False, {"speaker_kind": "unknown"})
        self.assertTrue(result["no_speech"])
        self.assertEqual(result["no_speech_reason"], "vad-insufficient-speech")
        self.assertFalse(result["raw_vad_has_speech"])
        self.assertEqual(result["raw_vad_speech_ms"], 0)
        self.assertNotIn("rejected_text", result)

    def test_self_echo_is_never_mislabeled_as_recoverable_silence(self):
        result = self.run_final(True, {"speaker_kind": "ai", "speaker_confidence": .99})
        self.assertEqual(result["audio_event"], "SelfSpeech")
        self.assertEqual(result["no_speech_reason"], "self-speech")
        self.assertEqual(result["text"], "")
        self.assertNotIn("rejected_text", result)

    def test_exact_observed_failure_keeps_rejected_words_as_evidence(self):
        result = self.run_final(True, {"speaker_kind": "guest", "speaker_id": "test-guest"})
        self.assertEqual(result["no_speech_reason"], "hallucination-rejected")
        self.assertEqual(result["text"], "", "Rejected evidence is not ordinary final speech")
        self.assertEqual(result["rejected_text"], "停一下。")
        self.assertTrue(result["raw_vad_has_speech"])
        self.assertEqual(result["raw_vad_speech_ms"], 480)

    def test_negation_is_retained_verbatim_for_the_semantic_layer(self):
        result = self.run_final(True, {"speaker_kind": "guest"}, "不要停。")
        self.assertEqual(result["rejected_text"], "不要停。")
        self.assertEqual(result["text"], "")


if __name__ == "__main__":
    unittest.main()
