"""Final-first dispatch, ownership and cancellation at safe inference boundaries."""
import asyncio
import threading
import unittest

from asr_scheduler import AnalysisScheduler, checkpoint


class SchedulerTests(unittest.TestCase):
    def setUp(self):
        self.scheduler = AnalysisScheduler()
        self.entered = threading.Event()
        self.release = threading.Event()

        def block():
            self.entered.set()
            if not self.release.wait(3):
                raise TimeoutError("test worker not released")
            checkpoint()
            return "blocker"

        self.blocker = self.scheduler.submit(block, (), "blocker", "busy")
        self.assertTrue(self.entered.wait(1))

    def tearDown(self):
        self.release.set()
        self.blocker.result(3)

    def test_final_precedes_pending_preview_without_interrupting_active_model(self):
        order = []
        preview = self.scheduler.submit(lambda: order.append("preview"), (), "p", "preview", True)
        final = self.scheduler.submit(lambda: order.append("final"), (), "f", "final")
        self.assertFalse(final.done())
        self.release.set()
        preview.result(3)
        final.result(3)
        self.assertEqual(order, ["final", "preview"])

    def test_supersede_only_own_previews(self):
        old = self.scheduler.submit(lambda: self.fail("obsolete preview ran"), (), "old", "one", True)
        other = self.scheduler.submit(lambda: "other client", (), "other", "two", True)
        new = self.scheduler.submit(lambda: "new", (), "new", "one")
        self.release.set()
        self.assertEqual(old.result(3), {"cancelled": True})
        self.assertEqual(other.result(3), "other client")
        self.assertEqual(new.result(3), "new")

    def test_cancel_before_upload_and_promotion_before_upload(self):
        self.scheduler.cancel("cancel-first")
        cancelled = self.scheduler.submit(lambda: self.fail("cancelled upload ran"), (), "cancel-first")
        self.assertEqual(cancelled.result(1), {"cancelled": True})
        self.scheduler.promote("promote-first")
        order = []
        promoted = self.scheduler.submit(lambda: order.append("promoted"), (), "promote-first", "one", True)
        final = self.scheduler.submit(lambda: order.append("final"), (), "next", "one")
        self.release.set()
        promoted.result(3)
        final.result(3)
        self.assertEqual(order, ["promoted", "final"])

    def test_promoted_preview_is_not_cancelled_by_next_final(self):
        order = []
        other = self.scheduler.submit(lambda: order.append("other"), (), "p0", "two", True)
        preview = self.scheduler.submit(lambda: order.append("promoted"), (), "p1", "one", True)
        self.assertTrue(self.scheduler.promote("p1"))
        final = self.scheduler.submit(lambda: order.append("final"), (), "f1", "one")
        self.release.set()
        for future in (other, preview, final):
            future.result(3)
        self.assertEqual(order, ["promoted", "final", "other"])

    def test_new_capture_does_not_cancel_old_upload_before_promotion_arrives(self):
        old = self.scheduler.submit(lambda: "recorded song", (), "old", "client:capture1", True)
        new = self.scheduler.submit(lambda: "new speech", (), "new", "client:capture2", True)
        self.scheduler.promote("old")
        self.release.set()
        self.assertEqual(old.result(3), "recorded song")
        self.assertEqual(new.result(3), "new speech")

    def test_cancel_running_job_at_checkpoint(self):
        self.scheduler.cancel("blocker")
        next_job = self.scheduler.submit(lambda: "next", ())
        self.release.set()
        self.assertEqual(self.blocker.result(3), {"cancelled": True})
        self.assertEqual(next_job.result(3), "next")

    def test_cancel_passes_through_acoustic_fallback(self):
        self.release.set()
        self.blocker.result(3)
        entered, release = threading.Event(), threading.Event()

        def work():
            entered.set()
            release.wait(3)
            try:
                checkpoint()
            except Exception:
                return "fake fallback evidence"
            return "expensive next phase"

        job = self.scheduler.submit(work, (), "active", "one", True)
        self.assertTrue(entered.wait(1))
        self.scheduler.cancel("active")
        release.set()
        self.assertEqual(job.result(3), {"cancelled": True})


class CancelledWaiterTests(unittest.IsolatedAsyncioTestCase):
    async def test_http_waiter_cancellation_does_not_poison_worker(self):
        scheduler = AnalysisScheduler()
        entered, release = threading.Event(), threading.Event()

        def work():
            entered.set()
            release.wait(3)
            return "finished"

        future = scheduler.submit(work, ())
        waiter = asyncio.create_task(scheduler.wait(future))
        self.assertTrue(await asyncio.to_thread(entered.wait, 1))
        waiter.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await waiter
        release.set()
        self.assertEqual(await scheduler.wait(future), "finished")
        self.assertEqual(await scheduler.wait(scheduler.submit(lambda: "next", ())), "next")


if __name__ == "__main__":
    unittest.main()
