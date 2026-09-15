"""Keep the original 14 room probes and add fixed stop/follow semantics; no retries."""
import copy
import validate_room_task_closure as probe


ACTIVE = {**probe.CURRENT, "bodyReserved": True, "followActive": True,
          "taskMode": "follow", "activeTaskId": "public-follow:1",
          "activeActionId": "public-follow:1", "attemptStatus": "active"}
WAITING = {**ACTIVE, "bodyReserved": False, "activeActionId": "",
           "actualUserDistance": 1.2, "attemptStatus": "follow-waiting"}


def decision(identifier, text, intent, facts):
    return {"id": identifier, "kind": "decision",
            "input": probe.decision_input(text, current=facts),
            "expected": {"intent": intent, "origin": "none" if intent == "none" else "user"}}


probe.CASES.extend([
    decision("short-stop", "停一下。", "stop-moving", ACTIVE),
    decision("negated-stop-keep-following", "不要停，继续跟着我。", "none", ACTIVE),
    decision("quoted-stop-not-command", "请只解释词语“停一下”的意思，继续跟着我就好。", "none", ACTIVE),
    decision("start-continuous-follow", "跟着我走，之后也一直跟着我。", "follow", probe.CURRENT),
    decision("one-time-approach", "请走到我身边。", "approach", probe.CURRENT),
    decision("stop-waiting-follow", "不用再跟着我了，在这里等我。", "stop-moving", WAITING),
    decision("ordinary-talk-keeps-follow", "今天玩得很开心。", "none", WAITING),
])

# Continuing an already active task may be expressed as none or idempotent follow.
# Both preserve the same task in production; a stop is never acceptable here.
for case in probe.CASES:
    if case["id"] == "negated-stop-keep-following":
        case["expected"] = {}
        case["allowedIntents"] = ["none", "follow"]
        case["scope"] = "Keep the existing following task; formal dispatch treats repeated follow as follow-already-active."

wrong_wait = probe.review_input("我在这里等你。", phase="moving")
probe.CASES.append({"id": "approach-cannot-claim-waiting", "kind": "review",
                    "input": wrong_wait, "expected": {"verdict": "inconsistent", "allows": False}})
for identifier, speech, coherent in [
    ("follow-waiting-not-stopped", "我已经停止跟随了。", False),
    ("follow-waiting-honest", "我先在你附近等着，你移动时我会继续跟着。", True),
]:
    inp = probe.review_input(speech, phase="follow-waiting", current=WAITING)
    inp.update(intendedAction="follow", dispatch="follow-submitted", actionId="public-follow:1",
               requestedUserText="一直跟着我。")
    inp["currentRoomFacts"] = {"currentFacts": copy.deepcopy(WAITING),
        "execution": {"phase": "follow-waiting", "actionId": "public-follow:1"},
        "lastActionResults": {"phase": "follow-waiting", "actionId": "public-follow:1"}}
    probe.CASES.append({"id": identifier, "kind": "review", "input": inp,
                        "expected": {"verdict": "consistent" if coherent else "inconsistent", "allows": coherent}})

probe.SOURCES.extend([
    "Tools/MotionAdapter/validate_room_stop_follow.py",
    "Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomPreparation.cs",
    "Assets/AIChatTookit/Scripts/Chat/ClosedSpeechEvidence.cs",
])

if __name__ == "__main__":
    probe.main()
