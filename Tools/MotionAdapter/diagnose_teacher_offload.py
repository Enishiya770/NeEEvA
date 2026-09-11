"""Bounded, offline BF16 LLM2Vec export with two known cached controls.

This does not change export_teacher, vendor source, the adapter, or live services.
Full-CUDA mode uses official construction and encode(batch_size=1); the historical
offload experiments omit encode()'s whole-model .to to retain Accelerate placement.
"""
import hashlib
import argparse
import json
import os
from pathlib import Path
import sys
import subprocess
import threading
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
OUTPUT = HERE / "runtime/teacher-motion-diagnostic"
CACHE = ROOT / "Server/ARDY/models/hf-cache/hub"
LOCK = json.loads((HERE / "upstream-lock.json").read_text(encoding="utf-8-sig"))
sys.path.insert(0, str(ROOT / "Server/ARDY/vendor" / ("ardy-" + LOCK["ardy"])))
os.environ["HF_HUB_CACHE"] = str(CACHE)
os.environ["HUGGINGFACE_CACHE_DIR"] = str(CACHE)
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
os.environ["OMP_NUM_THREADS"] = "4"
os.environ["HF_DEACTIVATE_ASYNC_LOAD"] = "1"
RESOURCE_STATE = {}
FINALIZE_RESOURCES = None
RESOURCE_PATH = OUTPUT / "resource-observations.json"
ABORT_PATH = OUTPUT / "resource-abort.json"
WRITE_RESOURCES = False


