"""Runtime device evidence and optional CUDA startup guards, without importing torch."""


class RequiredCudaWarmupError(RuntimeError):
    """Required CUDA tensors were loaded, but ASR inference could not execute."""


def warmup_asr(infer, require_cuda=False):
    try:
        return infer()
    except Exception as error:
        if require_cuda:
            raise RequiredCudaWarmupError(
                "--require-cuda: ASR CUDA warmup failed; refusing to start an "
                "unverified inference service: " + str(error)
            ) from error
        raise


def is_cuda_device(device):
    value = str(device or "").strip().lower()
    return value == "cuda" or value.startswith("cuda:")


def torch_runtime_details(torch_module):
    return {
        "torch_version": str(torch_module.__version__),
        "torch_cuda_build": getattr(torch_module.version, "cuda", None),
        "cuda_available": bool(torch_module.cuda.is_available()),
    }


def model_device_details(model, requested_device):
    """Read tensors rather than AutoModel kwargs, which may only express intent."""
    module = getattr(model, "model", model)
    devices = set()
    for collection in ("parameters", "buffers"):
        read = getattr(module, collection, None)
        if callable(read):
            for tensor in read():
                device = getattr(tensor, "device", None)
                if device is not None:
                    devices.add(str(device))
    actual_devices = sorted(devices)
    return {
        "requested_device": str(requested_device),
        "actual_device": actual_devices[0] if len(actual_devices) == 1 else
            "mixed(" + ",".join(actual_devices) + ")" if actual_devices else "unknown",
        "actual_devices": actual_devices,
        "device_source": "model_tensors" if actual_devices else "unavailable",
    }


def require_cuda_before_load(requested_device, require_cuda, runtime):
    if require_cuda and is_cuda_device(requested_device) and not runtime["cuda_available"]:
        raise RuntimeError(
            "--require-cuda: ASR requested " + str(requested_device) +
            " but torch.cuda.is_available() is false; torch=" +
            str(runtime["torch_version"]) + " CUDA build=" +
            str(runtime["torch_cuda_build"]) +
            ". Use a CUDA-enabled PyTorch environment or explicitly choose --device cpu."
        )


def require_cuda_after_load(requested_device, require_cuda, device_details):
    if not require_cuda or not is_cuda_device(requested_device):
        return
    actual = device_details["actual_devices"]
    if not actual or not all(is_cuda_device(device) for device in actual):
        raise RuntimeError(
            "--require-cuda: ASR requested " + str(requested_device) +
            " but model tensors are on " + device_details["actual_device"] +
            "; refusing to start with an unverified CUDA device or CPU fallback."
        )
