"""Freeze a production formal-context pair; explicit --execute runs exactly ABBA.

No generation parameter, message, image, slot or original request byte is changed.
Both arms already use the new exact fact compactor. The comparison isolates the
scoped spatial reply context versus the old full motion context, not total latency
improvement, a cold-cache experiment, speech honesty verification, or Unity/TTS.
"""
from __future__ import annotations

import argparse
import base64
import codecs
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import statistics
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
MONO = Path("E:/Unity_Data/2022.3.22f1/Editor/Data/MonoBleedingEdge")
ENDPOINT = "http://127.0.0.1:8080"
ORDER = [("01-A-before", "before"), ("02-B-after", "after"),
         ("03-B-after", "after"), ("04-A-before", "before")]
PARSER_SOURCES = ["Assets/AIChatTookit/Scripts/Chat/RoleOutputChannels.cs",
                  "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs"]
PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs"
HARNESS = r'''
using System; using System.IO; using System.Text; using Newtonsoft.Json; using Newtonsoft.Json.Linq;
public sealed class SceneLatencySpeechProbe {
__ACTUAL_THINK_FILTER__
 public static int Main(string[] args) {
  try {
   var source=JObject.Parse(File.ReadAllText(args[0])); double at=0; double? first=null;
   var instance=new SceneLatencySpeechProbe(); instance.ResetThinkStrip();
   var channels=new RoleOutputChannels(part=> { if(first==null && !string.IsNullOrWhiteSpace(part.Text)) first=at; });
   foreach(var delta in (JArray)source["deltas"]) {
    at=(double)delta["elapsedSeconds"];
    string clean=instance.StripLeadingThinkBlock((string)delta["text"]);
    if(clean.Length>0) channels.Push(clean);
   }
   at=(double)source["completeSeconds"]; channels.Finish();
   var result=new JObject {
    ["firstPublicChannelSpeechDeltaSeconds"]=first.HasValue?new JValue(first.Value):JValue.CreateNull(),
    ["publicCandidateSpeech"]=channels.Speech,
    ["privateCharactersExcluded"]=channels.PrivateCharacters,
    ["hasActions"]=channels.HasActions,["hasMalformedTool"]=channels.HasMalformedTool,
    ["hasInvalidLanguage"]=channels.HasInvalidLanguage,["hasUndeclaredSpeech"]=channels.HasUndeclaredSpeech,
    ["hasInvalidSpeechPhase"]=channels.HasInvalidSpeechPhase,
    ["speechDependency"]=channels.SpeechDependency.ToString(),
    ["publicSpeechApproved"]=false,["actionsExecuted"]=false,["ttsExecuted"]=false,
    ["scope"]="Actual production leading-think filter and RoleOutputChannels replay at client delta receive times. Candidate speech, before room factual review and TTS; not audible onset."
   };
   Console.WriteLine(result.ToString(Formatting.None)); return 0;
  } catch(Exception error) { Console.Error.WriteLine(error); return 1; }
 }
}
'''


def utc():
    return datetime.now(timezone.utc).isoformat()


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def strict_json(raw):
    def pairs(items):
        value = {}
        for key, item in items:
            if key in value:
                raise ValueError("Duplicate JSON property: " + key)
            value[key] = item
        return value
    return json.loads(raw, object_pairs_hook=pairs)


def image_urls(value):
    if isinstance(value, dict):
        if value.get("type") == "image_url":
            yield value["image_url"]["url"]
        for child in value.values():
            yield from image_urls(child)
    elif isinstance(value, list):
        for child in value:
            yield from image_urls(child)


