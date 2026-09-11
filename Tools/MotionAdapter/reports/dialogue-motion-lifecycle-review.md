# Dialogue motion lifetime review — 2026-09-10

The reviewed defect is independent of the generated pose quality. A committed
body action must survive an unrelated autonomous speech response, while a new
user turn, user speech, explicit `none`, or a new accepted motion still replaces
or stops it.

## Findings from code and the bounded Editor log excerpt

- `OnStreamComplete` handles a trailing `<continue/>` by calling
  `DispatchFormalStream` with the same formal response generation. This path does
  **not** call `StartStreaming` or invalidate the motion merely by continuing.
- Each completed chain response can nevertheless dispatch another motion tag.
  `ArdyLiveMotionController.RequestMotion` cancels the old action before starting
  the new intent. The log contains `Raise both hands up above head` at lines
  14466 and 14747, with immediate continuation at 14540 and 14820, followed by
  `Raise both hands straight up above the head` at 14977. The first repeated tag
  is exactly identical; the later description is different and remains an
  explicit replacement rather than being heuristically merged.
- `<next in="3s"/><continue/>` means immediate continuation: the existing
  scheduling parser deliberately chooses the final trailing scheduling tag.
  It does not wait three seconds and does not wait for a six-second body clip.
- Autonomous timer ticks enter `StartStreaming`, whose previous unconditional
  `CancelDialogueMotion("new-formal-response")` cancelled a committed action
  before knowing whether the new response contained any motion. The collected
  response path had the same unconditional cancellation. Both have been removed.
- Existing logs did not record motion cancellation reasons or playback elapsed
  time. They demonstrate repeated requests and scheduling, but do not establish
  how many seconds of each physical clip actually played. No claim is made that
  this lifecycle issue explains every raised-arm pose defect.

## Implemented boundaries

`BeginFormalResponseGeneration` advances the speech callback fence without
cancelling the body controller. Same-generation, exact `name + description`
commands are idempotent; no semantic similarity matching is performed. Explicit
`none` and real cancellation clear this record. Stale-generation callbacks remain
rejected, and subsequent true user requests can submit the same action again.

`MotionStateContextRequested` lets the bridge provide the controller's actual
current action JSON to the next model request. The prompt distinguishes speech
scheduling from action completion and asks the model not to restart an action
just to continue talking. Unbinding removes this subscription. The controller's
facts and bounded request/response/cancellation trace are implemented separately.

The updated `ArdyDialogueMotionRegression` covers exact chain repetition, changed
descriptions, an autonomous response without a motion, stale callbacks, explicit
stop, a new action after stop, real user speech/text cancellation, current motion
facts, and the actual collected-response callback. On 2026-09-10 it passed all
167 assertions in isolated Unity 2022.3.22f1, using the actual inactive ChatSample
and production parser without stubs. The process exited with code 0; its report
is `dialogue-motion-regression.json`. This verifies lifecycle behavior, while
generated pose quality and visual naturalness remain separate evaluations.
