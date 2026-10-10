"""Frozen cross-object dialogue probes through the actual production review payload.

Synthetic cases are declared before inference; expected labels never enter the
request. No Unity actions, service startup, retries or selection of answers.
Use --execute-model to send each case once to the already-running local model.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def cases() -> list[dict]:
    rows = []

    def proposal(name, text, target, state, verdict, assessment, sources=None, **expected):
        rows.append({"id": name, "context": {
            "mode": "new-proposal", "currentUserText": text,
            "proposal": {"operation": "set", "requestQuote": text,
                         "targetDescription": target, "desiredState": state},
            "existingGoal": {"status": "none"}, "recentDialogue": [],
            "referentSources": sources or [], "verifiedDeviceHistory": [],
            "verifiedObservedDeviceHistory": []},
            "expected": dict(verdict=verdict, requestAssessment=assessment, **expected)})

    proposal("survey-not-tv-command", "还有什么值得注意的东西吗？", "电视机", "off", "rejected", "not-requested")
    proposal("memory-question-not-fan-command", "你还记得风扇放在哪儿吗？", "风扇", "on", "rejected", "not-requested")
    proposal("english-frustration-not-heater-command", "Why do you keep asking about that heater?", "heater", "off", "rejected", "not-requested")
    proposal("japanese-inspect-not-screen-command", "その画面に何が映っているか見て。電源は変えないで。", "画面", "off", "rejected", "not-requested")
    proposal("negative-fan", "别把门口的风扇打开。", "门口的风扇", "on", "rejected", "not-requested")
    proposal("quoted-projector-command", "他说过‘关掉投影仪’，我只是在复述，不是让你照做。", "投影仪", "off", "rejected", "not-requested")
    proposal("hypothetical-screen", "如果把显示器关掉会怎么样？我先问问。", "显示器", "off", "rejected", "not-requested")
    proposal("genuine-fan-on", "请打开靠窗的风扇。", "靠窗的风扇", "on", "approved", "requested", relation="new")
    proposal("genuine-projector-off", "Please switch off the projector beside the door.", "projector beside the door", "off", "approved", "requested", relation="new")
    proposal("genuine-japanese-heater-off", "窓のそばの暖房を切ってください。", "窓のそばの暖房", "off", "approved", "requested", relation="new")
    proposal("wrong-proposed-state", "打开桌上的收音机。", "桌上的收音机", "off", "rejected", "requested")
    proposal("wrong-proposed-object", "关掉电视，不要动风扇。", "风扇", "off", "rejected", "requested")
    histories = [{"kind": "operated-goal", "sourceId": "operated-goal:fixture-projector-" + str(i),
                  "goalId": "fixture-projector-goal-" + str(i), "entityId": "fixture-projector-entity-" + str(i),
                  "targetDescription": location + "的投影仪", "originalRequest": "关掉" + location + "的投影仪。",
                  "desiredState": "off", "verified": True,
                  "scope": "Synthetic verified historical identity only; not current visibility or permission."}
                 for i, location in enumerate(("门边", "窗边"), 1)]
    proposal("ambiguous-two-verified-projectors", "把之前关掉的一台投影仪打开。", "之前关掉的一台投影仪", "on",
             "uncertain", "target-unclear", sources=histories, referentKind="unbound", referentSourceId="none")
    proposal("unseen-clearly-requested-radio", "找到书架上的收音机，把它关掉。", "书架上的收音机", "off",
             "approved", "requested", relation="new", referentKind="unbound", referentSourceId="none")

    goal_id = "goal-" + str(uuid.uuid5(uuid.NAMESPACE_URL, "neva-generalization-projector"))
    entity_id = "scene-object-" + str(uuid.uuid5(uuid.NAMESPACE_URL, "neva-generalization-projector-entity"))
    standing = {"goalId": goal_id, "originalRequest": "请把书架旁的投影仪打开。",
                "requestQuote": "把书架旁的投影仪打开", "latestUserText": "请把书架旁的投影仪打开。",
                "targetDescription": "书架旁的投影仪", "desiredState": "on", "status": "suspended",
                "authorized": True, "boundEntityId": entity_id, "searchSummary": {"searchAttempted": True,
                "completedStepCount": 1}, "boundEntityMemory": {"entityId": entity_id,
                "positionMemoryValid": True, "currentVisibility": "not-established"}}

    def update(name, text, relation, response, same=True):
        rows.append({"id": name, "context": {"mode": "goal-user-update", "currentUserText": text,
            "latestUserText": text, "proposal": None, "existingGoal": copy.deepcopy(standing),
            "referentSources": [], "verifiedDeviceHistory": [], "recentDialogue": []},
            "expected": dict(verdict="approved", relation=relation, responseMode=response,
                             **({"referentGoalId": goal_id} if same else {}))})

    update("direction-question-must-answer", "书架在哪个方向？你为什么往那边转？", ["clarify", "unrelated"], "answer", same=False)
    update("memory-question-must-answer", "你还记得刚才看到它的位置吗？", ["clarify", "unrelated"], "answer", same=False)
    update("english-reason-question", "Why did you turn left? Where is the projector now?", ["clarify", "unrelated"], "answer", same=False)
    update("japanese-progress-question", "もう探しましたか？まだ見つからない理由を教えて。", ["continue", "clarify", "unrelated"], "answer", same=False)
    update("explicit-answer-then-continue", "先告诉我刚才搜了哪里，然后继续完成原来的任务。", ["continue", "clarify"], "answer-then-resume")
    update("english-answer-then-continue", "Explain what stopped you, then carry on with switching on the same projector.", ["continue", "clarify"], "answer-then-resume")
    update("plain-continue", "继续完成刚才的任务。", "continue", "act")
    update("same-target-location", "就是你刚才看到的那台，在你现在右后方。", "clarify", "act")
    update("explicit-cancel-and-question", "先取消这个任务。你刚才为什么往左看？", "cancel", "answer")
    update("independent-turn-precondition", "先向右转九十度。", ["unrelated", "clarify"], "act", same=False)
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixtures = output / "fixtures"
    fixtures.mkdir()
    manifest = {"scope": "Frozen synthetic cross-object Chinese/English/Japanese review cases. Labels are independently specified in this manifest only; no scene execution is claimed.",
                "scriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "cases": []}
    for case in cases():
        name = case["id"] + ".input.json"
        write(fixtures / name, case["context"])
        manifest["cases"].append({"id": case["id"], "input": name, "expected": case["expected"],
                                  "provenance": {"kind": "synthetic-generalization-control"}})
    manifest_path = fixtures / "manifest.json"
    write(manifest_path, manifest)
    command = [sys.executable, "-X", "utf8", str(ROOT / "Tools/MotionAdapter/validate_typed_device_referent_qwen.py"),
               "--manifest", str(manifest_path), "--output", str(output / "review"), "--url", args.url, "--model", args.model]
    if args.execute_model:
        command.append("--execute-model")
    return subprocess.run(command, cwd=ROOT).returncode


if __name__ == "__main__":
    raise SystemExit(main())
