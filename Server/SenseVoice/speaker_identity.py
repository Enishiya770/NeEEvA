"""Persistent open-set speaker identity store for NeEEvA.

The store deliberately stays separate from the LLM memory graph: biometric
identity must not decay, be reseeded, or be rewritten by model-generated text.
"""

from __future__ import annotations

import json
import os
import threading
import uuid
from datetime import datetime, timezone
from typing import Dict, Iterable, List, Optional, Tuple

import numpy as np


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _unit(values: Iterable[float]) -> Optional[np.ndarray]:
    vec = np.asarray(list(values), dtype=np.float32).reshape(-1)
    if vec.size == 0 or not np.all(np.isfinite(vec)):
        return None
    norm = float(np.linalg.norm(vec))
    if norm < 1e-8:
        return None
    return vec / norm


def _packed(vec: np.ndarray) -> List[float]:
    # Seven decimals is plenty for cosine scoring and keeps the local file small.
    return [round(float(x), 7) for x in vec]


class SpeakerIdentityStore:
    OWNER_ID = "owner"
    AI_ID = "ai_self"

    def __init__(
        self,
        path: str,
        match_threshold: float = 0.55,
        session_threshold: float = 0.48,
        update_threshold: float = 0.64,
        owner_update_threshold: float = 0.70,
        min_identify_ms: int = 700,
        min_enroll_ms: int = 1500,
        promote_utterances: int = 3,
        promote_speech_ms: int = 10000,
        auto_owner_bootstrap: bool = True,
        max_exemplars: int = 8,
    ):
        self.path = os.path.abspath(os.path.expanduser(path))
        self.match_threshold = float(match_threshold)
        self.session_threshold = float(session_threshold)
        self.update_threshold = float(update_threshold)
        self.owner_update_threshold = float(owner_update_threshold)
        self.min_identify_ms = int(min_identify_ms)
        self.min_enroll_ms = int(min_enroll_ms)
        self.promote_utterances = max(1, int(promote_utterances))
        self.promote_speech_ms = max(1, int(promote_speech_ms))
        self.auto_owner_bootstrap = bool(auto_owner_bootstrap)
        self.max_exemplars = max(2, int(max_exemplars))

        self._lock = threading.RLock()
        self._profiles: Dict[str, dict] = {}
        self._session: Dict[str, dict] = {}
        self._guest_counter = 0
        self._load()

    # ------------------------------ persistence ------------------------------

    def _load(self) -> None:
        if not os.path.isfile(self.path):
            return
        try:
            with open(self.path, "r", encoding="utf-8") as fh:
                payload = json.load(fh)
            for profile in payload.get("profiles", []):
                if not isinstance(profile, dict):
                    continue
                speaker_id = str(profile.get("speaker_id", "")).strip()
                centroid = _unit(profile.get("centroid", []))
                if not speaker_id or centroid is None:
                    continue
                profile["centroid"] = _packed(centroid)
                cleaned = []
                for item in profile.get("exemplars", []):
                    vec = _unit(item)
                    if vec is not None:
                        cleaned.append(_packed(vec))
                profile["exemplars"] = cleaned[-self.max_exemplars :] or [_packed(centroid)]
                self._profiles[speaker_id] = profile
            print(f"[Speaker] loaded {len(self._profiles)} persistent profiles: {self.path}")
        except Exception as exc:
            print(f"[Speaker] failed to load profiles, starting empty: {exc}")

    def _save(self) -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        payload = {
            "version": 1,
            "updated_at": _utc_now(),
            "profiles": list(self._profiles.values()),
        }
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False, indent=2)
        os.replace(tmp, self.path)

    # ------------------------------ profile helpers ------------------------------

    def _new_profile(
        self,
        speaker_id: str,
        display_name: str,
        kind: str,
        embedding: np.ndarray,
        speech_ms: int,
        status: str = "candidate",
        locked: bool = False,
    ) -> dict:
        now = _utc_now()
        quality_ok = speech_ms >= self.min_enroll_ms
        return {
            "speaker_id": speaker_id,
            "display_name": display_name,
            "kind": kind,
            "status": status,
            "locked": bool(locked),
            "centroid": _packed(embedding),
            "exemplars": [_packed(embedding)],
            "utterance_count": 1,
            "enroll_utterances": 1 if quality_ok else 0,
            "total_speech_ms": int(speech_ms) if quality_ok else 0,
            "created_at": now,
            "last_seen": now,
        }

    def _all_profiles(self) -> List[Tuple[dict, bool]]:
        result = [(p, True) for p in self._profiles.values()]
        result.extend((p, False) for p in self._session.values())
        return result

    @staticmethod
    def _score(profile: dict, embedding: np.ndarray) -> float:
        centroid = _unit(profile.get("centroid", []))
        if centroid is None or centroid.size != embedding.size:
            return -1.0
        return float(np.dot(centroid, embedding))

    def _append_embedding(self, profile: dict, embedding: np.ndarray, speech_ms: int) -> None:
        exemplars = list(profile.get("exemplars", []))
        exemplars.append(_packed(embedding))
        exemplars = exemplars[-self.max_exemplars :]
        matrix = []
        for item in exemplars:
            vec = _unit(item)
            if vec is not None:
                matrix.append(vec)
        centroid = _unit(np.mean(np.stack(matrix), axis=0)) if matrix else embedding
        profile["exemplars"] = exemplars
        profile["centroid"] = _packed(centroid if centroid is not None else embedding)
        profile["enroll_utterances"] = int(profile.get("enroll_utterances", 0)) + 1
        profile["total_speech_ms"] = int(profile.get("total_speech_ms", 0)) + int(speech_ms)

    def _progress(self, profile: dict) -> float:
        if profile.get("status") == "confirmed":
            return 1.0
        by_count = int(profile.get("enroll_utterances", 0)) / self.promote_utterances
        by_time = int(profile.get("total_speech_ms", 0)) / self.promote_speech_ms
        return float(max(0.0, min(1.0, min(by_count, by_time))))

    def _summary(
        self,
        profile: Optional[dict],
        score: float = 0.0,
        is_new: bool = False,
        persistent: bool = False,
    ) -> dict:
        if profile is None:
            return {
                "speaker_id": "unknown",
                "speaker_name": "无法确认的说话人",
                "speaker_kind": "unknown",
                "speaker_status": "unknown",
                "speaker_confidence": round(float(score), 4),
                "speaker_is_new": False,
                "speaker_persistent": False,
                "speaker_enrollment_progress": 0.0,
            }
        return {
            "speaker_id": profile["speaker_id"],
            "speaker_name": profile.get("display_name", profile["speaker_id"]),
            "speaker_kind": profile.get("kind", "guest"),
            "speaker_status": profile.get("status", "candidate"),
            "speaker_confidence": round(float(score), 4),
            "speaker_is_new": bool(is_new),
            "speaker_persistent": bool(persistent),
            "speaker_enrollment_progress": round(self._progress(profile), 3),
        }

    def _promote_if_ready(self, profile: dict, was_persistent: bool) -> bool:
        if profile.get("status") == "confirmed":
            return was_persistent
        if self._progress(profile) < 1.0:
            return was_persistent

        profile["status"] = "confirmed"
        if profile.get("kind") == "owner":
            profile["locked"] = True
            profile["display_name"] = "主人"
        speaker_id = profile["speaker_id"]
        if not was_persistent:
            self._session.pop(speaker_id, None)
            self._profiles[speaker_id] = profile
        self._save()
        print(
            f"[Speaker] promoted {speaker_id} ({profile.get('display_name')}) "
            f"utterances={profile.get('enroll_utterances')} speech={profile.get('total_speech_ms')}ms"
        )
        return True

    # ------------------------------ identification ------------------------------

    def identify_only(self, values: Iterable[float], speech_ms: int) -> dict:
        """Match current profiles without creating or updating identities."""
        embedding = _unit(values)
        if embedding is None or speech_ms < self.min_identify_ms:
            result = self._summary(None)
            result["speaker_self_confidence"] = 0.0
            return result

        with self._lock:
            best_profile = None
            best_persistent = False
            best_score = -1.0
            self_score = -1.0
            for profile, persistent in self._all_profiles():
                score = self._score(profile, embedding)
                if profile.get("speaker_id") == "ai_self":
                    self_score = score
                if score > best_score:
                    best_profile = profile
                    best_persistent = persistent
                    best_score = score
            if best_profile is None:
                result = self._summary(None, best_score)
                result["speaker_self_confidence"] = round(max(0.0, self_score), 4)
                return result

            threshold = self.match_threshold if best_persistent else self.session_threshold
            if best_profile.get("status") != "confirmed":
                threshold = self.session_threshold
            elif best_profile.get("kind") == "ai":
                threshold = max(threshold, 0.55)
            if best_score < threshold:
                result = self._summary(None, best_score)
            else:
                result = self._summary(best_profile, best_score, False, best_persistent)
            # Always expose the direct AI_SELF similarity.  The best open-set
            # match can be a human profile even when the signal also contains
            # strong playback echo, so barge-in needs this independent veto.
            result["speaker_self_confidence"] = round(max(0.0, self_score), 4)
            return result

    def identify_and_learn(self, values: Iterable[float], speech_ms: int) -> dict:
        embedding = _unit(values)
        if embedding is None or speech_ms < self.min_identify_ms:
            return self._summary(None)

        with self._lock:
            best_profile = None
            best_persistent = False
            best_score = -1.0
            ai_profile = None
            ai_score = -1.0
            for profile, persistent in self._all_profiles():
                score = self._score(profile, embedding)
                if profile.get("speaker_id") == self.AI_ID:
                    ai_profile = profile
                    ai_score = score
                if score > best_score:
                    best_profile = profile
                    best_persistent = persistent
                    best_score = score

            # The microphone can hear the character through speakers.  A guest
            # profile contaminated by that echo can score marginally higher than
            # AI_SELF, so looking only at the global best match would let the
            # character register and later interrupt itself.  Treat a strong,
            # independent AI_SELF match as an echo veto before any open-set
            # learning or guest-profile update occurs.
            ai_veto_threshold = max(self.match_threshold, 0.55)
            if ai_profile is not None and ai_score >= ai_veto_threshold:
                return self._summary(ai_profile, ai_score, False, True)

            if best_profile is not None:
                threshold = self.match_threshold if best_persistent else self.session_threshold
                # Owner candidates are persisted early but still use the session threshold.
                if best_profile.get("status") != "confirmed":
                    threshold = self.session_threshold
                elif best_profile.get("kind") == "ai":
                    # AI_SELF is used as an echo-rejection anchor; be conservative.
                    threshold = max(threshold, 0.62)
                if best_score >= threshold:
                    best_profile["utterance_count"] = int(best_profile.get("utterance_count", 0)) + 1
                    best_profile["last_seen"] = _utc_now()

                    kind = best_profile.get("kind")
                    update_at = self.owner_update_threshold if kind == "owner" and best_profile.get("status") == "confirmed" else self.update_threshold
                    if best_profile.get("status") != "confirmed":
                        update_at = max(self.session_threshold, 0.52)
                    quality_ok = speech_ms >= self.min_enroll_ms
                    # Never adapt AI_SELF from microphone echo.
                    if quality_ok and kind != "ai" and best_score >= update_at:
                        self._append_embedding(best_profile, embedding, speech_ms)

                    now_persistent = self._promote_if_ready(best_profile, best_persistent)
                    if best_persistent and best_profile.get("status") == "confirmed":
                        self._save()
                    return self._summary(best_profile, best_score, False, now_persistent)

            # Open-set branch: no existing identity was close enough.
            owner_exists = self.OWNER_ID in self._profiles or self.OWNER_ID in self._session
            if self.auto_owner_bootstrap and not owner_exists:
                profile = self._new_profile(
                    self.OWNER_ID,
                    "主人（注册中）",
                    "owner",
                    embedding,
                    speech_ms,
                    status="candidate",
                    locked=False,
                )
                # Persist the owner candidate so enrollment can continue after a restart.
                self._profiles[self.OWNER_ID] = profile
                self._save()
                return self._summary(profile, 1.0, True, True)

            self._guest_counter += 1
            speaker_id = f"guest_{uuid.uuid4().hex[:10]}"
            profile = self._new_profile(
                speaker_id,
                f"陌生访客{self._guest_counter}",
                "guest",
                embedding,
                speech_ms,
            )
            self._session[speaker_id] = profile
            return self._summary(profile, 1.0, True, False)

    # ------------------------------ explicit management ------------------------------

    def enroll_fixed(
        self,
        speaker_id: str,
        display_name: str,
        kind: str,
        values: Iterable[float],
        speech_ms: int,
        replace: bool = False,
    ) -> dict:
        embedding = _unit(values)
        if embedding is None:
            raise ValueError("invalid speaker embedding")
        speaker_id = speaker_id.strip()
        if not speaker_id:
            raise ValueError("speaker_id is required")
        kind = kind.strip().lower() or "guest"
        with self._lock:
            profile = None if replace else self._profiles.get(speaker_id)
            if profile is None:
                profile = self._new_profile(
                    speaker_id,
                    display_name.strip() or speaker_id,
                    kind,
                    embedding,
                    speech_ms,
                    status="confirmed",
                    locked=kind in ("owner", "ai"),
                )
                profile["enroll_utterances"] = max(1, int(profile.get("enroll_utterances", 0)))
                profile["total_speech_ms"] = max(int(speech_ms), self.promote_speech_ms)
                self._profiles[speaker_id] = profile
            else:
                self._append_embedding(profile, embedding, speech_ms)
                profile["display_name"] = display_name.strip() or profile.get("display_name", speaker_id)
                profile["status"] = "confirmed"
                profile["locked"] = profile.get("locked", False) or kind in ("owner", "ai")
                profile["last_seen"] = _utc_now()
            self._session.pop(speaker_id, None)
            self._save()
            return self._summary(profile, 1.0, False, True)

    def has_profile(self, speaker_id: str) -> bool:
        with self._lock:
            return speaker_id in self._profiles or speaker_id in self._session

    def list_profiles(self, include_session: bool = True) -> List[dict]:
        with self._lock:
            result = []
            for profile, persistent in self._all_profiles():
                if not include_session and not persistent:
                    continue
                item = self._summary(profile, 1.0, False, persistent)
                item.update(
                    {
                        "utterance_count": int(profile.get("utterance_count", 0)),
                        "enroll_utterances": int(profile.get("enroll_utterances", 0)),
                        "total_speech_ms": int(profile.get("total_speech_ms", 0)),
                        "locked": bool(profile.get("locked", False)),
                        "created_at": profile.get("created_at", ""),
                        "last_seen": profile.get("last_seen", ""),
                    }
                )
                result.append(item)
            return result

    def rename(self, speaker_id: str, display_name: str) -> dict:
        name = display_name.strip()
        if not name:
            raise ValueError("display_name is required")
        with self._lock:
            profile = self._profiles.get(speaker_id) or self._session.get(speaker_id)
            if profile is None:
                raise KeyError(speaker_id)
            profile["display_name"] = name[:64]
            if speaker_id in self._profiles:
                self._save()
            return self._summary(profile, 1.0, False, speaker_id in self._profiles)

    def merge(self, source_id: str, target_id: str) -> dict:
        if source_id == target_id:
            raise ValueError("source and target must differ")
        with self._lock:
            source = self._profiles.get(source_id) or self._session.get(source_id)
            target = self._profiles.get(target_id) or self._session.get(target_id)
            if source is None or target is None:
                raise KeyError("source or target speaker not found")
            if source.get("kind") in ("owner", "ai"):
                raise ValueError("locked identities cannot be merged away")

            combined = list(target.get("exemplars", [])) + list(source.get("exemplars", []))
            target["exemplars"] = combined[-self.max_exemplars :]
            matrix = [_unit(x) for x in target["exemplars"]]
            matrix = [x for x in matrix if x is not None]
            centroid = _unit(np.mean(np.stack(matrix), axis=0))
            if centroid is not None:
                target["centroid"] = _packed(centroid)
            target["utterance_count"] = int(target.get("utterance_count", 0)) + int(source.get("utterance_count", 0))
            target["enroll_utterances"] = int(target.get("enroll_utterances", 0)) + int(source.get("enroll_utterances", 0))
            target["total_speech_ms"] = int(target.get("total_speech_ms", 0)) + int(source.get("total_speech_ms", 0))
            target["last_seen"] = _utc_now()

            self._profiles.pop(source_id, None)
            self._session.pop(source_id, None)
            if target_id in self._profiles:
                self._save()
            return self._summary(target, 1.0, False, target_id in self._profiles)

    def delete(self, speaker_id: str, allow_locked: bool = False) -> None:
        with self._lock:
            profile = self._profiles.get(speaker_id) or self._session.get(speaker_id)
            if profile is None:
                raise KeyError(speaker_id)
            if profile.get("locked") and not allow_locked:
                raise ValueError("locked identity cannot be deleted")
            self._profiles.pop(speaker_id, None)
            self._session.pop(speaker_id, None)
            self._save()

    def reset_owner(self) -> None:
        with self._lock:
            self._profiles.pop(self.OWNER_ID, None)
            self._session.pop(self.OWNER_ID, None)
            self._save()
