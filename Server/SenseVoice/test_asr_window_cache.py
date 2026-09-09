import unittest
from asr_window_cache import AsrWindowCache


class WindowCacheTests(unittest.TestCase):
    def test_exact_identity_and_mutation(self):
        cache = AsrWindowCache()
        calls = []
        def infer():
            calls.append(1)
            return [{"text": "actual transcription"}]
        args = (1, "auto", (3,), "float32", b"abc", infer)
        first, hit = cache.resolve(*args)
        self.assertFalse(hit)
        first[0]["text"] = "consumer mutation"
        second, hit = cache.resolve(*args)
        self.assertTrue(hit)
        self.assertEqual(second[0]["text"], "actual transcription")
        second[0]["text"] = "another mutation"
        self.assertEqual(cache.resolve(*args)[0][0]["text"], "actual transcription")
        self.assertEqual(len(calls), 1)
        for index, value in [(0, 2), (1, "ja"), (2, (1, 3)), (3, "int16"), (4, b"abd")]:
            different = list(args)
            different[index] = value
            self.assertFalse(cache.resolve(*different)[1])
        self.assertEqual(len(calls), 6)

    def test_ttl_capacity_and_errors(self):
        clock = [0.0]
        cache = AsrWindowCache(capacity=2, ttl=10, clock=lambda: clock[0])
        def get(audio):
            return cache.resolve(1, "auto", (1,), "f32", audio, lambda: [audio])
        get(b"a"); get(b"b"); get(b"a"); get(b"c")
        self.assertEqual(len(cache.entries), 2)
        self.assertFalse(get(b"b")[1])
        clock[0] = 11
        self.assertFalse(get(b"b")[1])
        def fail():
            raise RuntimeError("real error")
        with self.assertRaises(RuntimeError):
            cache.resolve(1, "auto", (1,), "f32", b"bad", fail)
        self.assertFalse(get(b"bad")[1])


if __name__ == "__main__":
    unittest.main()
