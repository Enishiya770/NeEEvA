"""Validate the real shared-model endpoint; never starts a model or uses fallback."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError
import argparse
import base64
import hashlib
import io
import json
import time
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0"
MODEL_SHA = "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://127.0.0.1:18082")
    parser.add_argument("--output", type=Path, default=ROOT / "reports" / "shared-qwen-http.json")
    args = parser.parse_args()

    def call(path, body=None, timeout=90):
        data = None if body is None else json.dumps(body, ensure_ascii=False).encode()
        req = Request(args.base_url + path, data=data, headers={"Content-Type": "application/json"})
        with urlopen(req, timeout=timeout) as response:
            return json.load(response)

    def feature(text, request_id):
        result = call("/neeeva/motion-features", {"text": text, "request_id": request_id, "feature_contract": CONTRACT})
        assert result["feature_contract"] == CONTRACT and result["model_sha256"] == MODEL_SHA
        assert result["request_id"] == request_id and result["text"] == text
        assert result["shared_model"] is True and result["feature_source"] == "live-qwen"
        assert result["dimension"] == 2048 and result["context_tokens"] == 512
        assert result["pooling"] == "last_input_token_raw"
        assert result["template"] == "Motion description: {text}\nRepresentation:"
        assert result["chat_slots"] == 3 and result["chat_context_tokens_per_slot"] == 65536
        vector = np.asarray(result["embedding"], dtype=np.float32)
        assert vector.shape == (2048,) and np.isfinite(vector).all()
        return vector, result

    def invalid(body):
        try:
            call("/neeeva/motion-features", body)
        except HTTPError as error:
            assert error.code == 400
            return {"status": error.code, "body": json.load(error)}
        raise AssertionError("Invalid feature request was accepted")

    report = {"schema": 1, "date": time.strftime("%Y-%m-%d"), "base_url": args.base_url,
              "feature_contract": CONTRACT, "model_sha256": MODEL_SHA,
              "validation_scope": "Real HTTP, full configured 3 x 65536 chat capacity and vision; not a 65536-token occupancy stress test"}
    for filename, field in [("provenance.json", "runtime_provenance"), ("process-evidence.json", "process_evidence")]:
        evidence = ROOT / "runtime/server-feature-evidence" / filename
        if evidence.exists():
            report[field] = json.loads(evidence.read_text(encoding="utf-8-sig"))
    rows = [json.loads(line) for line in (ROOT / "runtime/remote-probe/dataset.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    cached = np.fromfile(ROOT / "runtime/remote-probe/features.f32", dtype="<f4").reshape(-1, 2048)
    indices = sorted(set([0, 1, 4, 10, 48, 53, 1999] + list(range(0, len(rows), 91))))
    compatibility = []
    for index in indices:
        vector, result = feature(rows[index]["text"], f"compat-{index}")
        error = float(np.max(np.abs(vector - cached[index])))
        cosine = float(vector @ cached[index] / (np.linalg.norm(vector) * np.linalg.norm(cached[index])))
        assert error <= 1e-4, (index, error)
        compatibility.append({"id": rows[index]["id"], "max_abs_error": error, "cosine": cosine,
                              "timings": result["timings"]})
    report["cached_feature_compatibility"] = compatibility
    first, first_meta = feature(rows[53]["text"], "repeat-a")
    feature(rows[4]["text"], "repeat-b")
    repeat, _ = feature(rows[53]["text"], "repeat-c")
    report["repeat_max_abs"] = float(np.max(np.abs(first - repeat)))
    assert report["repeat_max_abs"] <= 1e-4
    report["endpoint_metadata"] = {k: v for k, v in first_meta.items() if k != "embedding"}
    report["invalid_contract"] = invalid({"text": "A person waves.", "feature_contract": "wrong"})
    report["oversize"] = invalid({"text": "a" * 1025, "feature_contract": CONTRACT})

    # Real chat slots retain their cached prompts across action extraction.
    prompt = "A careful assistant answers briefly. User: What is 7 plus 5? Assistant:"
    chat_body = {"prompt": prompt, "id_slot": 0, "n_predict": 24, "temperature": 0,
                 "seed": 123, "stream": False, "cache_prompt": True, "return_tokens": True}
    baseline = call("/completion", chat_body)
    before = call("/slots")
    feature(rows[48]["text"], "between-chat")
    after = call("/slots")
    assert before == after, "Feature call modified idle chat slot state"
    repeated_chat = call("/completion", chat_body)
    assert baseline["tokens"] == repeated_chat["tokens"]
    report["chat_slot_state_unchanged"] = True
    report["chat_tokens_equal"] = baseline["tokens"] == repeated_chat["tokens"]
    # Repeating a shorter prompt may rewind hybrid recurrent state and re-prefill.
    # Prove cache retention with a true continuation using exact token IDs.
    prompt_ids = call("/tokenize", {"content": prompt, "add_special": True})["tokens"]
    prefix = call("/completion", {**chat_body, "prompt": prompt_ids, "n_predict": 1, "cache_prompt": False})
    feature(rows[53]["text"], "before-cached-continuation")
    continued = call("/completion", {**chat_body, "prompt": prompt_ids + prefix["tokens"], "n_predict": 8})
    report["cached_continuation_timings"] = continued["timings"]
    assert continued["timings"]["cache_n"] >= len(prompt_ids), continued["timings"]
    report["chat_cached_tokens_after_feature"] = continued["timings"]["cache_n"]
    report["slots"] = [{"id": s["id"], "n_ctx": s["n_ctx"]} for s in after]

    # Queue priority: request while one chat is actually generating. Request 2 is
    # rejected while request 1 waits; features only complete after chat is idle.
    with ThreadPoolExecutor(max_workers=4) as executor:
        long_chat = executor.submit(call, "/completion", {**chat_body, "id_slot": 1, "prompt": "List the integers from 1 through 150, one per line:", "n_predict": 160})
        deadline = time.monotonic() + 10
        while not any(s["is_processing"] for s in call("/slots")):
            assert time.monotonic() < deadline and not long_chat.done(), "Could not observe active chat"
            time.sleep(.01)
        pending = executor.submit(feature, rows[1]["text"], "queued-motion")
        time.sleep(.12)
        saturated_status = None
        try:
            feature(rows[10]["text"], "queue-overflow")
        except HTTPError as error:
            saturated_status = error.code
            assert error.code == 503
        long_chat.result()
        _, queued = pending.result()
        assert saturated_status == 503
        assert queued["timings"]["queue_ms"] >= 100
        report["scheduling"] = {"queue_full_http_status": saturated_status, "queued_feature_timings": queued["timings"], "policy": "idle-chat-only, server inference thread"}

    # A synthetic color image verifies the real mmproj path without user data.
    from PIL import Image
    image = Image.new("RGB", (224, 224), (255, 0, 0))
    stream = io.BytesIO()
    image.save(stream, format="PNG")
    data_url = "data:image/png;base64," + base64.b64encode(stream.getvalue()).decode()
    vision = call("/v1/chat/completions", {"messages": [{"role": "user", "content": [
        {"type": "text", "text": "What single color fills this image? Reply with the color only."},
        {"type": "image_url", "image_url": {"url": data_url}}]}],
        "max_tokens": 48, "temperature": 0, "chat_template_kwargs": {"enable_thinking": False}})
    response = vision["choices"][0]["message"]["content"]
    assert "red" in response.lower(), response
    report["vision"] = {"synthetic_image_sha256": hashlib.sha256(stream.getvalue()).hexdigest(), "response": response, "usage": vision.get("usage")}
    _, post_vision = feature(rows[53]["text"], "after-vision")
    report["feature_after_vision"] = post_vision["shared_model"]
    report["passed"] = True
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "compatibility_rows": len(indices), "report": str(args.output)}))


if __name__ == "__main__":
    main()
