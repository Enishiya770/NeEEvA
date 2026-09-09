"""Concurrency regressions: final analysis must not stall /stream/asr."""
import asyncio
import threading
import time
import unittest
from unittest.mock import patch

import sensevoice_server as server


class Upload:
    async def read(self):
        return b"test-final-audio"


class Socket:
    def __init__(self):
        self.messages = iter([
            {"text": '{"event":"start"}'},
            {"bytes": bytes(28800)},  # 900 ms, long enough for the first partial
            {"text": '{"event":"cancel"}'},
        ])
        self.results = []

    async def accept(self):
        pass

    async def receive(self):
        return next(self.messages)

    async def send_json(self, result):
        self.results.append(result)


class AsrDispatchTests(unittest.IsolatedAsyncioTestCase):
    async def test_stream_progresses_while_final_pitch_is_busy(self):
        entered = threading.Event()
        release = threading.Event()

        def slow_final(*args):
            entered.set()
            release.wait(2)
            return {"text": "final"}

        socket = Socket()
        with patch.object(server, "_model", object()), \
             patch.object(server, "recognize_final_audio", slow_final), \
             patch.object(server, "recognize_stream_partial", return_value={"text": "partial"}):
            final = asyncio.create_task(server.asr(Upload(), "auto", False, False))
            try:
                started = time.perf_counter()
                self.assertTrue(await asyncio.to_thread(entered.wait, 1))
                await asyncio.wait_for(server.stream_asr(socket), 1)
                self.assertFalse(final.done(), "Full ASR blocked the event loop until it finished")
                self.assertLess(time.perf_counter() - started, 1)
                partial = next(r for r in socket.results if r["event"] == "partial")
                self.assertEqual(partial["audio_ms"], 900)
            finally:
                release.set()
                self.assertEqual((await final)["text"], "final")

    async def test_final_requests_remain_serialized_and_errors_do_not_lock_worker(self):
        active = 0
        peak = 0

        def final_work(*args):
            nonlocal active, peak
            active += 1
            peak = max(peak, active)
            time.sleep(.025)
            active -= 1
            return {"text": "ok"}

        with patch.object(server, "_model", object()), patch.object(server, "recognize_final_audio", final_work):
            results = await asyncio.gather(*[server.asr(Upload(), "auto", False, False) for _ in range(3)])
            self.assertEqual(peak, 1)
            self.assertEqual(len(results), 3)
        with patch.object(server, "_model", object()), patch.object(server, "recognize_final_audio", side_effect=ValueError("test")):
            with self.assertRaises(ValueError):
                await server.asr(Upload(), "auto", False, False)
        with patch.object(server, "_model", object()), patch.object(server, "recognize_final_audio", return_value={"text": "recovered"}):
            self.assertEqual((await server.asr(Upload(), "auto", False, False))["text"], "recovered")


if __name__ == "__main__":
    unittest.main()
