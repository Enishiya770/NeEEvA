import unittest
from types import SimpleNamespace

from asr_runtime import (
    model_device_details, require_cuda_after_load, require_cuda_before_load,
    torch_runtime_details, warmup_asr, RequiredCudaWarmupError,
)


def fake_torch(available=False, build=None):
    return SimpleNamespace(
        __version__="test+cu" if available else "test+cpu",
        version=SimpleNamespace(cuda=build),
        cuda=SimpleNamespace(is_available=lambda: available),
    )


def fake_model(parameters=(), buffers=(), configured="cuda:0"):
    return SimpleNamespace(
        kwargs={"device": configured},
        model=SimpleNamespace(
            parameters=lambda: iter(SimpleNamespace(device=d) for d in parameters),
            buffers=lambda: iter(SimpleNamespace(device=d) for d in buffers),
        ),
    )


class AsrRuntimeTests(unittest.TestCase):
    def test_required_cuda_warmup_failure_is_fatal_and_preserves_cause(self):
        failure = RuntimeError("no kernel image is available for execution")
        def infer():
            raise failure
        with self.assertRaises(RequiredCudaWarmupError) as caught:
            warmup_asr(infer, require_cuda=True)
        self.assertIs(caught.exception.__cause__, failure)
        self.assertIn("no kernel image", str(caught.exception))
        with self.assertRaises(RuntimeError) as legacy:
            warmup_asr(infer, require_cuda=False)
        self.assertIs(legacy.exception, failure)  # Existing best-effort handler still receives it.

    def test_warmup_success_returns_the_real_inference_result(self):
        result = [{"text": ""}]
        self.assertIs(warmup_asr(lambda: result, require_cuda=True), result)

    def test_cpu_torch_rejected_only_when_cuda_requested_and_required(self):
        runtime = torch_runtime_details(fake_torch())
        self.assertIsNone(runtime["torch_cuda_build"])
        self.assertFalse(runtime["cuda_available"])
        with self.assertRaisesRegex(RuntimeError, "--require-cuda.*is_available"):
            require_cuda_before_load("cuda:0", True, runtime)
        require_cuda_before_load("cuda:0", False, runtime)
        require_cuda_before_load("cpu", True, runtime)
        require_cuda_after_load("cpu", True, model_device_details(fake_model(["cpu"]), "cpu"))

    def test_effective_cpu_tensors_override_requested_cuda_metadata(self):
        details = model_device_details(fake_model(["cpu"], configured="cuda:0"), "cuda:0")
        self.assertEqual(details["actual_device"], "cpu")
        self.assertEqual(details["device_source"], "model_tensors")
        with self.assertRaisesRegex(RuntimeError, "model tensors are on cpu"):
            require_cuda_after_load("cuda:0", True, details)
        require_cuda_after_load("cuda:0", False, details)

    def test_cuda_torch_and_model_pass(self):
        runtime = torch_runtime_details(fake_torch(True, "12.6"))
        self.assertEqual(runtime["torch_cuda_build"], "12.6")
        require_cuda_before_load("cuda:0", True, runtime)
        details = model_device_details(fake_model(["cuda:0"], ["cuda:0"]), "cuda:0")
        self.assertEqual(details["actual_devices"], ["cuda:0"])
        require_cuda_after_load("cuda:0", True, details)

    def test_buffer_only_device_and_mixed_cpu_cuda_are_reported(self):
        details = model_device_details(fake_model(buffers=["cuda:1"]), "cuda:1")
        self.assertEqual(details["actual_device"], "cuda:1")
        require_cuda_after_load("cuda:1", True, details)
        mixed = model_device_details(fake_model(["cuda:0"], ["cpu"]), "cuda:0")
        self.assertEqual(mixed["actual_device"], "mixed(cpu,cuda:0)")
        with self.assertRaisesRegex(RuntimeError, "mixed"):
            require_cuda_after_load("cuda:0", True, mixed)

    def test_unverified_device_is_not_promoted_from_kwargs(self):
        details = model_device_details(fake_model(configured="cuda:0"), "cuda:0")
        self.assertEqual(details["actual_device"], "unknown")
        self.assertEqual(details["device_source"], "unavailable")
        with self.assertRaisesRegex(RuntimeError, "unverified"):
            require_cuda_after_load("cuda:0", True, details)
        require_cuda_after_load("cuda:0", False, details)


if __name__ == "__main__":
    unittest.main()
