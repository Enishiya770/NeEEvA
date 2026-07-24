import io
import json
import os
import tempfile
import unittest
import wave

import numpy as np

from song_search import SongSearchEngine
from singing_analysis import SingingAnalyzer


class _FakeSingingAnalyzer:
    def analyze(self, wav, lyrics="", thorough=False):
        return {
            "pitch_contour_midi": [60, 60, 62, 64, 64, 65, 67, 67, 69, 67, 65, 64],
            "pitch_timeline_midi": [0, 60, 60, 62, 64, 0, 65, 67, 69, 67, 65, 64, 0],
            "pitch_timeline_frame_seconds": 0.1,
        }


class _SequenceSingingAnalyzer:
    def __init__(self, contours):
        self.contours = list(contours)

    def analyze(self, wav, lyrics="", thorough=False):
        contour = self.contours.pop(0)
        return {
            "pitch_contour_midi": contour,
            "pitch_timeline_midi": [0] + contour + [0],
            "pitch_timeline_frame_seconds": 0.1,
        }


def _silent_wav_bytes(seconds=1.0, sample_rate=16000):
    output = io.BytesIO()
    with wave.open(output, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(sample_rate)
        writer.writeframes(np.zeros(int(seconds * sample_rate), dtype=np.int16).tobytes())
    return output.getvalue()


class SongMemoryTests(unittest.TestCase):
    def test_playable_timeline_preserves_rests_and_timing(self):
        midi = np.asarray([60.0, 60.2, 0.0, 0.0, 62.0, 62.1, 64.0, 64.1], dtype=np.float32)
        voiced = np.asarray([True, True, False, False, True, True, True, True])
        timeline = SingingAnalyzer._build_timeline(
            midi, voiced, hop_seconds=0.05, frame_seconds=0.10
        )
        self.assertEqual(len(timeline), 4)
        self.assertGreater(timeline[0], 59.0)
        self.assertEqual(timeline[1], 0.0)
        self.assertGreater(timeline[-1], 63.0)

    def test_unnamed_remember_rename_match_and_forget(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            catalog_path = os.path.join(temp_dir, "song_catalog.json")
            engine = SongSearchEngine(catalog_path, _FakeSingingAnalyzer())
            audio = _silent_wav_bytes()
            wav = np.zeros(16000, dtype=np.float32)

            remembered = engine.remember_clip(
                title="",
                artist="",
                wav=wav,
                wav_bytes=audio,
                reason="an improvised hum",
            )
            self.assertFalse(remembered["named"])
            self.assertEqual(remembered["reference_count"], 1)
            song_id = remembered["song_id"]
            old_path = os.path.join(temp_dir, remembered["wav_file"])
            self.assertTrue(os.path.isfile(old_path))
            self.assertIn("unknown", os.path.basename(old_path))
            with open(catalog_path, "r", encoding="utf-8") as handle:
                first_catalog = json.load(handle)
            first_reference = first_catalog["songs"][0]["references"][0]
            self.assertIn(0, first_reference["pitch_timeline_midi"])
            self.assertEqual(first_reference["pitch_timeline_frame_seconds"], 0.1)

            renamed = engine.rename_song(
                song_id,
                title='Lemon:/?*',
                artist="米津玄師",
                aliases=["レモン"],
            )
            self.assertEqual(renamed["title"], 'Lemon:/?*')
            summaries = engine.catalog_summaries()
            self.assertEqual(len(summaries), 1)

            with open(catalog_path, "r", encoding="utf-8") as handle:
                catalog_text = handle.read()
            self.assertIn('"version": 3', catalog_text)
            self.assertNotIn(os.path.basename(old_path), catalog_text)
            self.assertFalse(os.path.exists(old_path))

            managed_files = os.listdir(engine.audio_dir)
            self.assertEqual(len(managed_files), 1)
            self.assertNotRegex(managed_files[0], r'[<>:"/\\|?*]')

            second_reference = engine.remember_clip(
                title="",
                artist="",
                wav=wav,
                wav_bytes=audio,
                reason="a second phrase from the same remembered song",
                song_id=song_id,
            )
            self.assertEqual(second_reference["song_id"], song_id)
            self.assertEqual(second_reference["reference_count"], 2)
            self.assertEqual(len(os.listdir(engine.audio_dir)), 2)

            matched = engine.search(
                query="",
                mode="hum",
                melody_contour=[65, 65, 67, 69, 69, 70, 72, 72, 74, 72, 70, 69],
            )
            self.assertTrue(matched["reliable"])
            self.assertEqual(matched["best_match"]["source_id"], song_id)

            forgotten = engine.forget_song(song_id)
            self.assertEqual(forgotten["song_id"], song_id)
            self.assertEqual(engine.catalog_count, 0)
            self.assertEqual(os.listdir(engine.audio_dir), [])

    def test_duplicate_takes_are_variants_not_song_sequence(self):
        contour = [60, 60, 62, 64, 64, 65, 67, 67, 69, 67, 65, 64]
        with tempfile.TemporaryDirectory() as temp_dir:
            engine = SongSearchEngine(
                os.path.join(temp_dir, "song_catalog.json"),
                _SequenceSingingAnalyzer([contour, contour, contour]),
            )
            audio = _silent_wav_bytes()
            wav = np.zeros(16000, dtype=np.float32)
            first = engine.remember_clip("Lemon", "米津玄師", wav, audio, lyrics="same")
            engine.remember_clip("", "", wav, audio, lyrics="same", song_id=first["song_id"])
            third = engine.remember_clip("", "", wav, audio, lyrics="same", song_id=first["song_id"])

            self.assertEqual(third["reference_count"], 3)
            self.assertEqual(third["unique_segment_count"], 1)
            self.assertEqual(third["duplicate_variant_count"], 2)
            memory_plan = engine.resolve_performance(
                song_id=first["song_id"], mode="memory", seed=7
            )
            self.assertEqual(memory_plan["selected_segment_count"], 1)
            with self.assertRaisesRegex(ValueError, "重复演唱样本"):
                engine.resolve_performance(
                    song_id=first["song_id"],
                    mode="continue",
                    query_contour=contour,
                )

    def test_continuation_uses_next_unique_learned_segment(self):
        first_contour = [60, 60, 62, 64, 64, 65, 67, 67, 69, 67, 65, 64]
        next_contour = [60, 63, 59, 66, 61, 68, 62, 69, 63, 70, 64, 71]
        with tempfile.TemporaryDirectory() as temp_dir:
            engine = SongSearchEngine(
                os.path.join(temp_dir, "song_catalog.json"),
                _SequenceSingingAnalyzer([first_contour, next_contour]),
            )
            audio = _silent_wav_bytes()
            wav = np.zeros(16000, dtype=np.float32)
            first = engine.remember_clip("Test Song", "Artist", wav, audio, lyrics="first")
            second = engine.remember_clip(
                "Test Song", "", wav, audio, lyrics="next"
            )
            self.assertEqual(second["song_id"], first["song_id"])
            self.assertEqual(second["unique_segment_count"], 2)
            plan = engine.resolve_performance(
                song_id=first["song_id"],
                mode="continue",
                query_contour=first_contour,
                query_lyrics="first",
            )
            self.assertTrue(plan["continuation"])
            self.assertEqual(plan["continuation_basis"], "learned_segment_sequence")
            self.assertEqual(plan["selected_segment_count"], 1)
            self.assertEqual(plan["selected_references"][0]["sequence_index"], 1)
            self.assertEqual(plan["lyrics_confidence"], 1.0)


if __name__ == "__main__":
    unittest.main()
