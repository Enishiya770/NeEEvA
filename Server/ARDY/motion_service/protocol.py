"""Bounded wire protocol shared with the Unity motion client."""
from __future__ import annotations

import math
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

FEATURE_CONTRACT = "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0"
FEATURE_TEMPLATE = "Motion description: {text}\nRepresentation:"
QWEN_MODEL_SHA256 = "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4"
FPS, NEW_FRAMES, HISTORY_FRAMES, MAX_CHUNKS = 20, 40, 16, 3


class WireModel(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)


class Rotation(WireModel):
    x: float
    y: float
    z: float
    w: float

    @model_validator(mode="after")
    def unit_rotation(self):
        norm = sum(getattr(self, k) ** 2 for k in "xyzw")
        if not math.isfinite(norm) or abs(norm - 1.0) > 0.002:
            raise ValueError("History quaternions must be finite and normalized")
        return self


class MotionFrame(WireModel):
    globalRotations: list[Rotation] = Field(min_length=27, max_length=27)


class InitialHistory(WireModel):
    kind: Literal["unity-upper-body-projection-v1"]
    fps: Literal[20] = 20
    frames: list[MotionFrame] = Field(min_length=1, max_length=16)
    conditioningFrames: Literal[4, 8, 16] = 16

    @field_validator("conditioningFrames", mode="before")
    @classmethod
    def exact_conditioning_length(cls, value):
        # Literal alone accepts numerically equal floats; the wire contract is integer-only.
        if type(value) is not int or value not in (4, 8, 16):
            raise ValueError("conditioningFrames must be the integer 4, 8 or 16")
        return value

    @model_validator(mode="after")
    def stationary_lower_body(self):
        # Core27 root and eight leg joints are synthetic neutral, never live observations.
        for frame in self.frames:
            for index in (0, *range(19, 27)):
                rotation = frame.globalRotations[index]
                if max(abs(rotation.x), abs(rotation.y), abs(rotation.z)) > 0.0001:
                    raise ValueError("Projected history root and legs must be identity rotations")
        return self


class Identity(WireModel):
    characterId: str = Field(min_length=1, max_length=96, pattern=r"^[A-Za-z0-9_.:-]+$")
    turnId: str = Field(min_length=1, max_length=96, pattern=r"^[A-Za-z0-9_.:-]+$")
    revision: int = Field(ge=0, le=9007199254740991, strict=True)


class GenerateRequest(Identity):
    requestId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    chunkIndex: int = Field(ge=0, lt=3, strict=True)
    description: str = Field(min_length=1, max_length=240)
    mask: Literal["UpperBody"] = "UpperBody"
    seed: int = Field(default=0, ge=0, le=2147483647, strict=True)
    timeoutMs: int = Field(default=20000, ge=100, le=20000, strict=True)
    maxChunks: Literal[3] = 3
    initialHistory: InitialHistory | None = None

    @field_validator("description")
    @classmethod
    def nonempty_description(cls, value):
        if not value.strip() or len(value.encode("utf-8")) > 1024:
            raise ValueError("Description must be nonempty and at most 1024 UTF-8 bytes")
        return value

    @model_validator(mode="after")
    def initial_only_on_first_chunk(self):
        if self.chunkIndex and self.initialHistory is not None:
            raise ValueError("initialHistory is only valid for chunkIndex 0")
        return self


class CancelRequest(Identity):
    requestId: str | None = Field(default=None, min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    reason: str | None = Field(default=None, max_length=240)


class ServiceError(Exception):
    def __init__(self, status: int, code: str, message: str):
        self.status, self.code, self.message = status, code, message
        super().__init__(message)

    def payload(self):
        return {"error": {"code": self.code, "message": self.message}}
