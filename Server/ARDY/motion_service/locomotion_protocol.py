"""Room locomotion uses explicit planar targets and measured full-body history."""
from __future__ import annotations

import math
from typing import Literal

from pydantic import Field, field_validator, model_validator
from .protocol import WireModel, Rotation

CORE_NAMES = ["Hips", "Spine", "Spine1", "Spine2", "Spine3", "Neck", "Head",
    "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandEnd", "RightHandThumb1",
    "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandEnd", "LeftHandThumb1",
    "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase"]


class Point(WireModel):
    x: float = Field(ge=-20, le=20)
    y: float = Field(ge=-5, le=5)
    z: float = Field(ge=-20, le=20)


class FullBodyFrame(WireModel):
    rootPosition: Point
    globalRotations: list[Rotation] = Field(min_length=27, max_length=27)


class FullBodyHistory(WireModel):
    kind: Literal["unity-full-body-projection-v1"]
    fps: Literal[20]
    jointNames: list[str]
    frames: list[FullBodyFrame] = Field(min_length=16, max_length=16)

    @model_validator(mode="after")
    def valid_history(self):
        if self.jointNames != CORE_NAMES:
            raise ValueError("History must use the pinned Core27 order")
        for frame in self.frames:
            if not .25 <= frame.rootPosition.y <= 2.5:
                raise ValueError("History pelvis must be above its explicit support plane")
        for a, b in zip(self.frames, self.frames[1:]):
            if math.dist(tuple(a.rootPosition.model_dump().values()), tuple(b.rootPosition.model_dump().values())) > .15:
                raise ValueError("History contains a discontinuous root trajectory")
        return self


class PathTarget(WireModel):
    rootPosition: Point
    headingDegrees: float = Field(ge=-36000, le=36000)

    @model_validator(mode="after")
    def flat(self):
        if abs(self.rootPosition.y) > .0001:
            raise ValueError("This locomotion endpoint supports one flat support plane only")
        return self


class LocomotionIdentity(WireModel):
    characterId: str = Field(min_length=1, max_length=96, pattern=r"^[A-Za-z0-9_.:-]+$")
    revision: int = Field(ge=0, le=9007199254740991, strict=True)


class LocomotionRequest(LocomotionIdentity):
    schema: Literal[1] = 1
    requestId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    description: str = Field(min_length=1, max_length=240)
    seed: int = Field(default=0, ge=0, le=2147483647, strict=True)
    timeoutMs: int = Field(default=20000, ge=100, le=20000, strict=True)
    targets: list[PathTarget] = Field(min_length=40, max_length=200)
    initialHistory: FullBodyHistory

    @field_validator("description")
    @classmethod
    def meaningful(cls, value):
        if not value.strip():
            raise ValueError("Description cannot be blank")
        return value

    @model_validator(mode="after")
    def valid_route(self):
        if len(self.targets) % 40:
            raise ValueError("A route must contain complete 40-frame windows")
        positions = [(t.rootPosition.x, t.rootPosition.z) for t in self.targets]
        for a, b in zip(positions, positions[1:]):
            if math.dist(a, b) > .075001:
                raise ValueError("Target speed exceeds 1.5 source metres/second")
        for a, b in zip(self.targets, self.targets[1:]):
            if abs((b.headingDegrees-a.headingDegrees+180) % 360-180) > 9.001:
                raise ValueError("Target heading changes too quickly")
        last = self.initialHistory.frames[-1].rootPosition
        if math.dist(positions[0], (last.x, last.z)) > .20:
            raise ValueError("Target route must begin near the actually observed pelvis")
        root = self.initialHistory.frames[-1].globalRotations[0]
        forward_x = 2*(root.x*root.z + root.w*root.y)
        forward_z = 1-2*(root.x*root.x + root.y*root.y)
        if math.hypot(forward_x, forward_z) < .1:
            raise ValueError("Observed pelvis must have a defined horizontal heading")
        observed_heading = math.degrees(math.atan2(forward_x, forward_z))
        if abs((self.targets[0].headingDegrees-observed_heading+180) % 360-180) > 9.001:
            raise ValueError("First heading must continue the actually observed pelvis heading")
        if any(math.dist(p, positions[-1]) > .005 for p in positions[-20:]):
            raise ValueError("Reserve at least the last second to hold the destination")
        return self


class LocomotionCancel(LocomotionIdentity):
    requestId: str | None = Field(default=None, max_length=128)
