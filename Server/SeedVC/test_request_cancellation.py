"""Real bridge control flow, fake worker: no model, GPU, subprocess or HTTP."""
import asyncio
import importlib.util
import io
from pathlib import Path
import threading
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("seedvc_cancel_test", Path(__file__).with_name("seedvc_server.py"))
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class CancellationTests(unittest.TestCase):
    def setUp(self):
        self.worker = server._RvcWorker()
        self.process = Mock()
        self.process.poll.return_value = None
        self.process.kill.side_effect = lambda: setattr(self.process.poll, "return_value", -9)
        self.worker._process = self.process
        self.worker._force_cpu = False
        self.worker._device = "cuda:0"
        self.patches = [patch.object(server, "_rvc_worker", self.worker),
            patch.object(self.worker, "_schedule_idle_shutdown"),
            patch.object(server.subprocess, "Popen", side_effect=AssertionError("No real process")),
            patch.object(server.subprocess, "run", side_effect=AssertionError("No device probing"))]
        for item in self.patches:
            item.start(); self.addCleanup(item.stop)
        server._active_process = None
        server._active_request_id = ""
        server._requests.clear()

    def endpoint(self, request_id="current"):
        async def run():
            return await server.convert(server.UploadFile(file=io.BytesIO(b"test")), "reference.wav",
                request_id, 8, True, 0, 1234, .85, .33, .75, 0., 60.)
        return asyncio.run(run())

    def run_fake_conversion(self, source, target, *args):
        request_id = args[-1]
        result = self.worker.convert({"source": "fake"}, timeout=1, force_cpu=False,
                                     request_id=request_id)
        if result is None:
            self.fail("Cancelled worker must not fall through to a one-shot retry")
        return b"fake-wav", {}

    def test_persistent_request_is_cancelled_and_cannot_retry(self):
        def read(process, timeout):
            self.assertTrue(server.cancel("current")["cancelled"])
            return ""
        with patch.object(server, "_run_conversion", side_effect=self.run_fake_conversion), \
                patch.object(server, "_read_line_with_timeout", side_effect=read):
            with self.assertRaises(server.HTTPException) as raised:
                self.endpoint()
        self.assertEqual(raised.exception.status_code, 499)
        self.process.kill.assert_called()
        self.process.communicate.assert_called_once_with(timeout=5)
        self.assertIsNone(self.worker._process)
        self.assertIsNone(server._active_process)
        self.assertEqual(server._active_request_id, "")
        self.assertFalse(server._conversion_lock.locked())

    def test_wrong_id_preserves_live_worker_and_success(self):
        def read(process, timeout):
            self.assertFalse(server.cancel("old")["cancelled"])
            return '{"ok":true}'
        with patch.object(server, "_run_conversion", side_effect=self.run_fake_conversion), \
                patch.object(server, "_read_line_with_timeout", side_effect=read):
            self.assertEqual(self.endpoint().body, b"fake-wav")
        self.process.kill.assert_not_called()
        self.assertFalse(server.cancel("current")["cancelled"])

    def test_cancel_before_worker_binding_kills_only_late_old_process(self):
        def convert(*args):
            self.assertTrue(server.cancel("current")["cancelled"])
            server._bind_process(self.process, "current")
            self.fail("cancelled request accepted a late worker")
        with patch.object(server, "_run_conversion", side_effect=convert):
            with self.assertRaises(server.HTTPException) as raised:
                self.endpoint()
        self.assertEqual(raised.exception.status_code, 499)
        self.process.kill.assert_called_once()

    def test_cancelled_result_never_publishes_and_successor_works(self):
        def cancelled(*args):
            self.assertTrue(server.cancel("current")["cancelled"])
            return b"old-wav", {}
        with patch.object(server, "_run_conversion", side_effect=cancelled):
            with self.assertRaises(server.HTTPException): self.endpoint()
        with patch.object(server, "_run_conversion", return_value=(b"new-wav", {})):
            self.assertEqual(self.endpoint("next").body, b"new-wav")
        self.assertFalse(server.cancel("current")["cancelled"])

    def test_legacy_one_shot_process_cancel_contract(self):
        def convert(*args):
            server._bind_process(self.process, "current")
            self.assertFalse(server.cancel("other")["cancelled"])
            self.assertTrue(server.cancel("current")["cancelled"])
            return b"old-wav", {}
        with patch.object(server, "_run_conversion", side_effect=convert):
            with self.assertRaises(server.HTTPException): self.endpoint()
        self.process.kill.assert_called_once()

    def test_queued_cancel_does_not_kill_current_or_start_cancelled_work(self):
        entered = threading.Event()
        release = threading.Event()
        called = []
        def conversion(*args):
            called.append(args[-1])
            server._bind_process(self.process, args[-1])
            entered.set()
            if not release.wait(3): raise AssertionError("test did not release worker")
            return b"first-wav", {}
        async def scenario():
            reads = [asyncio.Event(), asyncio.Event()]
            class Upload:
                def __init__(self, n): self.n=n
                async def read(self):
                    await reads[self.n].wait()
                    return b"fake"
            def request(n, identity):
                return asyncio.create_task(server.convert(Upload(n), "ref.wav", identity,
                    8, True, 0, 1234, .85, .33, .75, 0., 60.))
            first=request(0,"A"); second=request(1,"B")
            await asyncio.sleep(0) # Both requests pass the early busy check.
            reads[0].set()
            self.assertTrue(await asyncio.to_thread(entered.wait,2))
            reads[1].set()
            for _ in range(100):
                if "B" in server._requests: break
                await asyncio.sleep(.001)
            try:
                self.assertTrue(server.cancel("B")["cancelled"])
                self.assertFalse(server.cancel("wrong")["cancelled"])
                self.process.kill.assert_not_called()
            finally: release.set()
            self.assertEqual((await first).body,b"first-wav")
            with self.assertRaises(server.HTTPException) as raised: await second
            self.assertEqual(raised.exception.status_code,499)
        with patch.object(server,"_run_conversion",side_effect=conversion): asyncio.run(scenario())
        self.assertEqual(called,["A"])
        self.assertEqual(server._requests,{})

    def test_duplicate_id_cannot_replace_existing_cancellation(self):
        event=threading.Event()
        server._requests["current"]=event
        try:
            with self.assertRaises(server.HTTPException) as raised: self.endpoint()
            self.assertEqual(raised.exception.status_code,409)
            self.assertIs(server._requests["current"],event)
        finally: server._requests.clear()

    def test_cancel_before_worker_check_preserves_unowned_warm_worker(self):
        def conversion(*args):
            server.cancel("current")
            return self.run_fake_conversion(*args)
        with patch.object(server,"_run_conversion",side_effect=conversion):
            with self.assertRaises(server.HTTPException): self.endpoint()
        self.process.kill.assert_not_called()
        self.assertIs(self.worker._process,self.process)

    def test_cancel_between_check_and_reuse_bind_preserves_warm_worker(self):
        original=server._bind_process
        def bind(process,request_id,new_process=True):
            server.cancel(request_id)
            return original(process,request_id,new_process)
        with patch.object(server,"_run_conversion",side_effect=self.run_fake_conversion), \
                patch.object(server,"_bind_process",side_effect=bind):
            with self.assertRaises(server.HTTPException): self.endpoint()
        self.process.kill.assert_not_called()
        self.assertIs(self.worker._process,self.process)


if __name__ == "__main__": unittest.main()
