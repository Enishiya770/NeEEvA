import unittest
from streaming_windows import WindowTranscript, next_window_start


class AnchoredWindowsTests(unittest.TestCase):
    def test_origin_is_stable_until_window_limit(self):
        start, end = 0, 0
        starts = []
        for total in range(1000, 17001, 1000):
            start = next_window_start(total, min(total, 10000), start, end)
            self.assertLessEqual(total - start, 6000)
            starts.append(start)
            end = total
        self.assertEqual(starts[:6], [0] * 6)
        self.assertEqual(starts[6:10], [4500] * 4)
        self.assertEqual(starts[10:14], [8500] * 4)

    def test_burst_and_retention_limit_remain_bounded(self):
        self.assertEqual(next_window_start(30000, 10000, 0, 1000), 24000)
        self.assertEqual(next_window_start(30000, 2000, 0, 1000), 28000)

    def test_revisions_replace_only_current_window(self):
        # Exact linguistic overlap behavior is tested in test_streaming_window.py.
        transcript = WindowTranscript(lambda a, b: a + b)
        self.assertEqual(transcript.update(0, "很长的初稿"), "很长的初稿")
        self.assertEqual(transcript.update(0, "修正"), "修正")
        self.assertEqual(transcript.update(4500, "第二句话"), "修正第二句话")
        self.assertEqual(transcript.update(4500, "第二句修正"), "修正第二句修正")
        self.assertEqual(transcript.update(0, "过期结果"), "修正第二句修正")
        self.assertEqual(transcript.update(4500, ""), "修正")
        self.assertEqual(transcript.update(8500, "第三句"), "修正第三句")

    def test_deliberate_repetition_within_window_is_preserved(self):
        transcript = WindowTranscript(lambda a, b: a + b)
        self.assertEqual(transcript.update(0, "你好你好你好"), "你好你好你好")


if __name__ == '__main__':
    unittest.main()
