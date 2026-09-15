"""Replay one saved production visual autonomy preprobe; optionally one no-image control.

No Unity execution, formal response, motion, speech, retry, cancellation or service lifecycle.
Without --run, only prepares local evidence and does not perform HTTP requests.
"""
from __future__ import annotations

import argparse
import base64
import copy
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import time
import uuid

import httpx

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_SOURCE = ROOT / "Server/ARDY/runtime/scene-vision-20260913/integration-r9"
BASE = "http://127.0.0.1:8080"
STATUSES = {"continue", "waiting_tool", "waiting_user", "closed", "none"}


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def read_json(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def image_parts(payload: dict) -> list[dict]:
    return [part for message in payload["messages"] if isinstance(message.get("content"), list)
            for part in message["content"] if part.get("type") == "image_url"]


def schema_review(content: str) -> dict:
    """Inspect the production extraction boundary plus the stricter prompt schema.

    JsonUtility coercion/defaulting is deliberately not emulated. These are Python
    structural checks, not a claim that Unity's C# parser has executed this response.
    """
    begin, end = content.find("{"), content.rfind("}")
    result = {"jsonExtracted": False, "promptSchemaValid": False,
              "productionExtractionBoundaryMatched": False,
              "reviewKind": "Python structural checks; actual Unity parser not invoked"}
    if begin < 0 or end <= begin:
        result["errors"] = ["No complete JSON object at the production first/last brace boundary."]
        return result
    candidate = content[begin:end + 1]
    result["productionExtractionBoundaryMatched"] = '"proceed"' in candidate.lower()
    try:
        decision = json.loads(candidate, parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
    except (ValueError, TypeError) as error:
        result["errors"] = ["Invalid extracted JSON: " + str(error)]
        return result
    result["jsonExtracted"] = True
    result["decision"] = decision
    errors = []
    if not isinstance(decision, dict):
        errors.append("Decision is not an object.")
    else:
        if type(decision.get("proceed")) is not bool:
            errors.append("proceed must be a JSON boolean.")
        for name in ("work_evidence", "work_status", "intent", "novelty"):
            if not isinstance(decision.get(name), str):
                errors.append(name + " must be a string.")
        if not isinstance(decision.get("work_status"), str) or decision["work_status"] not in STATUSES:
            errors.append("work_status is outside the production prompt enum.")
        wait = decision.get("wait_seconds")
        if type(wait) not in (int, float) or not math.isfinite(wait) or wait < 0:
            errors.append("wait_seconds must be a finite nonnegative number.")
        if content[:begin].strip() or content[end + 1:].strip():
            errors.append("Content outside the compact JSON object violates the prompt format.")
    result.update(promptSchemaValid=not errors, errors=errors)
    return result


def prepare(source: Path, text_control: bool) -> tuple[list[tuple[str, dict]], dict]:
    raw = (source / "real-role-autonomy-request.json").read_bytes()
    original = json.loads(raw.decode("utf-8-sig"))
    integration = read_json(source / "report.json")
    observation = read_json(source / "observation.json")
    capture = (source / "capture.jpg").read_bytes()
    if integration.get("status") != "passed":
        raise ValueError("Use a passed graphical integration export.")
    export = integration["realRoleAutonomyExport"]
    # Unity export hashes the serialized JSON before appending its single final newline.
    if digest(raw.rstrip(b"\r\n")) != export["sha256"]:
        raise ValueError("Original production request does not match its integration report digest.")
    images = image_parts(original)
    if len(images) != 1 or not 32 <= len(capture) <= 2 * 1024 * 1024:
        raise ValueError("Require exactly one bounded original captured image.")
    url = images[0]["image_url"]["url"]
    if not url.startswith("data:image/jpeg;base64,") or base64.b64decode(url.split(",", 1)[1], validate=True) != capture:
        raise ValueError("Export image bytes do not match capture.jpg.")
    capture_id = observation["captureId"]
    if capture_id != export["captureId"] or capture_id != integration["captureId"]:
        raise ValueError("Observation identity differs from the production export.")
    parameters = {key: value for key, value in original.items() if key != "messages"}
    if parameters.get("stream") is not False or parameters.get("id_slot") != 1:
        raise ValueError("Expected the bounded production nonstreaming auxiliary slot 1 request.")
    if type(parameters.get("max_tokens")) is not int or not 1 <= parameters["max_tokens"] <= 256:
        raise ValueError("Production output budget must be bounded to 256 tokens.")
    if original["messages"][-1]["role"] != "user":
        raise ValueError("Expected the production autonomy input as the last user message.")
    if any(message["role"] != "system" for message in original["messages"][:-1]):
        raise ValueError("This probe expects the exported empty-history autonomy opportunity.")
    input_text = "\n".join(part.get("text", "") for part in original["messages"][-1]["content"])
    if capture_id not in input_text or "现在没有新的用户发言" not in input_text:
        raise ValueError("The actual capture identity and no-new-user autonomy input must be preserved.")
    replay_scope = (
        "[离线历史回放边界；本条仅说明验证条件，不是角色的用户发言或动作命令]\n"
        "随后原样提供的是一次已保存的自主预判输入与当时的角色资料。"
        "其中感知帧、图像年龄、视点兼容性和‘当前’均为采集时记录，不是现在的现场状态。"
        "采集标识=" + capture_id + "；采集时刻=" + observation["capturedAtUtc"] + "。"
        "请仅回放判断那次没有新用户发言的自主机会，维持原来的内部 JSON 格式。"
        "proceed 可以是真或假，不需要为了验证而开始说话或行动。"
        "本次没有正式回复、Unity 控制或执行工具；意图不会被执行，不得宣称已经执行。"
    )
    requests = []
    for name in (["saved-visual-autonomy", "saved-autonomy-no-image-control"] if text_control else ["saved-visual-autonomy"]):
        payload = copy.deepcopy(original)
        control = name.endswith("control")
        scope = replay_scope + (
            "\n此请求是无图对照：本次没有任何图像附件。下方保留的历史记录中 hasImage=true 和附件说明只描述原始记录，"
            "它们不代表本次提供了图像；不要据此推测具体可见物体。"
            if control else "\n本次唯一图片附件是该采集时刻保存的 RGB，可用于判断当时所见；不证明现在仍可见。"
        )
        payload["messages"].insert(len(payload["messages"]) - 1, {"role": "system", "content": scope})
        if control:
            payload["messages"][-1]["content"] = [part for part in payload["messages"][-1]["content"] if part.get("type") != "image_url"]
        assert {key: value for key, value in payload.items() if key != "messages"} == parameters
        assert payload["messages"][:-2] == original["messages"][:-1]
        assert [p for p in payload["messages"][-1]["content"] if p.get("type") == "text"] == [p for p in original["messages"][-1]["content"] if p.get("type") == "text"]
        requests.append((name, payload))
    provenance = {
        "sourceDirectory": str(source.resolve()), "productionRequestSha256": digest(raw),
        "productionExportJsonSha256": export["sha256"], "imageSha256": digest(capture),
        "captureId": capture_id, "capturedAtUtc": observation["capturedAtUtc"],
        "parametersUnchanged": parameters, "actualRoleConfiguration": integration["actualRoleConfiguration"],
        "originalSystemMessagesAndAutonomyTextUnchanged": True,
        "wireDifferences": "One inserted system message marks all original time/current-view statements as archived replay. Optional control also removes the sole image and explicitly states no image was supplied. No other request fields changed.",
        "parserSource": "Assets/AIChatTookit/Scripts/Chat/ChatSample.cs: TryParseAutonomyIntentDecision and BuildAutonomyIntentProbePrompt",
        "sourceCodeSha256": digest((ROOT / "Assets/AIChatTookit/Scripts/Chat/ChatSample.cs").read_bytes()),
    }
    return requests, provenance


def wait_for_unity_moving(log: Path, seconds: float = 60) -> dict:
    """Wait for a launched-isolated-run log, never create/launch/control Unity.

    The caller must supply a fresh per-run log path. We record any preexisting
    moving line as such, rather than claiming it happened after this wait began.
    """
    started = time.perf_counter()
    first = True
    while time.perf_counter() - started < seconds:
        if log.is_file():
            with log.open("rb") as stream:
                stream.seek(max(0, log.stat().st_size - 512 * 1024))
                tail = stream.read().decode("utf-8", errors="replace")
            lines = [line for line in tail.splitlines() if "[Room/Dialogue]" in line and "phase=moving" in line]
            if lines:
                return {"path": str(log.resolve()), "matchedLine": lines[-1], "seconds": time.perf_counter() - started,
                        "foundOnFirstRead": first, "scope": "Log observation only, not a lock or guarantee of ongoing movement."}
        first = False
        time.sleep(.25)
    raise TimeoutError("No [Room/Dialogue] phase=moving line observed in the supplied Unity log within 60 seconds.")


def run(args: argparse.Namespace) -> int:
    requests, provenance = prepare(args.source, args.text_control or args.only_text_control)
    if args.only_text_control:
        if args.with_motion_feature:
            raise ValueError("A text-only control cannot own the visual concurrency feature probe.")
        requests = [item for item in requests if item[0].endswith("control")]
    if args.output.exists() and any(args.output.iterdir()):
        raise FileExistsError("Use an empty evidence directory.")
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"status": "prepared", "startedUtc": datetime.now(timezone.utc).isoformat(),
              "endpoint": BASE, "provenance": provenance, "modelRequests": 0, "results": [],
              "scope": "At most one saved production visual autonomy preprobe, one optional sequential no-image control, and one optional concurrent motion-feature extraction. No user action command and no execution of the returned intent.",
              "limitations": ["Single offline replay does not establish live autonomous behavior or generalization.",
                  "The original production max_tokens and sampling are retained; length truncation is a failed schema/transport check, not repaired by retries.",
                  "A valid proceed=false is allowed. Image-grounded intent quality needs review against the saved capture; schema success is not semantic correctness.",
                  "No-image control is a single qualitative comparison, not a statistical causal test."]}
    for name, payload in requests:
        write(args.output / (name + "-request.json"), payload)
    feature_request = None
    if args.with_motion_feature:
        from validate_scene_perception_qwen import FEATURE_CONTRACT
        feature_request = {"text": "A person walks forward at a comfortable pace while keeping both hands relaxed.",
                           "request_id": "scene-autonomy-feature-" + uuid.uuid4().hex, "feature_contract": FEATURE_CONTRACT}
        write(args.output / "concurrent-motion-feature-request.json", feature_request)
    report["plannedRequests"] = len(requests) + int(feature_request is not None)
    report["featureDispatchDelaySeconds"] = .2 if feature_request is not None else 0
    report["waitUnityLog"] = str(args.wait_unity_log) if args.wait_unity_log else None
    write(args.output / "report.json", report)
    if not args.run:
        print(json.dumps({"status": "prepared", "plannedRequests": report["plannedRequests"], "submittedRequests": 0, "output": str(args.output)}, ensure_ascii=False))
        return 0
    begun = time.perf_counter()
    try:
        if args.wait_unity_log:
            report["unityMovingObservation"] = wait_for_unity_moving(args.wait_unity_log)
            write(args.output / "report.json", report)
        with httpx.Client(timeout=httpx.Timeout(90, connect=5), follow_redirects=False) as client:
            health = client.get(BASE + "/health")
            health.raise_for_status()
            write(args.output / "health-before.json", health.json())
            if health.json().get("status") != "ok":
                raise RuntimeError("Qwen is not healthy; no inference submitted.")

            def send(item: dict, endpoint: str, payload: dict, feature: bool = False) -> None:
                started = time.perf_counter()
                item["submittedSeconds"] = started - begun
                try:
                    response = client.post(BASE + endpoint, json=payload)
                    item.update(seconds=time.perf_counter() - started, statusCode=response.status_code)
                    (args.output / (item["name"] + "-response.txt")).write_text(response.text, encoding="utf-8")
                    response.raise_for_status()
                    body = response.json()
                    if feature:
                        from validate_scene_perception_qwen import validate_feature
                        vector = validate_feature(body, payload)
                        item.update(featureContractValid=True, dimension=int(vector.shape[0]),
                                    modelSha256=body["model_sha256"], requestId=body["request_id"],
                                    timings=body.get("timings"))
                    else:
                        choice = body["choices"][0]
                        content = choice["message"].get("content", "") or ""
                        item.update(content=content, finishReason=choice.get("finish_reason"),
                                    responseId=body.get("id"), usage=body.get("usage"), timings=body.get("timings"),
                                    schemaReview=schema_review(content))
                        item["completedValidJson"] = item["finishReason"] == "stop" and item["schemaReview"]["promptSchemaValid"]
                except Exception as error:
                    item["error"] = repr(error)
                finally:
                    item["finishedSeconds"] = time.perf_counter() - begun
                    item["seconds"] = item["finishedSeconds"] - item["submittedSeconds"]

            for name, payload in requests:
                slots_response = client.get(BASE + "/slots")
                slots_response.raise_for_status()
                slots = slots_response.json()
                write(args.output / (name + "-slots-before.json"), slots)
                if not isinstance(slots, list) or not slots or any(slot.get("is_processing") is not False for slot in slots):
                    raise RuntimeError("Qwen slots busy or unknown; no further inference submitted.")
                if not any(slot.get("id") == payload["id_slot"] for slot in slots):
                    raise RuntimeError("Original production auxiliary slot 1 is unavailable.")
                item = {"name": name, "imageCount": len(image_parts(payload)), "submittedUtc": datetime.now(timezone.utc).isoformat()}
                report["results"].append(item)
                report["modelRequests"] += 1
                if feature_request is not None and name == "saved-visual-autonomy":
                    feature_item = {"name": "concurrent-motion-feature", "kind": "feature"}
                    report["results"].append(feature_item)
                    report["modelRequests"] += 1
                    write(args.output / "report.json", report)
                    with ThreadPoolExecutor(max_workers=2) as executor:
                        image_future = executor.submit(send, item, "/v1/chat/completions", payload)
                        # Let the full role/image request enter processing before
                        # probing feature latency; completion timing is still
                        # evidence of client overlap, not a GPU scheduling claim.
                        time.sleep(.2)
                        feature_future = executor.submit(send, feature_item, "/neeeva/motion-features", feature_request, True)
                        image_future.result()
                        feature_future.result()
                    report["clientRequestsOverlappedSeconds"] = max(0, min(item["finishedSeconds"], feature_item["finishedSeconds"]) - max(item["submittedSeconds"], feature_item["submittedSeconds"]))
                else:
                    write(args.output / "report.json", report)
                    send(item, "/v1/chat/completions", payload)
                write(args.output / "report.json", report)
                if any("error" in result for result in report["results"]):
                    raise RuntimeError("One bounded inference request failed; see preserved per-request evidence. No retries.")
            report["status"] = "passed-structure-review-autonomy-manually" if all(item.get("featureContractValid", item.get("completedValidJson", False)) for item in report["results"]) else "failed-completion-or-schema"
    except Exception as error:
        report.update(status="failed", error=repr(error))
    finally:
        report["elapsedSeconds"] = time.perf_counter() - begun
        write(args.output / "report.json", report)
    print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"], "output": str(args.output)}, ensure_ascii=False))
    return 0 if report["status"].startswith("passed-") else 1


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--text-control", action="store_true", help="Add exactly one sequential no-image control; never retried.")
    parser.add_argument("--only-text-control", action="store_true", help="Submit only the no-image control after a separately completed visual replay; do not repeat the image request.")
    parser.add_argument("--with-motion-feature", action="store_true", help="Alongside the unique image request, extract one shared-Qwen feature with contract verification.")
    parser.add_argument("--wait-unity-log", type=Path, help="Wait at most 60 seconds for [Room/Dialogue] phase=moving in this fresh isolated-run log; never launches Unity.")
    parser.add_argument("--run", action="store_true", help="Submit the prepared bounded requests to the existing localhost 8080 Qwen tunnel.")
    raise SystemExit(run(parser.parse_args()))


if __name__ == "__main__":
    main()