def main(argv=None, generic=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=Path, required=generic, default=OUTPUT / "prompts.jsonl")
    parser.add_argument("--output", type=Path, required=generic, default=OUTPUT / "teacher-six.npz")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--batch-size", type=int, choices=[1], default=1,
                        help="Official ARDY wrapper fixes internal batching at 1 for repeatable raw embeddings")
    parser.add_argument("--max-rss-gib", type=float, default=6, choices=[6, 8])
    if generic:
        parser.set_defaults(full_cuda=True, max_rss_gib=8, forbid_disk=True, cpu_weight_gib=6, max_cuda_gib=18)
    else:
        parser.add_argument("--cpu-weight-gib", type=float, default=4, choices=[4, 6])
        parser.add_argument("--forbid-disk", action="store_true")
        parser.add_argument("--max-cuda-gib", type=float, default=10, choices=[10, 12])
        parser.add_argument("--full-cuda", action="store_true", help="Requires a separately coordinated >=18-GiB free GPU window")
    args = parser.parse_args(argv)
    import numpy as np
    from Tools.MotionAdapter.data import read_records, read_bundle, write_bundle
    rows = read_records(args.dataset)
    args.output = args.output.resolve()
    report_path = args.report.resolve() if args.report else (
        args.output.with_suffix(".report.json") if generic else HERE / "reports/teacher-motion-diagnostic.json")
    if args.output.exists() or (generic and report_path.exists()):
        raise FileExistsError("Choose new output/report paths; existing features and checkpoints are never overwritten")
    if args.output.suffix.lower() != ".npz" or args.output == args.dataset.resolve():
        raise ValueError("Output must be a new .npz feature bundle separate from its input dataset")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    global RESOURCE_PATH, ABORT_PATH, WRITE_RESOURCES
    if generic:
        RESOURCE_PATH = args.output.with_suffix(".resources.json")
        ABORT_PATH = args.output.with_suffix(".abort.json")
    control_manifest = read_records(HERE / "runtime/prompts.jsonl")
    reference_features, reference_meta = read_bundle(HERE / "runtime/teacher-2000.npz", control_manifest, "teacher")
    controls = [control_manifest[i] for i in [4, 53]]
    references = reference_features[[4, 53]].copy()
    del reference_features
    paired_report = json.loads((HERE / "reports/paired-features.json").read_text(encoding="utf-8-sig"))
    if reference_meta["feature_contract"] != paired_report["teacher_contract"]:
        raise RuntimeError("The cached verification teacher contract no longer matches its recorded provenance")
    WRITE_RESOURCES = True
    import psutil
    import torch
    from peft import PeftModel
    import transformers.modeling_utils as modeling_utils
    from safetensors import safe_open
    from ardy.model.llm2vec.llm2vec import LLM2Vec, batch_to_device

    OUTPUT.mkdir(parents=True, exist_ok=True)
    gib = 1024**3
    def nvidia_memory():
        result = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,memory.free",
                                 "--format=csv,noheader,nounits"], check=True, capture_output=True,
                                text=True, timeout=3, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        used, free = [float(v.strip()) * 1024**2 for v in result.stdout.strip().splitlines()[0].split(",")]
        return int(used), int(free)

    available = psutil.virtual_memory().available
    gpu_free, gpu_total = torch.cuda.mem_get_info()
    nv_used, nv_free = nvidia_memory()
    RESOURCE_STATE["preflight"] = {"available_ram_bytes": available, "cuda_api_free_bytes": gpu_free,
                                   "nvidia_smi_used_bytes": nv_used, "nvidia_smi_free_bytes": nv_free}
    # The RSS cap includes already-imported Python/Torch memory; do not count
    # that resident footprint a second time as an additional future allocation.
    minimum_ram = max(0, args.max_rss_gib * gib - psutil.Process().memory_info().rss) + 2 * gib
    # Keep the original preflight threshold. The allocator cap is an upper
    # bound, while both actual free-memory providers are guarded during work.
    minimum_gpu = (18 if args.full_cuda else 13) * gib
    if available < minimum_ram or min(gpu_free, nv_free) < minimum_gpu:
        raise RuntimeError(f"Wait for independent rendering to finish: available RAM={available/gib:.2f} GiB, CUDA API GPU={gpu_free/gib:.2f} GiB, nvidia-smi GPU={nv_free/gib:.2f} GiB; require {minimum_ram/gib:.0f}/{minimum_gpu/gib:.0f} GiB before loading")
    torch.set_num_threads(4)
    if args.full_cuda:
        # This mode is only used during the explicitly coordinated ARDY pause.
        args.max_cuda_gib = min(18, min(gpu_free, nv_free) / gib - 2)
    gpu_headroom = (2 if args.full_cuda else 3) * gib
    torch.cuda.set_per_process_memory_fraction(args.max_cuda_gib * gib / gpu_total)
    # Weight placement budgets are separate from allocator/runtime ceilings.
    max_memory = {0: 9 * gib, "cpu": int(args.cpu_weight_gib * gib)}
    baseline = {"available_ram_bytes": available, "cuda_api_free_bytes": gpu_free,
                "nvidia_smi_used_bytes": nv_used, "nvidia_smi_free_bytes": nv_free}
    peaks = {"rss_bytes": 0, "cuda_allocated_bytes": 0, "cuda_reserved_bytes": 0}
    minima = {"system_available_ram_bytes": available, "cuda_api_free_bytes": gpu_free,
              "nvidia_smi_free_bytes": nv_free}
    RESOURCE_STATE.update(baseline=baseline, peaks=peaks, minima=minima,
                          rss_limit_bytes=int(args.max_rss_gib*gib), cuda_allocator_limit_bytes=int(args.max_cuda_gib*gib),
                          strategy="official-full-cuda" if args.full_cuda else "cpu-gpu-placement",
                          minimum_gpu_headroom_bytes=gpu_headroom,
                          note="CUDA allocator counters, CUDA API free, and nvidia-smi global memory are recorded separately; Windows WDDM values must not be added as one physical-memory ledger.")
    finished = threading.Event()
    process = psutil.Process()

    def persist_resources():
        RESOURCE_PATH.write_text(json.dumps(RESOURCE_STATE, indent=2), encoding="utf-8")

    def finalize_resources():
        finished.set()
        observer.join(timeout=4)
        peaks["cuda_allocated_bytes"] = max(peaks["cuda_allocated_bytes"], torch.cuda.max_memory_allocated())
        peaks["cuda_reserved_bytes"] = max(peaks["cuda_reserved_bytes"], torch.cuda.max_memory_reserved())
        RESOURCE_STATE["finished_unix_seconds"] = time.time()
        persist_resources()

    global FINALIZE_RESOURCES
    FINALIZE_RESOURCES = finalize_resources

    def monitor():
        last_nv = 0
        while not finished.wait(.25):
            rss = process.memory_info().rss
            ram_free = psutil.virtual_memory().available
            api_free = torch.cuda.mem_get_info()[0]
            peaks["rss_bytes"] = max(peaks["rss_bytes"], rss)
            peaks["cuda_allocated_bytes"] = max(peaks["cuda_allocated_bytes"], torch.cuda.max_memory_allocated())
            peaks["cuda_reserved_bytes"] = max(peaks["cuda_reserved_bytes"], torch.cuda.max_memory_reserved())
            minima["system_available_ram_bytes"] = min(minima["system_available_ram_bytes"], ram_free)
            minima["cuda_api_free_bytes"] = min(minima["cuda_api_free_bytes"], api_free)
            if time.monotonic() - last_nv > 1:
                try:
                    used, free = nvidia_memory()
                    minima["nvidia_smi_free_bytes"] = min(minima["nvidia_smi_free_bytes"], free)
                    RESOURCE_STATE["latest_nvidia_smi"] = {"used_bytes": used, "free_bytes": free, "unix_seconds": time.time()}
                except Exception as error:
                    RESOURCE_STATE["monitor_error"] = str(error)
                    persist_resources()
                    os._exit(71)
                last_nv = time.monotonic()
                persist_resources()
            if (rss > args.max_rss_gib * gib or ram_free < 2 * gib
                    or api_free < gpu_headroom or minima["nvidia_smi_free_bytes"] < gpu_headroom):
                RESOURCE_STATE["guard_abort"] = True
                persist_resources()
                ABORT_PATH.write_text(json.dumps({"reason": "CPU budget or RAM/GPU minimum headroom reached", "peaks": peaks,
                                                                         "minimum_gpu_headroom_bytes": gpu_headroom}), encoding="utf-8")
                # End only this independent diagnostic process, releasing its
                # model immediately. Never terminate another service process.
                os._exit(70)

    observer = threading.Thread(target=monitor, daemon=True)
    observer.start()
    recorded = paired_report["teacher_provenance"]
    snapshots = {}
    for repo, revision in recorded["repos"].items():
        hub = CACHE / ("models--" + repo.replace("/", "--"))
        path = hub / "snapshots" / revision
        if not path.is_dir():
            raise RuntimeError(f"Missing pinned local snapshot {repo}@{revision}")
        # MNTP's adapter config references this base by repository id. In offline
        # mode it must resolve to the same already-cached pinned revision.
        if (hub / "refs/main").read_text().strip() != revision:
            raise RuntimeError("Offline model ref differs from training provenance")
        snapshots[repo] = str(path)

    original_loader = PeftModel.from_pretrained.__func__

    @classmethod
    def bounded_peft(cls, model, model_id, *positional, **kwargs):
        # The official LLM2Vec factory loads both adapters with this method.
        # Propagate the same memory bounds instead of PEFT auto-fitting them to
        # all currently free GPU memory after attaching an offloaded adapter.
        if args.full_cuda:
            kwargs.update(local_files_only=True)
        else:
            kwargs.update(device_map="auto", max_memory=max_memory,
                          offload_folder=str(OUTPUT / "weight-offload"),
                          low_cpu_mem_usage=True, local_files_only=True)
        if args.forbid_disk and "disk" in getattr(model, "hf_device_map", {}).values():
            raise RuntimeError("Requested CPU/GPU-only diagnostic unexpectedly needs disk offload")
        return original_loader(cls, model, model_id, *positional, **kwargs)

    PeftModel.from_pretrained = bounded_peft
    original_safe_open = modeling_utils.safe_open
    original_allocator_warmup = modeling_utils.caching_allocator_warmup

    class ReleasedSlice:
        """Read one exact tensor, clone, then release its file mapping.

        Transformers 5.8 retains all shard mappings until the entire model has
        loaded. Windows counts their touched pages against this process's RSS,
        even for tensors already copied to GPU. Per-tensor mappings keep the
        same BF16 values without retaining all those source pages at once.
        """
        def __init__(self, filename, key, shape, dtype):
            self.filename, self.key = filename, key
            self.shape, self.dtype = shape, dtype

        def get_shape(self):
            return self.shape

        def get_dtype(self):
            return self.dtype

        def __getitem__(self, item):
            with safe_open(self.filename, framework="pt", device="cpu") as handle:
                return handle.get_slice(self.key)[item].clone()

    class ReleasedReader:
        def __init__(self, filename, **kwargs):
            self.filename = filename
            with safe_open(filename, **kwargs) as handle:
                self.info = {key: (handle.get_slice(key).get_shape(), handle.get_slice(key).get_dtype())
                             for key in handle.keys()}
                self.meta = handle.metadata()

        def keys(self):
            return self.info.keys()

        def metadata(self):
            return self.meta

        def get_slice(self, key):
            return ReleasedSlice(self.filename, key, *self.info[key])

        def __enter__(self):
            return self

        def __exit__(self, *args):
            pass

    modeling_utils.safe_open = ReleasedReader
    # This preallocation optimization ignores the per-process CUDA cap and
    # tries to reserve a second model-sized block while adding tiny adapters.
    # Let PyTorch allocate the actual tensors as needed instead.
    modeling_utils.caching_allocator_warmup = lambda *args, **kwargs: None
    started = time.monotonic()
    try:
        placement = {"device_map": "cuda:0"} if args.full_cuda else {
            "device_map": "auto", "max_memory": max_memory, "offload_folder": str(OUTPUT / "weight-offload")}
        print(f"Loading pinned BF16 teacher, full_cuda={args.full_cuda}, allocator_cap={args.max_cuda_gib:.3f} GiB; offline only", flush=True)
        # Preserve the actual official factory's MNTP wrapping/merge sequence.
        # Every offline repo ref has been checked against training provenance.
        encoder = LLM2Vec.from_pretrained(
            base_model_name_or_path="McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp",
            peft_model_name_or_path="McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised",
            torch_dtype=torch.bfloat16, cache_dir=str(CACHE),
            low_cpu_mem_usage=True, local_files_only=True, **placement)
    finally:
        PeftModel.from_pretrained = classmethod(original_loader)
        modeling_utils.safe_open = original_safe_open
        modeling_utils.caching_allocator_warmup = original_allocator_warmup
    # Match load_text_encoder's final BF16 conversion without relocating modules.
    encoder.to(dtype=torch.bfloat16)
    if args.full_cuda:
        encoder.to("cuda:0")
        if any(parameter.device.type != "cuda" for parameter in encoder.parameters()):
            raise RuntimeError("Full-CUDA teacher unexpectedly retained non-CUDA parameters")
    encoder.eval()
    for parameter in encoder.parameters():
        parameter.requires_grad = False
    loaded = time.monotonic()
    device_map = getattr(encoder.model, "hf_device_map", {})
    if args.forbid_disk and "disk" in device_map.values():
        raise RuntimeError("Requested CPU/GPU-only diagnostic unexpectedly needs disk offload")
    print("Loaded bounded teacher; verify two cached controls before exporting the dataset", flush=True)
    vectors, timings, comparisons = [], [], []
    verified_control_vectors = {}

    def encode_one(row):
        start = time.monotonic()
        with torch.no_grad():
            if args.full_cuda:
                value = encoder.encode([row["text"]], batch_size=args.batch_size, show_progress_bar=False,
                                       device="cuda:0").detach().float().cpu().numpy()
            else:
                sentence = encoder._convert_to_str("", row["text"])
                features = encoder.tokenize([encoder.prepare_for_tokenization(sentence)])
                features = batch_to_device(features, "cuda:0")
                value = encoder.forward(features).detach().float().cpu().numpy()
        if value.shape != (1, 4096) or not np.isfinite(value).all():
            raise RuntimeError("Teacher feature shape/finite check failed")
        timings.append({"id": row["id"], "seconds": time.monotonic() - start})
        return value[0]

    for row, reference in zip(controls, references):
        vector = encode_one(row)
        relative_l2 = float(np.linalg.norm(vector - reference) / np.linalg.norm(reference))
        cosine = float(vector @ reference / (np.linalg.norm(vector) * np.linalg.norm(reference)))
        comparisons.append({"id": row["id"], "text": row["text"], "cosine": cosine,
                            "relative_l2": relative_l2, "max_abs_error": float(np.max(np.abs(vector-reference)))})
        RESOURCE_STATE["cached_comparisons"] = comparisons
        if cosine < .9999 or relative_l2 > .005:
            raise RuntimeError(f"Teacher does not match cached wrapper scale/format: {comparisons[-1]}")
        verified_control_vectors[row["text"]] = vector
        print("Verified cached teacher: " + json.dumps(comparisons[-1]), flush=True)
    for i, row in enumerate(rows):
        vector = verified_control_vectors.get(row["text"])
        if vector is None:
            vector = encode_one(row)
        vectors.append(vector)
        if (i + 1) % 100 == 0 or i + 1 == len(rows):
            print(f"Encoded {i+1}/{len(rows)} dataset descriptions", flush=True)
    features = np.stack(vectors)
    provenance = {**recorded, "device": "bounded-bf16-full-cuda" if args.full_cuda else "bounded-bf16-accelerate-" + ("cuda-cpu" if args.forbid_disk else "cuda-cpu-disk-offload"),
                  "placement_only_change": "Official factory and encode(batch_size=1), direct full-CUDA construction" if args.full_cuda else "Official prepare_for_tokenization/tokenize/forward/get_pooling; omitted encode's full-device .to",
                  "model_snapshot_paths": snapshots, "device_map": device_map,
                  "loading_io": "sequential exact tensor copy with per-tensor safetensors mapping release",
                  "allocator_warmup": "disabled unnecessary model-sized preallocation during adapter load",
                  "construction": "unchanged official LLM2Vec.from_pretrained factory",
                  "max_weight_memory_bytes": None if args.full_cuda else {str(k): v for k, v in max_memory.items()},
                  "hard_process_rss_limit_bytes": int(args.max_rss_gib * gib),
                  "hard_cuda_allocator_limit_bytes": int(args.max_cuda_gib * gib),
                  "batch_size": args.batch_size,
                  "cached_teacher_reference_contract": reference_meta["feature_contract"],
                  "cached_teacher_reference_records_sha256": reference_meta["records_sha256"],
                  "new_diagnostic_contract": True, "cached_teacher_comparisons": comparisons}
    contract = hashlib.sha256(json.dumps(provenance, sort_keys=True).encode()).hexdigest()
    if args.output.exists():
        raise FileExistsError("Output appeared during encoding; refusing to overwrite it")
    write_bundle(args.output, features, rows,
                 {"kind": "teacher", "feature_contract": contract, "provenance": provenance})
    report = {"passed": True, "rows": len(rows), "dimension": 4096, "contract": contract,
              "baseline_resources": baseline, "observed_peaks": peaks,
              "resource_minima": minima,
              "load_seconds": loaded - started, "encode_timings": timings,
              "cached_comparisons": comparisons, "provenance": provenance,
              "dataset": str(args.dataset.resolve()), "output": str(args.output)}
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    finished.set()
    print(json.dumps({"passed": True, "output": report["output"], "contract": contract}), flush=True)


def run(argv=None, generic=False):
    try:
        main(argv, generic=generic)
    except SystemExit:
        raise
    except BaseException:
        RESOURCE_STATE["exception"] = traceback.format_exc()
        raise
    finally:
        if FINALIZE_RESOURCES is not None:
            FINALIZE_RESOURCES()
        elif WRITE_RESOURCES:
            OUTPUT.mkdir(parents=True, exist_ok=True)
            RESOURCE_PATH.write_text(json.dumps(RESOURCE_STATE, indent=2), encoding="utf-8")


if __name__ == "__main__":
    run()
