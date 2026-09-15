"""A continuous room stream acknowledges only the audited short output prefix."""
from __future__ import annotations

import math
from typing import Literal

from pydantic import Field, field_validator, model_validator

from .locomotion_protocol import FullBodyFrame, FullBodyHistory, LocomotionIdentity, PathTarget

STREAM_PROTOCOL = "ardy-room-stream-v1"


def check_seam(target: PathTarget, last: FullBodyFrame):
    if math.dist((target.rootPosition.x, target.rootPosition.z), (last.rootPosition.x, last.rootPosition.z)) > .20:
        raise ValueError("Stream targets must start near the acknowledged pelvis")
    q = last.globalRotations[0]
    x, z = 2 * (q.x*q.z + q.w*q.y), 1 - 2 * (q.x*q.x + q.y*q.y)
    if math.hypot(x, z) < .1:
        raise ValueError("Stream history has no horizontal pelvis heading")
    heading = math.degrees(math.atan2(x, z))
    if abs((target.headingDegrees-heading+180) % 360-180) > 9.001:
        raise ValueError("First stream heading must continue the acknowledged pelvis")


class StreamStep(LocomotionIdentity):
    schema: Literal[1] = 1
    sessionId: str = Field(min_length=1, max_length=96, pattern=r"^[A-Za-z0-9_.:-]+$")
    requestId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    stepIndex: int = Field(ge=0, le=1000000, strict=True)
    startFrame: int = Field(ge=0, le=16000000, strict=True)
    controlRevision: int = Field(default=0, ge=0, le=9007199254740991, strict=True)
    commitFrames: Literal[8, 16] = 8
    previousRequestId: str | None = Field(default=None, max_length=128)
    description: str | None = Field(default=None, min_length=1, max_length=240)
    initialHistory: FullBodyHistory | None = None
    targets: list[PathTarget] = Field(min_length=40, max_length=184)
    seed: int = Field(default=0, ge=0, le=2147483647, strict=True)
    timeoutMs: int = Field(default=5000, ge=100, le=20000, strict=True)

    @field_validator("description", "previousRequestId", mode="before")
    @classmethod
    def unity_optional_string(cls, value):
        # JsonUtility can serialize a null string as "". Semantic requirements
        # below still demand a real description on step zero and an ACK later.
        return None if value == "" else value

    @field_validator("commitFrames", mode="before")
    @classmethod
    def exact_prefix(cls, value):
        if type(value) is not int or value not in (8, 16):
            raise ValueError("commitFrames must be integer 8 or 16")
        return value

    @model_validator(mode="after")
    def continuous_targets(self):
        if len(self.targets) % 4:
            raise ValueError("Stream lookahead must align to four-frame model tokens")
        for a, b in zip(self.targets, self.targets[1:]):
            if math.dist((a.rootPosition.x, a.rootPosition.z), (b.rootPosition.x, b.rootPosition.z)) > .075001:
                raise ValueError("Target speed exceeds 1.5 source metres/second")
            if abs((b.headingDegrees-a.headingDegrees+180) % 360-180) > 9.001:
                raise ValueError("Target heading changes too quickly")
        if self.description is not None and not self.description.strip():
            raise ValueError("Description cannot be blank")
        if self.stepIndex == 0:
            if self.startFrame != 0 or self.previousRequestId is not None or self.initialHistory is None or self.description is None:
                raise ValueError("First stream step needs description and history, frame zero and no acknowledgement")
            check_seam(self.targets[0], self.initialHistory.frames[-1])
        elif self.initialHistory is not None or not self.previousRequestId:
            raise ValueError("Continuation needs previousRequestId and cannot replace its acknowledged history")
        return self


class StreamCancel(LocomotionIdentity):
    sessionId: str = Field(min_length=1, max_length=96, pattern=r"^[A-Za-z0-9_.:-]+$")