def verify_pair(directory):
    manifest = strict_json((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if not (manifest.get("productionContextHelper") == "BuildFormalResponseRequestContext"
            and manifest.get("roomTaskPrepared") is True and manifest.get("roomTaskIntent") == "look-at"
            and manifest.get("actualFact", {}).get("phase") == "observed"
            and manifest.get("actualFact", {}).get("resultCaptureId") == manifest.get("captureId")
            and manifest.get("nativeMovementClaimed") is False):
        raise ValueError("Expected the isolated real completed-look production fixture manifest")
    arms = {}
    for arm in ("before", "after"):
        filename = manifest[arm + "RequestFile"]
        if Path(filename).name != filename:
            raise ValueError("Request must be a direct file in the pair directory")
        raw = (directory / filename).read_bytes()
        body = strict_json(raw.decode("utf-8-sig"))
        if body.get("stream") is not True or body.get("enable_thinking") is not False:
            raise ValueError("Expected production nonthinking SSE; never rewrite generation parameters")
        if body.get("stream_options", {}).get("include_usage") is not True:
            raise ValueError("Production pair must already request stream usage")
        images = list(image_urls(body))
        if len(images) != 1 or not images[0].startswith("data:image/"):
            raise ValueError("Expected exactly one embedded isolated scene image")
        if manifest["captureId"] not in json.dumps(body, ensure_ascii=False):
            raise ValueError("Current capture identity missing from production facts")
        arms[arm] = {"raw": raw, "body": body, "images": images}
    before, after = arms["before"]["body"], arms["after"]["body"]
    if {k: v for k, v in before.items() if k != "messages"} != {k: v for k, v in after.items() if k != "messages"}:
        raise ValueError("Generation parameters differ between arms")
    left, right = before["messages"], after["messages"]
    if len(left) != len(right) or [m["role"] for m in left] != [m["role"] for m in right]:
        raise ValueError("Message ownership/order differs")
    if [m for m in left if m["role"] != "system"] != [m for m in right if m["role"] != "system"]:
        raise ValueError("User/history/image changed between arms")
    if arms["before"]["images"] != arms["after"]["images"] or left == right:
        raise ValueError("Image changed or there is no context comparison")
    return manifest, arms


def compile_parser(output):
    parser = output / "parser"
    parser.mkdir(exist_ok=True)
    provider = (ROOT / PROVIDER).read_text(encoding="utf-8-sig")
    start = provider.index("    private int m_ThinkStripState = 0;")
    end = provider.index("    private static string StripLeadingThinkBlock(string text, bool wholeText)", start)
    fragment = provider[start:end]
    harness = HARNESS.replace("__ACTUAL_THINK_FILTER__", fragment)
    (parser / "SceneLatencySpeechProbe.cs").write_text(harness, encoding="utf-8")
    shutil.copyfile(ROOT / "Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll", parser / "Newtonsoft.Json.dll")
    exe = parser / "scene-latency-speech-probe.exe"
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
               "/r:" + str(parser / "Newtonsoft.Json.dll"), "/out:" + str(exe),
               *[str(ROOT / p) for p in PARSER_SOURCES], str(parser / "SceneLatencySpeechProbe.cs")]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (parser / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production speech parser failed compilation; see parser/compile.log")
    return exe, {p: sha((ROOT / p).read_bytes()) for p in PARSER_SOURCES + [PROVIDER]}, sha(fragment.encode())


def replay(exe, data, case_directory):
    path = case_directory / "delta-timings.json"
    write_json(path, data)
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(exe), str(path)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Speech replay failed: " + result.stderr[:1000])
    return json.loads(result.stdout.lstrip("\ufeff"))


def idle_slots():
    with urllib.request.urlopen(ENDPOINT + "/slots", timeout=5) as response:
        slots = json.load(response)
    if not isinstance(slots, list) or not slots or any(not isinstance(s, dict) or s.get("is_processing") is not False for s in slots):
        raise RuntimeError("Model activity is busy/unknown; no request submitted and no automatic retry")
    return [{"id": s.get("id"), "is_processing": False} for s in slots]


def server_performance(events):
    # Missing provider counters are unknown, not zero. Total prompt tokens include
    # cache hits; timings.prompt_n is the separate actual prefill work counter.
    result = {key: None for key in ("promptTokensTotal", "completionTokens", "promptEvaluatedTokens",
                                  "prefillMilliseconds", "generationMilliseconds", "cachedTokensReported", "cacheTokensAtTiming")}
    for event in events:
        usage = event.get("usage") if isinstance(event.get("usage"), dict) else {}
        timings = event.get("timings") if isinstance(event.get("timings"), dict) else {}
        details = usage.get("prompt_tokens_details") if isinstance(usage.get("prompt_tokens_details"), dict) else {}
        candidates = {"promptTokensTotal": usage.get("prompt_tokens"), "completionTokens": usage.get("completion_tokens"),
                      "promptEvaluatedTokens": timings.get("prompt_n"), "prefillMilliseconds": timings.get("prompt_ms"),
                      "generationMilliseconds": timings.get("predicted_ms"), "cachedTokensReported": details.get("cached_tokens"),
                      "cacheTokensAtTiming": timings.get("cache_n")}
        for key, value in candidates.items():
            if isinstance(value, (int, float)) and not isinstance(value, bool) and 0 <= value < float("inf"):
                result[key] = value
    return result


def capture(raw_request, directory, timeout):
    (directory / "request.json").write_bytes(raw_request)
    request = urllib.request.Request(ENDPOINT + "/v1/chat/completions", raw_request,
                                    {"Content-Type": "application/json", "Accept": "text/event-stream", "Authorization": "Bearer ollama"})
    raw = bytearray()
    deltas, packets, metadata = [], [], []
    pending = ""
    decoder = codecs.getincrementaldecoder("utf-8")()
    first_byte = first_content = headers_at = None
    finish = None
    done = False
    status = None
    started_utc = utc()
    start = time.perf_counter()  # Immediately before actual HTTP submission, after all preparation/idle reads.

    def receive(line, elapsed):
        nonlocal finish, done, first_content
        if not line.startswith("data:"):
            return
        payload = line[5:].strip()
        if payload == "[DONE]":
            done = True
            return
        event = strict_json(payload)
        if event.get("error") is not None:
            raise RuntimeError("SSE provider error; raw event retained locally")
        fields = {k: event[k] for k in ("usage", "timings", "timing", "performance", "cache") if k in event}
        if fields:
            metadata.append({"elapsedSeconds": elapsed, **fields})
        choices = event.get("choices") or []
        if len(choices) > 1:
            raise RuntimeError("Unexpected multiple completion choices")
        if choices:
            choice = choices[0]
            finish = choice.get("finish_reason") or finish
            text = (choice.get("delta") or {}).get("content")
            if isinstance(text, str) and text:
                if first_content is None:
                    first_content = elapsed
                deltas.append({"elapsedSeconds": elapsed, "text": text})

    error = None
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            headers_at = time.perf_counter() - start
            status = response.status
            if "text/event-stream" not in response.headers.get("Content-Type", "").lower():
                raise RuntimeError("Expected SSE response")
            while True:
                packet = response.read1(65536)
                elapsed = time.perf_counter() - start
                if not packet:
                    break
                if first_byte is None:
                    first_byte = elapsed
                raw.extend(packet)
                packets.append({"elapsedSeconds": elapsed, "base64": base64.b64encode(packet).decode("ascii")})
                if elapsed > timeout or len(raw) > 16 * 1024 * 1024:
                    raise TimeoutError("Bounded stream duration/body limit reached; no retry")
                pending += decoder.decode(packet)
                while "\n" in pending:
                    line, pending = pending.split("\n", 1)
                    receive(line.rstrip("\r"), elapsed)
            pending += decoder.decode(b"", final=True)
            if pending.strip():
                receive(pending.rstrip("\r"), time.perf_counter() - start)
    except Exception as exc:
        status = getattr(exc, "code", status)
        error = type(exc).__name__ + ": " + str(exc)[:1000]
    complete = time.perf_counter() - start
    (directory / "response.sse").write_bytes(raw)
    write_json(directory / "wire-chunks.json", packets)
    result = {"httpStartedAtUtc": started_utc, "httpStatus": status, "finishReason": finish,
              "doneSeen": done, "error": error, "metadataEvents": metadata,
              "serverPerformance": server_performance(metadata),
              "requestSha256": sha(raw_request), "responseSseSha256": sha(raw),
              "clientTimings": {"headersSeconds": headers_at, "firstResponseBodyByteSeconds": first_byte,
                                "firstContentDeltaSeconds": first_content, "completeSeconds": complete},
              "timingScope": "All receive times start immediately before HTTP submission; first byte is response body, not TCP/header arrival. No EOU, planning, speech review or TTS included."}
    return result, {"deltas": deltas, "completeSeconds": complete}


def summaries(cases):
    result = {}
    for arm in ("before", "after"):
        rows = [c for c in cases if c["arm"] == arm and c["httpStatus"] == 200 and c["finishReason"] == "stop" and c["doneSeen"] and not c["error"]]
        arm_result = {"completedRequests": len(rows)}
        for metric in ("headersSeconds", "firstResponseBodyByteSeconds", "firstContentDeltaSeconds", "completeSeconds", "firstPublicChannelSpeechDeltaSeconds"):
            values = [c["clientTimings"].get(metric) for c in rows]
            values = [v for v in values if isinstance(v, (int, float))]
            arm_result[metric] = {"n": len(values), "median": statistics.median(values) if values else None, "values": values}
        result[arm] = arm_result
    return result


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--pair-directory", type=Path, required=True)
    ap.add_argument("--output", type=Path, required=True)
    ap.add_argument("--execute", action="store_true", help="Explicitly submit the fixed four sequential requests once")
    ap.add_argument("--timeout", type=int, default=60, help="HTTP/stream bound in seconds, 20..90; no generation-parameter change")
    args = ap.parse_args()
    if not 20 <= args.timeout <= 90:
        raise ValueError("timeout must be 20..90 seconds")
    source, output = args.pair_directory.resolve(), args.output.resolve()
    manifest, arms = verify_pair(source)
    output.mkdir(parents=True, exist_ok=True)
    if (output / "execution.once.json").exists():
        raise RuntimeError("This fixed ABBA probe already reserved its execution; no rerun/retry")
    frozen = {arm: sha(arms[arm]["raw"]) for arm in ("before", "after")}
    old_report = output / "report.json"
    if old_report.exists():
        prior = json.loads(old_report.read_text(encoding="utf-8"))
        if prior.get("status") != "prepared" or prior.get("requestHashes") != frozen:
            raise RuntimeError("Output is not this unchanged prepared pair")
    exe, parser_sources, filter_hash = compile_parser(output)
    checks = output / "parser-self-check"; checks.mkdir(exist_ok=True)
    check = replay(exe, {"deltas": [{"elapsedSeconds": 1, "text": "<think>private"},
                                   {"elapsedSeconds": 2, "text": "</think>\n<speech mode=\"independent\"/><lang code=\"en\"/>Hello"}], "completeSeconds": 3}, checks)
    if check["firstPublicChannelSpeechDeltaSeconds"] != 2 or check["publicCandidateSpeech"] != "Hello":
        raise RuntimeError("Production parser timing/private-channel fixture failed")
    report = {"status": "prepared", "sourcePair": str(source), "sourceManifest": manifest,
              "requestHashes": frozen, "parserSources": parser_sources, "actualThinkFilterSha256": filter_hash,
              "scope": __doc__, "order": [key for key, _ in ORDER], "generationOverrides": {}, "modelRequests": 0,
              "limits": "Four fixed sequential samples only. ABBA exposes order/cache effects but does not erase caches or prove population performance. Both arms already have the new exact compactor. Candidate public text is not reviewed/approved/audible speech.",
              "cases": [], "preparedAtUtc": utc(), "inputMetrics": {}}
    for arm, value in arms.items():
        (output / (arm + ".request.json")).write_bytes(value["raw"])
        report["inputMetrics"][arm] = {"wireBytes": len(value["raw"]), "systemUtf8Bytes": sum(len(m["content"].encode()) for m in value["body"]["messages"] if m["role"] == "system" and isinstance(m["content"], str)), "singleImageSha256": sha(value["images"][0].encode())}
    write_json(old_report, report)
    if not args.execute:
        print(json.dumps({"status": "prepared", "modelRequests": 0, "order": report["order"], "inputMetrics": report["inputMetrics"], "report": str(old_report)}))
        return
    with (output / "execution.once.json").open("x", encoding="utf-8") as reserve:
        json.dump({"reservedAtUtc": utc(), "requestHashes": frozen, "order": report["order"]}, reserve)
    try:
        for key, arm in ORDER:
            directory = output / key; directory.mkdir()
            slots = idle_slots()
            write_json(directory / "attempt.json", {"submittedAtUtc": utc(), "requestSha256": frozen[arm]})
            report["modelRequests"] += 1
            result, deltas = capture(arms[arm]["raw"], directory, args.timeout)
            result.update(id=key, arm=arm, idleSlotsBefore=slots)
            parsed = replay(exe, deltas, directory)
            result["speechParser"] = parsed
            result["clientTimings"]["firstPublicChannelSpeechDeltaSeconds"] = parsed["firstPublicChannelSpeechDeltaSeconds"]
            report["cases"].append(result)
            report["summary"] = summaries(report["cases"])
            write_json(old_report, report)
            print(json.dumps({"id": key, "status": result["httpStatus"], "finishReason": result["finishReason"], "timings": result["clientTimings"]}), flush=True)
            if result["error"] or not result["doneSeen"] or result["finishReason"] != "stop" or result["httpStatus"] != 200:
                raise RuntimeError("Fixed sample did not complete normally: " + key)
        report["status"] = "completed"
    except Exception as error:
        report["status"] = "failed-or-busy-no-retry"
        report["error"] = type(error).__name__ + ": " + str(error)[:1000]
        raise
    finally:
        report["finishedAtUtc"] = utc()
        write_json(old_report, report)


if __name__ == "__main__":
    main()
