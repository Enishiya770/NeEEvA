import asyncio
import unittest

import numpy as np

from singing_boundary_refinement import (RATE, MAX_CANDIDATES, BoundaryRequest, BoundaryError,
                                        acoustic_gaps, refine_boundary)


def manifest(**changes):
    item = dict(schema=1, request_id="test-1", source_id="clip:fixed", source_revision="r1",
                audio_sha256="a" * 64, source_offset_seconds=3.23,
                current_start_seconds=3.23, current_end_seconds=7.43,
                edge="lead", exclude_text="接下来唱第二段喽", keep_text="初めてあなたを見た",
                played_revision="play-4", pcm_lineage_hash="lineage-3")
    item.update(changes)
    return item


def waveform():
    # Encoded synthetic regions test ownership/boundaries, not real ASR accuracy.
    return np.r_[np.full(RATE, .1), np.zeros(RATE // 5), np.full(3 * RATE, .2)].astype(np.float32)


class BoundaryTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.wav = waveform()
        self.request = BoundaryRequest.parse(manifest(), "a" * 64, len(self.wav))

    async def recognize(self, piece):
        words = []
        if np.count_nonzero(piece == np.float32(.1)) > RATE // 4:
            words.append(self.request.exclude_text)
        if np.count_nonzero(piece == np.float32(.2)) > RATE // 4:
            words.append(self.request.keep_text)
        return dict(text="。".join(words))

    async def test_resolved_lead_preserves_source_mapping_and_other_edge(self):
        result = await refine_boundary(self.wav, self.request, self.recognize)
        self.assertTrue(result["ok"], result)
        self.assertAlmostEqual(result["range"]["start_seconds"], 4.33, places=2)
        self.assertEqual(result["range"]["end_seconds"], 7.43)
        self.assertEqual(result["played_revision"], "play-4")
        self.assertEqual(result["pcm_lineage_hash"], "lineage-3")
        self.assertFalse(result["word_timestamps"])
        self.assertFalse(result["mode_verified"])
        self.assertEqual(result["asr_probes"], 2)

    async def test_tail_preserves_start_and_matches_suffix_anchor(self):
        self.wav = self.wav[::-1].copy()
        self.request = BoundaryRequest.parse(manifest(edge="tail"), "a" * 64, len(self.wav))
        result = await refine_boundary(self.wav, self.request, self.recognize)
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["range"]["start_seconds"], 3.23)
        self.assertAlmostEqual(result["range"]["end_seconds"], 6.33, places=2)

    async def test_continuous_audio_unresolved_without_asr_or_text_ratio(self):
        audio = np.full(len(self.wav), .1, dtype=np.float32)
        async def must_not_run(_):
            self.fail("No acoustic boundary must not be replaced with text proportional seconds")
        result = await refine_boundary(audio, self.request, must_not_run)
        self.assertEqual(result["reason"], "no_acoustic_gap")
        self.assertIsNone(result["range"])

    async def test_kept_first_words_cannot_be_dropped(self):
        calls = 0
        async def truncated(_):
            nonlocal calls
            calls += 1
            return dict(text=self.request.exclude_text if calls == 1 else "あなたを見た")
        result = await refine_boundary(self.wav, self.request, truncated)
        self.assertEqual(result["reason"], "anchor_not_supported")
        self.assertEqual(result["evidence"][0]["status"], "kept_anchor_not_intact")

    async def test_extra_removed_words_cannot_be_discarded(self):
        async def extra(_):
            return dict(text=self.request.exclude_text + " 初めて")
        result = await refine_boundary(self.wav, self.request, extra)
        self.assertFalse(result["ok"])
        self.assertEqual(result["asr_probes"], 1)

    async def test_cancel_between_probes_discards_all_ranges(self):
        state = [False]
        async def cancel(_):
            state[0] = True
            return dict(text=self.request.exclude_text)
        result = await refine_boundary(self.wav, self.request, cancel, lambda: state[0])
        self.assertEqual(result["status"], "cancelled")
        self.assertIsNone(result["range"])
        self.assertEqual(result["asr_probes"], 1)

    async def test_recognizer_failure_is_not_accepted_as_evidence(self):
        async def fail(_):
            raise RuntimeError("private infrastructure details")
        result = await refine_boundary(self.wav, self.request, fail)
        self.assertEqual(result["reason"], "analysis_failed")
        self.assertEqual(result["detail"], "RuntimeError")
        self.assertIsNone(result["range"])

    async def test_multiple_supported_gaps_remain_ambiguous(self):
        audio = np.r_[np.ones(RATE), np.zeros(RATE // 5), np.ones(RATE),
                      np.zeros(RATE // 5), np.ones(2 * RATE)].astype(np.float32) * .1
        request = BoundaryRequest.parse(manifest(current_end_seconds=7.63), "a" * 64, len(audio))
        count = 0
        async def nondiscriminating(_):
            nonlocal count
            count += 1
            return dict(text=request.exclude_text if count % 2 else request.keep_text)
        result = await refine_boundary(audio, request, nondiscriminating)
        self.assertEqual(result["reason"], "ambiguous_boundary")
        self.assertIsNone(result["range"])

    async def test_too_many_gaps_do_not_silently_select_unchecked_candidate(self):
        audio = np.r_[tuple(np.r_[np.ones(RATE // 3) * .1, np.zeros(RATE // 10)]
                             for _ in range(MAX_CANDIDATES + 2))].astype(np.float32)
        request = BoundaryRequest.parse(manifest(current_end_seconds=3.23 + len(audio) / RATE), "a" * 64, len(audio))
        async def mismatch(_):
            return dict(text="other words")
        result = await refine_boundary(audio, request, mismatch)
        self.assertEqual(result["reason"], "analysis_budget_exhausted")
        self.assertLessEqual(result["asr_probes"], MAX_CANDIDATES)

    async def test_equivalent_quiet_transition_chooses_least_destructive_cut(self):
        audio = np.r_[np.full(RATE, .1), np.zeros(RATE // 10),
                      np.full(RATE // 10, .016), np.zeros(RATE // 10), np.full(2 * RATE, .1)].astype(np.float32)
        request = BoundaryRequest.parse(manifest(current_end_seconds=3.23 + len(audio) / RATE), "a" * 64, len(audio))
        count = 0
        async def same_words(_):
            nonlocal count
            count += 1
            return dict(text=request.exclude_text if count % 2 else request.keep_text)
        result = await refine_boundary(audio, request, same_words)
        self.assertTrue(result["ok"], result)
        self.assertEqual(result["supported_gap_count"], 2)
        self.assertLess(result["range"]["start_seconds"], 4.4)
        self.assertGreater(result["boundary_uncertainty"]["end_seconds"], 4.4)

    async def test_close_boundaries_with_audible_bridge_are_not_coalesced(self):
        audio = np.r_[np.full(RATE, .1), np.zeros(RATE // 10),
                      np.full(RATE // 10, .08), np.zeros(RATE // 10), np.full(2 * RATE, .1)].astype(np.float32)
        request = BoundaryRequest.parse(manifest(current_end_seconds=3.23 + len(audio) / RATE), "a" * 64, len(audio))
        count = 0
        async def same_words(_):
            nonlocal count
            count += 1
            return dict(text=request.exclude_text if count % 2 else request.keep_text)
        result = await refine_boundary(audio, request, same_words)
        self.assertEqual(result["reason"], "ambiguous_boundary")

    async def test_prefix_audio_has_no_future_gap(self):
        prefix = self.wav[:int(.8 * RATE)]
        request = BoundaryRequest.parse(manifest(current_end_seconds=4.03), "a" * 64, len(prefix))
        self.assertEqual(acoustic_gaps(prefix, request), [])

    def test_invalid_manifest_and_source_binding(self):
        invalid = [dict(audio_sha256="b" * 64), dict(current_end_seconds=100),
                   dict(source_offset_seconds=-1), dict(current_start_seconds=float("nan")),
                   dict(edge="middle"), dict(schema=True), dict(exclude_text="!"),
                   dict(keep_text="接下来唱第二段喽"), dict(source_revision="")]
        for changes in invalid:
            with self.subTest(changes=changes), self.assertRaises(BoundaryError):
                BoundaryRequest.parse(manifest(**changes), "a" * 64, len(self.wav))


if __name__ == "__main__":
    unittest.main()
