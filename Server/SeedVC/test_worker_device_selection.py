"""No GPU, service, nvidia-smi, model imports or child processes are used."""

import asyncio
from concurrent.futures import ThreadPoolExecutor
import importlib.util
from pathlib import Path
import threading
import unittest
from unittest.mock import Mock, patch


spec = importlib.util.spec_from_file_location(
    "seedvc_worker_test_module", Path(__file__).with_name("seedvc_server.py")
)
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class WorkerDeviceSelectionTests(unittest.TestCase):
    def setUp(self):
        self.worker = server._RvcWorker()
        self.events = []
        self.free_mib = 1200
        self.patches = [
            patch.object(server, "RVC_GPU_MIN_FREE_MIB", 1000),
            patch.object(server, "RVC_WORKER_IDLE_SECONDS", 90),
            patch.object(server, "_cuda_free_mib", side_effect=lambda: self.free_mib),
            patch.object(self.worker, "_spawn", side_effect=self.spawn),
            patch.object(self.worker, "_shutdown_locked", side_effect=self.shutdown),
            patch.object(self.worker, "_schedule_idle_shutdown"),
            patch.object(server.subprocess, "Popen", side_effect=AssertionError("No real process allowed")),
            patch.object(server.subprocess, "run", side_effect=AssertionError("No real nvidia-smi allowed")),
        ]
        for item in self.patches:
            item.start()
            self.addCleanup(item.stop)

    def spawn(self, force_cpu):
        self.events.append(("spawn", force_cpu))
        self.worker._process = Mock()
        self.worker._process.poll.return_value = None
        self.worker._force_cpu = force_cpu
        self.worker._device = "cpu" if force_cpu else "cuda:0"
        return True

    def shutdown(self):
        self.events.append(("shutdown", self.worker._force_cpu))
        self.worker._process = None
        self.worker._force_cpu = None

    def test_live_cuda_is_not_evicted_by_its_own_vram_use(self):
        first = self.worker.warm_up()
        self.free_mib = 475
        second = self.worker.warm_up()
        self.assertFalse(first["already_ready"])
        self.assertTrue(second["already_ready"])
        self.assertEqual(second["device"], "cuda:0")
        self.assertEqual(second["cuda_free_mib_before"], 475)
        self.assertEqual(self.events, [("spawn", False)])

    def test_cold_low_memory_still_uses_cpu(self):
        self.free_mib = 900
        result = self.worker.warm_up()
        self.assertEqual(result["device"], "cpu")
        self.assertEqual(self.events, [("spawn", True)])

    def test_cpu_upgrades_on_next_warmup_with_sufficient_memory(self):
        self.free_mib = 900
        self.worker.warm_up()
        self.free_mib = 1200
        # Conversion's existing lookup keeps CPU until an explicit warm-up.
        self.assertTrue(self.worker.active_force_cpu())
        upgraded = self.worker.warm_up()
        self.assertFalse(upgraded["already_ready"])
        self.assertEqual(upgraded["device"], "cuda:0")
        self.free_mib = 400
        self.assertTrue(self.worker.warm_up()["already_ready"])
        self.assertEqual(self.events, [("spawn", True), ("shutdown", True), ("spawn", False)])

    def test_live_cpu_is_reused_while_memory_remains_low(self):
        self.free_mib = 900
        self.worker.warm_up()
        self.assertTrue(self.worker.warm_up()["already_ready"])
        self.assertEqual(self.events, [("spawn", True)])

    def test_exited_cuda_worker_does_not_bypass_cold_guard(self):
        self.worker.warm_up()
        self.worker._process.poll.return_value = 0
        self.free_mib = 900
        result = self.worker.warm_up()
        self.assertFalse(result["already_ready"])
        self.assertEqual(result["device"], "cpu")
        self.assertEqual(self.events, [("spawn", False), ("spawn", True)])

    def test_unknown_memory_retains_existing_cold_cuda_policy(self):
        self.free_mib = None
        self.assertEqual(self.worker.warm_up()["device"], "cuda:0")

    def test_concurrent_warmups_reuse_one_cuda_process(self):
        entered = threading.Event()
        release = threading.Event()

        def slow_spawn(force_cpu):
            entered.set()
            self.assertTrue(release.wait(2), "Test failed to release mocked model startup")
            self.free_mib = 475  # First CUDA load consumes its own headroom.
            return self.spawn(force_cpu)

        self.worker._spawn.side_effect = slow_spawn
        with ThreadPoolExecutor(max_workers=2) as executor:
            first = executor.submit(self.worker.warm_up)
            self.assertTrue(entered.wait(2))
            second = executor.submit(self.worker.warm_up)
            try:
                self.assertEqual(self.worker.status_snapshot(), {"worker_status": "busy", "active_device": None})
            finally:
                release.set()
            self.assertFalse(first.result(timeout=2)["already_ready"])
            self.assertTrue(second.result(timeout=2)["already_ready"])
        self.assertEqual(self.events, [("spawn", False)])

    def test_health_separates_active_device_from_cold_prediction(self):
        self.worker.warm_up()
        self.free_mib = 475
        with patch.object(server, "_rvc_worker", self.worker), patch.object(server, "_rvc_missing", return_value=[]):
            health = server.health()
        self.assertEqual(health["active_device"], "cuda:0")
        self.assertEqual(health["worker_status"], "ready")
        self.assertEqual(health["will_use"], "cpu-low-vram")
        self.assertIn("Cold-start", health["will_use_scope"])

    def test_health_does_not_wait_for_worker_lock(self):
        with patch.object(server, "_rvc_worker", self.worker), patch.object(server, "_rvc_missing", return_value=[]):
            with self.worker._lock:
                # This would deadlock if /health acquired the lock in blocking mode.
                health = server.health()
        self.assertEqual(health["worker_status"], "busy")
        self.assertIsNone(health["active_device"])

    def test_warmup_endpoint_uses_worker_atomic_selection(self):
        with patch.object(server, "_rvc_worker", self.worker), patch.object(server, "_rvc_ready", return_value=True):
            first = asyncio.run(server.warmup())
            self.free_mib = 475
            second = asyncio.run(server.warmup())
        self.assertTrue(first["ok"])
        self.assertTrue(second["already_ready"])
        self.assertEqual(second["device"], "cuda:0")
        self.assertEqual(second["cuda_free_mib_before"], 475)
        self.assertGreaterEqual(second["elapsed_seconds"], 0)
        self.assertEqual(self.events, [("spawn", False)])


if __name__ == "__main__":
    unittest.main()
