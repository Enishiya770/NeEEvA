"""Hierarchical open-set speaker identity store for NeEEvA.

Version 2 separates a person (identity) from the person's acoustic variants
(voiceprints). A display name and owner/guest/ai kind belong to the identity;
each normal, soft, singing, or device-specific voice can keep an independent
centroid and candidate/confirmed lifecycle beneath that identity.

The store deliberately stays separate from the LLM memory graph: biometric
identity must not decay, be reseeded, or be rewritten by model-generated text.
"""

from __future__ import annotations

import copy
import json
import os
import shutil
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


def _earliest(*values: str) -> str:
    clean = [str(value) for value in values if value]
    return min(clean) if clean else _utc_now()


def _latest(*values: str) -> str:
    clean = [str(value) for value in values if value]
    return max(clean) if clean else _utc_now()


class SpeakerIdentityStore:
    """Thread-safe identity folder and voiceprint repository."""

    VERSION = 2
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
        auto_organize: bool = True,
        auto_merge_threshold: float = 0.65,
        auto_min_enroll_utterances: int = 3,
        auto_min_speech_ms: int = 10000,
        max_operation_history: int = 100,
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
        self.auto_organize_enabled = bool(auto_organize)
        self.auto_merge_threshold = max(
            float(match_threshold), float(auto_merge_threshold)
        )
        self.auto_min_enroll_utterances = max(1, int(auto_min_enroll_utterances))
        self.auto_min_speech_ms = max(1, int(auto_min_speech_ms))
        self.max_operation_history = max(10, int(max_operation_history))

        self._lock = threading.RLock()
        self._identities: Dict[str, dict] = {}
        self._session_identities: Dict[str, dict] = {}
        self._unassigned: Dict[str, dict] = {}
        self._session_unassigned: Dict[str, dict] = {}
        self._operations: List[dict] = []
        self._blocked_auto_pairs = set()
        self._guest_counter = 0
        self._load()
        if self.auto_organize_enabled:
            self.auto_organize()

    # ------------------------------ persistence ------------------------------

    def _load(self) -> None:
        if not os.path.isfile(self.path):
            return
        try:
            with open(self.path, "r", encoding="utf-8") as fh:
                payload = json.load(fh)

            if int(payload.get("version", 1)) < self.VERSION:
                self._migrate_v1(payload)
                return

            for raw in payload.get("identities", []):
                identity = self._normalize_identity(raw)
                if identity is not None:
                    self._identities[identity["identity_id"]] = identity
            for raw in payload.get("unassigned_voiceprints", []):
                voiceprint = self._normalize_voiceprint(raw)
                if voiceprint is not None:
                    voiceprint["provisional_name"] = str(
                        raw.get("provisional_name", voiceprint["voiceprint_id"])
                    )[:64]
                    self._unassigned[voiceprint["voiceprint_id"]] = voiceprint
            self._operations = [
                copy.deepcopy(item)
                for item in payload.get("operations", [])
                if isinstance(item, dict) and item.get("operation_id")
            ][-self.max_operation_history :]
            for pair in payload.get("blocked_auto_pairs", []):
                if not isinstance(pair, list) or len(pair) != 2:
                    continue
                left, right = str(pair[0]).strip(), str(pair[1]).strip()
                if left and right and left != right:
                    self._blocked_auto_pairs.add(tuple(sorted((left, right))))
            print(
                f"[Speaker] loaded {len(self._identities)} identities / "
                f"{self._persistent_voiceprint_count()} voiceprints: {self.path}"
            )
        except Exception as exc:
            print(f"[Speaker] failed to load profiles, starting empty: {exc}")

    def _migrate_v1(self, payload: dict) -> None:
        """Convert every legacy profile into one identity with one voiceprint."""
        migrated = 0
        for raw in payload.get("profiles", []):
            if not isinstance(raw, dict):
                continue
            speaker_id = str(raw.get("speaker_id", "")).strip()
            voiceprint = self._normalize_voiceprint(raw, fallback_id=speaker_id)
            if not speaker_id or voiceprint is None:
                continue
            identity = {
                "identity_id": speaker_id,
                "display_name": str(raw.get("display_name", speaker_id))[:64],
                "kind": str(raw.get("kind", "guest")).strip().lower() or "guest",
                "locked": bool(raw.get("locked", False)),
                "created_at": raw.get("created_at", _utc_now()),
                "last_seen": raw.get("last_seen", _utc_now()),
                "aliases": [],
                "merged_from": [],
                "voiceprints": [voiceprint],
            }
            self._identities[speaker_id] = identity
            migrated += 1

        backup = self._backup_file("v1-before-migration")
        self._save()
        print(
            f"[Speaker] migrated legacy repository to v{self.VERSION}: "
            f"{migrated} identities; backup={backup or 'none'}"
        )

    def _normalize_identity(self, raw: object) -> Optional[dict]:
        if not isinstance(raw, dict):
            return None
        identity_id = str(raw.get("identity_id", "")).strip()
        if not identity_id:
            return None
        voiceprints = []
        for item in raw.get("voiceprints", []):
            voiceprint = self._normalize_voiceprint(item)
            if voiceprint is not None:
                voiceprints.append(voiceprint)
        if not voiceprints:
            return None
        aliases = []
        for alias in raw.get("aliases", []):
            value = str(alias).strip()
            if value and value != identity_id and value not in aliases:
                aliases.append(value)
        merged_from = raw.get("merged_from", [])
        if not isinstance(merged_from, list):
            merged_from = []
        return {
            "identity_id": identity_id,
            "display_name": str(raw.get("display_name", identity_id))[:64],
            "kind": str(raw.get("kind", "guest")).strip().lower() or "guest",
            "locked": bool(raw.get("locked", False)),
            "created_at": raw.get("created_at", _utc_now()),
            "last_seen": raw.get("last_seen", _utc_now()),
            "aliases": aliases,
            "merged_from": merged_from,
            "voiceprints": voiceprints,
        }

    def _normalize_voiceprint(
        self, raw: object, fallback_id: str = ""
    ) -> Optional[dict]:
        if not isinstance(raw, dict):
            return None
        voiceprint_id = str(
            raw.get("voiceprint_id", raw.get("speaker_id", fallback_id))
        ).strip()
        centroid = _unit(raw.get("centroid", []))
        if not voiceprint_id or centroid is None:
            return None
        cleaned = []
        for item in raw.get("exemplars", []):
            vec = _unit(item)
            if vec is not None and vec.size == centroid.size:
                cleaned.append(_packed(vec))
        return {
            "voiceprint_id": voiceprint_id,
            "status": str(raw.get("status", "candidate")),
            "centroid": _packed(centroid),
            "exemplars": cleaned[-self.max_exemplars :] or [_packed(centroid)],
            "utterance_count": int(raw.get("utterance_count", 0)),
            "enroll_utterances": int(raw.get("enroll_utterances", 0)),
            "total_speech_ms": int(raw.get("total_speech_ms", 0)),
            "created_at": raw.get("created_at", _utc_now()),
            "last_seen": raw.get("last_seen", _utc_now()),
        }

    def _persistent_voiceprint_count(self) -> int:
        return sum(len(item.get("voiceprints", [])) for item in self._identities.values()) + len(
            self._unassigned
        )

    def _payload(self) -> dict:
        return {
            "version": self.VERSION,
            "updated_at": _utc_now(),
            "identities": list(self._identities.values()),
            "unassigned_voiceprints": list(self._unassigned.values()),
            "operations": self._operations[-self.max_operation_history :],
            "blocked_auto_pairs": [list(pair) for pair in sorted(self._blocked_auto_pairs)],
        }

    def _save(self, backup_reason: str = "") -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        if backup_reason:
            self._backup_file(backup_reason)
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump(self._payload(), fh, ensure_ascii=False, indent=2)
        os.replace(tmp, self.path)

    def _backup_file(self, reason: str) -> str:
        if not os.path.isfile(self.path):
            return ""
        safe_reason = "".join(
            char if char.isalnum() or char in "-_" else "-" for char in reason
        ).strip("-") or "backup"
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        directory = os.path.join(os.path.dirname(self.path), "speaker_profile_backups")
        os.makedirs(directory, exist_ok=True)
        base = os.path.splitext(os.path.basename(self.path))[0]
        destination = os.path.join(directory, f"{base}_{stamp}_{safe_reason}.json")
        shutil.copy2(self.path, destination)
        return destination

    def _snapshot(self) -> tuple:
        return (
            copy.deepcopy(self._identities),
            copy.deepcopy(self._session_identities),
            copy.deepcopy(self._unassigned),
            copy.deepcopy(self._session_unassigned),
            copy.deepcopy(self._operations),
            copy.deepcopy(self._blocked_auto_pairs),
        )

    def _restore(self, snapshot: tuple) -> None:
        (
            self._identities,
            self._session_identities,
            self._unassigned,
            self._session_unassigned,
            self._operations,
            self._blocked_auto_pairs,
        ) = snapshot

    # ------------------------------ hierarchy helpers ------------------------------

    def _new_voiceprint(
        self,
        embedding: np.ndarray,
        speech_ms: int,
        status: str = "candidate",
        voiceprint_id: str = "",
    ) -> dict:
        now = _utc_now()
        quality_ok = speech_ms >= self.min_enroll_ms
        return {
            "voiceprint_id": voiceprint_id or f"voice_{uuid.uuid4().hex[:12]}",
            "status": status,
            "centroid": _packed(embedding),
            "exemplars": [_packed(embedding)],
            "utterance_count": 1,
            "enroll_utterances": 1 if quality_ok else 0,
            "total_speech_ms": int(speech_ms) if quality_ok else 0,
            "created_at": now,
            "last_seen": now,
        }

    def _new_identity(
        self,
        identity_id: str,
        display_name: str,
        kind: str,
        voiceprint: dict,
        locked: bool = False,
    ) -> dict:
        now = _utc_now()
        return {
            "identity_id": identity_id,
            "display_name": (display_name.strip() or identity_id)[:64],
            "kind": kind.strip().lower() or "guest",
            "locked": bool(locked),
            "created_at": voiceprint.get("created_at", now),
            "last_seen": voiceprint.get("last_seen", now),
            "aliases": [],
            "merged_from": [],
            "voiceprints": [voiceprint],
        }

    def _all_identities(self) -> List[Tuple[dict, bool]]:
        result = [(identity, True) for identity in self._identities.values()]
        result.extend((identity, False) for identity in self._session_identities.values())
        return result

    def _all_unassigned(self) -> List[Tuple[dict, bool]]:
        result = [(voiceprint, True) for voiceprint in self._unassigned.values()]
        result.extend((voiceprint, False) for voiceprint in self._session_unassigned.values())
        return result

    def _find_identity(self, identity_id: str, include_aliases: bool = False) -> Tuple[Optional[dict], bool]:
        identity = self._identities.get(identity_id)
        if identity is not None:
            return identity, True
        identity = self._session_identities.get(identity_id)
        if identity is not None:
            return identity, False
        if include_aliases:
            for candidate, persistent in self._all_identities():
                if identity_id in candidate.get("aliases", []):
                    return candidate, persistent
        return None, False

    def _find_voiceprint(
        self, voiceprint_id: str
    ) -> Tuple[Optional[dict], Optional[dict], bool, bool]:
        for identity, persistent in self._all_identities():
            for voiceprint in identity.get("voiceprints", []):
                if voiceprint.get("voiceprint_id") == voiceprint_id:
                    return voiceprint, identity, persistent, True
        voiceprint = self._unassigned.get(voiceprint_id)
        if voiceprint is not None:
            return voiceprint, None, True, False
        voiceprint = self._session_unassigned.get(voiceprint_id)
        if voiceprint is not None:
            return voiceprint, None, False, False
        return None, None, False, False

    @staticmethod
    def _score(voiceprint: dict, embedding: np.ndarray) -> float:
        centroid = _unit(voiceprint.get("centroid", []))
        if centroid is None or centroid.size != embedding.size:
            return -1.0
        return float(np.dot(centroid, embedding))

    def _append_embedding(self, voiceprint: dict, embedding: np.ndarray, speech_ms: int) -> None:
        exemplars = list(voiceprint.get("exemplars", []))
        exemplars.append(_packed(embedding))
        exemplars = exemplars[-self.max_exemplars :]
        matrix = []
        for item in exemplars:
            vec = _unit(item)
            if vec is not None:
                matrix.append(vec)
        centroid = _unit(np.mean(np.stack(matrix), axis=0)) if matrix else embedding
        voiceprint["exemplars"] = exemplars
        voiceprint["centroid"] = _packed(centroid if centroid is not None else embedding)
        voiceprint["enroll_utterances"] = int(voiceprint.get("enroll_utterances", 0)) + 1
        voiceprint["total_speech_ms"] = int(voiceprint.get("total_speech_ms", 0)) + int(speech_ms)

    def _progress(self, voiceprint: dict) -> float:
        if voiceprint.get("status") == "confirmed":
            return 1.0
        by_count = int(voiceprint.get("enroll_utterances", 0)) / self.promote_utterances
        by_time = int(voiceprint.get("total_speech_ms", 0)) / self.promote_speech_ms
        return float(max(0.0, min(1.0, min(by_count, by_time))))

    @staticmethod
    def _identity_status(identity: dict) -> str:
        voiceprints = identity.get("voiceprints", [])
        return "confirmed" if any(vp.get("status") == "confirmed" for vp in voiceprints) else "candidate"

    @staticmethod
    def _aggregate(identity: dict, field: str) -> int:
        return sum(int(vp.get(field, 0)) for vp in identity.get("voiceprints", []))

    def _voiceprint_summary(
        self,
        voiceprint: dict,
        identity: Optional[dict],
        persistent: bool,
        assigned: bool,
    ) -> dict:
        return {
            "voiceprint_id": voiceprint["voiceprint_id"],
            "status": voiceprint.get("status", "candidate"),
            "persistent": bool(persistent),
            "assigned": bool(assigned),
            "identity_id": identity.get("identity_id", "") if identity else "",
            "identity_name": identity.get("display_name", "") if identity else "",
            "identity_kind": identity.get("kind", "guest") if identity else "guest",
            "provisional_name": voiceprint.get("provisional_name", ""),
            "enrollment_progress": round(self._progress(voiceprint), 3),
            "utterance_count": int(voiceprint.get("utterance_count", 0)),
            "enroll_utterances": int(voiceprint.get("enroll_utterances", 0)),
            "total_speech_ms": int(voiceprint.get("total_speech_ms", 0)),
            "created_at": voiceprint.get("created_at", ""),
            "last_seen": voiceprint.get("last_seen", ""),
        }

    def _identity_detail(self, identity: dict, persistent: bool) -> dict:
        voiceprints = identity.get("voiceprints", [])
        return {
            "identity_id": identity["identity_id"],
            "display_name": identity.get("display_name", identity["identity_id"]),
            "kind": identity.get("kind", "guest"),
            "locked": bool(identity.get("locked", False)),
            "persistent": bool(persistent),
            "status": self._identity_status(identity),
            "voiceprint_count": len(voiceprints),
            "confirmed_voiceprints": sum(vp.get("status") == "confirmed" for vp in voiceprints),
            "candidate_voiceprints": sum(vp.get("status") != "confirmed" for vp in voiceprints),
            "utterance_count": self._aggregate(identity, "utterance_count"),
            "enroll_utterances": self._aggregate(identity, "enroll_utterances"),
            "total_speech_ms": self._aggregate(identity, "total_speech_ms"),
            "created_at": identity.get("created_at", ""),
            "last_seen": identity.get("last_seen", ""),
            "aliases": list(identity.get("aliases", [])),
            "merged_from": list(identity.get("merged_from", [])),
            "voiceprints": [
                self._voiceprint_summary(vp, identity, persistent, True) for vp in voiceprints
            ],
        }

    @staticmethod
    def _is_placeholder_name(name: str, identity_id: str = "") -> bool:
        value = str(name or "").strip()
        return (
            not value
            or value == identity_id
            or value in ("主人", "主人（注册中）")
            or value.startswith("陌生访客")
        )

    def _identity_similarity(self, left: dict, right: dict) -> float:
        scores = []
        for left_voiceprint in left.get("voiceprints", []):
            left_centroid = _unit(left_voiceprint.get("centroid", []))
            if left_centroid is None:
                continue
            for right_voiceprint in right.get("voiceprints", []):
                scores.append(self._score(right_voiceprint, left_centroid))
        return max(scores) if scores else -1.0

    def _eligible_for_auto_organize(self, identity: dict) -> bool:
        if identity.get("kind") == "ai":
            return False
        return any(
            voiceprint.get("status") == "confirmed"
            and int(voiceprint.get("enroll_utterances", 0))
            >= self.auto_min_enroll_utterances
            and int(voiceprint.get("total_speech_ms", 0)) >= self.auto_min_speech_ms
            for voiceprint in identity.get("voiceprints", [])
        )

    def _auto_target_priority(self, identity: dict) -> tuple:
        return (
            identity.get("kind") == "owner",
            bool(identity.get("locked", False)),
            self._aggregate(identity, "utterance_count"),
            self._aggregate(identity, "total_speech_ms"),
            str(identity.get("created_at", "")),
        )

    def _auto_result_name(self, target: dict, source: dict) -> str:
        target_name = str(target.get("display_name", target["identity_id"]))
        source_name = str(source.get("display_name", source["identity_id"]))
        if self._is_placeholder_name(target_name, target["identity_id"]) and not self._is_placeholder_name(
            source_name, source["identity_id"]
        ):
            return source_name
        return target_name

    @staticmethod
    def _public_operation(operation: dict) -> dict:
        return {
            "operation_id": operation.get("operation_id", ""),
            "type": operation.get("type", ""),
            "mode": operation.get("mode", "manual"),
            "created_at": operation.get("created_at", ""),
            "undone_at": operation.get("undone_at", ""),
            "source_id": operation.get("source_id", ""),
            "source_name": operation.get("source_name", ""),
            "target_id": operation.get("target_id", ""),
            "target_name": operation.get("target_name_after", ""),
            "best_similarity": operation.get("best_similarity", -1.0),
            "moved_voiceprint_ids": list(operation.get("moved_voiceprint_ids", [])),
        }

    def _append_operation(self, operation: dict) -> None:
        self._operations.append(operation)
        self._operations = self._operations[-self.max_operation_history :]

    def _summary(
        self,
        identity: Optional[dict],
        voiceprint: Optional[dict],
        score: float = 0.0,
        is_new: bool = False,
        persistent: bool = False,
    ) -> dict:
        if voiceprint is None:
            return {
                "speaker_id": "unknown",
                "speaker_identity_id": "",
                "speaker_voiceprint_id": "",
                "speaker_voiceprint_status": "unknown",
                "speaker_name": "无法确认的说话人",
                "speaker_kind": "unknown",
                "speaker_status": "unknown",
                "speaker_confidence": round(float(score), 4),
                "speaker_is_new": False,
                "speaker_persistent": False,
                "speaker_enrollment_progress": 0.0,
            }

        if identity is None:
            voiceprint_id = voiceprint["voiceprint_id"]
            name = voiceprint.get("provisional_name", voiceprint_id)
            return {
                "speaker_id": voiceprint_id,
                "speaker_identity_id": "",
                "speaker_voiceprint_id": voiceprint_id,
                "speaker_voiceprint_status": voiceprint.get("status", "candidate"),
                "speaker_name": name,
                "speaker_kind": "guest",
                "speaker_status": voiceprint.get("status", "candidate"),
                "speaker_confidence": round(float(score), 4),
                "speaker_is_new": bool(is_new),
                "speaker_persistent": bool(persistent),
                "speaker_enrollment_progress": round(self._progress(voiceprint), 3),
            }

        identity_id = identity["identity_id"]
        return {
            "speaker_id": identity_id,
            "speaker_identity_id": identity_id,
            "speaker_voiceprint_id": voiceprint["voiceprint_id"],
            "speaker_voiceprint_status": voiceprint.get("status", "candidate"),
            "speaker_name": identity.get("display_name", identity_id),
            "speaker_kind": identity.get("kind", "guest"),
            "speaker_status": voiceprint.get("status", "candidate"),
            "speaker_confidence": round(float(score), 4),
            "speaker_is_new": bool(is_new),
            "speaker_persistent": bool(persistent),
            "speaker_enrollment_progress": round(self._progress(voiceprint), 3),
        }

    def _all_matches(self, embedding: np.ndarray) -> List[dict]:
        matches = []
        for identity, persistent in self._all_identities():
            for voiceprint in identity.get("voiceprints", []):
                matches.append(
                    {
                        "identity": identity,
                        "voiceprint": voiceprint,
                        "persistent": persistent,
                        "assigned": True,
                        "score": self._score(voiceprint, embedding),
                    }
                )
        for voiceprint, persistent in self._all_unassigned():
            matches.append(
                {
                    "identity": None,
                    "voiceprint": voiceprint,
                    "persistent": persistent,
                    "assigned": False,
                    "score": self._score(voiceprint, embedding),
                }
            )
        return matches

    def _best_match(self, embedding: np.ndarray) -> Tuple[Optional[dict], float, Optional[dict]]:
        matches = self._all_matches(embedding)
        best = max(matches, key=lambda item: item["score"], default=None)
        ai = None
        for item in matches:
            identity = item["identity"]
            if identity is not None and identity.get("identity_id") == self.AI_ID:
                if ai is None or item["score"] > ai["score"]:
                    ai = item
        return best, (ai["score"] if ai is not None else -1.0), ai

    def _match_threshold(self, match: dict) -> float:
        voiceprint = match["voiceprint"]
        if not match["persistent"] or voiceprint.get("status") != "confirmed":
            return self.session_threshold
        identity = match["identity"]
        if identity is not None and identity.get("kind") == "ai":
            return max(self.match_threshold, 0.55)
        return self.match_threshold

    def _move_session_identity_to_persistent(self, identity: dict) -> None:
        identity_id = identity["identity_id"]
        self._session_identities.pop(identity_id, None)
        self._identities[identity_id] = identity

    def _promote_if_ready(
        self,
        identity: Optional[dict],
        voiceprint: dict,
        persistent: bool,
    ) -> Tuple[Optional[dict], dict, bool]:
        if voiceprint.get("status") == "confirmed" or self._progress(voiceprint) < 1.0:
            return identity, voiceprint, persistent

        voiceprint["status"] = "confirmed"
        voiceprint_id = voiceprint["voiceprint_id"]
        if identity is None:
            self._unassigned.pop(voiceprint_id, None)
            self._session_unassigned.pop(voiceprint_id, None)
            name = voiceprint.pop("provisional_name", f"陌生访客{self._guest_counter}")
            identity = self._new_identity(
                voiceprint_id,
                name,
                "guest",
                voiceprint,
                locked=False,
            )
            self._identities[voiceprint_id] = identity
            persistent = True
        else:
            if identity.get("kind") == "owner":
                identity["locked"] = True
                if identity.get("display_name") == "主人（注册中）":
                    identity["display_name"] = "主人"
            if not persistent:
                self._move_session_identity_to_persistent(identity)
                persistent = True

        self._save()
        print(
            f"[Speaker] promoted voiceprint {voiceprint_id} under "
            f"{identity.get('identity_id')} ({identity.get('display_name')}) "
            f"utterances={voiceprint.get('enroll_utterances')} "
            f"speech={voiceprint.get('total_speech_ms')}ms"
        )
        return identity, voiceprint, persistent

    # ------------------------------ identification ------------------------------

    def identify_only(self, values: Iterable[float], speech_ms: int) -> dict:
        """Match all voiceprints without creating or updating repository state."""
        embedding = _unit(values)
        if embedding is None or speech_ms < self.min_identify_ms:
            result = self._summary(None, None)
            result["speaker_self_confidence"] = 0.0
            return result

        with self._lock:
            best, self_score, _ = self._best_match(embedding)
            if best is None or best["score"] < self._match_threshold(best):
                result = self._summary(None, None, best["score"] if best else -1.0)
            else:
                result = self._summary(
                    best["identity"],
                    best["voiceprint"],
                    best["score"],
                    False,
                    best["persistent"],
                )
            result["speaker_self_confidence"] = round(max(0.0, self_score), 4)
            return result

    def identify_and_learn(self, values: Iterable[float], speech_ms: int) -> dict:
        embedding = _unit(values)
        if embedding is None or speech_ms < self.min_identify_ms:
            return self._summary(None, None)

        with self._lock:
            best, ai_score, ai_match = self._best_match(embedding)

            # A strong AI voiceprint match vetoes human learning even when a
            # contaminated guest voiceprint scores marginally higher.
            ai_veto_threshold = max(self.match_threshold, 0.55)
            if ai_match is not None and ai_score >= ai_veto_threshold:
                return self._summary(
                    ai_match["identity"],
                    ai_match["voiceprint"],
                    ai_score,
                    False,
                    ai_match["persistent"],
                )

            if best is not None and best["score"] >= self._match_threshold(best):
                identity = best["identity"]
                voiceprint = best["voiceprint"]
                persistent = best["persistent"]
                voiceprint["utterance_count"] = int(voiceprint.get("utterance_count", 0)) + 1
                voiceprint["last_seen"] = _utc_now()
                if identity is not None:
                    identity["last_seen"] = voiceprint["last_seen"]

                kind = identity.get("kind") if identity is not None else "guest"
                update_at = (
                    self.owner_update_threshold
                    if kind == "owner" and voiceprint.get("status") == "confirmed"
                    else self.update_threshold
                )
                if voiceprint.get("status") != "confirmed":
                    update_at = max(self.session_threshold, 0.52)
                quality_ok = speech_ms >= self.min_enroll_ms
                if quality_ok and kind != "ai" and best["score"] >= update_at:
                    self._append_embedding(voiceprint, embedding, speech_ms)

                identity, voiceprint, persistent = self._promote_if_ready(
                    identity, voiceprint, persistent
                )
                if persistent:
                    self._save()
                return self._summary(identity, voiceprint, best["score"], False, persistent)

            owner_exists = self.OWNER_ID in self._identities or self.OWNER_ID in self._session_identities
            if self.auto_owner_bootstrap and not owner_exists:
                voiceprint = self._new_voiceprint(
                    embedding,
                    speech_ms,
                    status="candidate",
                    voiceprint_id=f"voice_owner_{uuid.uuid4().hex[:8]}",
                )
                identity = self._new_identity(
                    self.OWNER_ID,
                    "主人（注册中）",
                    "owner",
                    voiceprint,
                    locked=False,
                )
                self._identities[self.OWNER_ID] = identity
                self._save()
                return self._summary(identity, voiceprint, 1.0, True, True)

            self._guest_counter += 1
            voiceprint_id = f"guest_{uuid.uuid4().hex[:10]}"
            voiceprint = self._new_voiceprint(
                embedding,
                speech_ms,
                status="candidate",
                voiceprint_id=voiceprint_id,
            )
            voiceprint["provisional_name"] = f"陌生访客{self._guest_counter}"
            self._session_unassigned[voiceprint_id] = voiceprint
            return self._summary(None, voiceprint, 1.0, True, False)

    # ------------------------------ explicit enrollment ------------------------------

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
            identity, persistent = self._find_identity(speaker_id)
            if replace or identity is None:
                voiceprint = self._new_voiceprint(
                    embedding,
                    speech_ms,
                    status="confirmed",
                    voiceprint_id=speaker_id,
                )
                voiceprint["enroll_utterances"] = max(
                    1, int(voiceprint.get("enroll_utterances", 0))
                )
                voiceprint["total_speech_ms"] = max(int(speech_ms), self.promote_speech_ms)
                identity = self._new_identity(
                    speaker_id,
                    display_name,
                    kind,
                    voiceprint,
                    locked=kind in ("owner", "ai"),
                )
                self._identities[speaker_id] = identity
                self._session_identities.pop(speaker_id, None)
            else:
                voiceprints = identity.get("voiceprints", [])
                voiceprint = next(
                    (vp for vp in voiceprints if vp.get("status") == "confirmed"),
                    voiceprints[0] if voiceprints else None,
                )
                if voiceprint is None:
                    voiceprint = self._new_voiceprint(embedding, speech_ms, "confirmed")
                    voiceprints.append(voiceprint)
                else:
                    self._append_embedding(voiceprint, embedding, speech_ms)
                identity["display_name"] = display_name.strip() or identity.get(
                    "display_name", speaker_id
                )
                identity["locked"] = bool(identity.get("locked", False)) or kind in (
                    "owner",
                    "ai",
                )
                identity["last_seen"] = _utc_now()
                voiceprint["status"] = "confirmed"
                if not persistent:
                    self._move_session_identity_to_persistent(identity)

            self._save()
            return self._summary(identity, voiceprint, 1.0, False, True)

    # ------------------------------ repository queries ------------------------------

    def has_profile(self, speaker_id: str) -> bool:
        with self._lock:
            identity, _ = self._find_identity(speaker_id, include_aliases=True)
            if identity is not None:
                return True
            voiceprint, _, _, _ = self._find_voiceprint(speaker_id)
            return voiceprint is not None

    def list_repository(self, include_session: bool = True) -> dict:
        with self._lock:
            identities = [
                self._identity_detail(identity, True) for identity in self._identities.values()
            ]
            unassigned = [
                self._voiceprint_summary(voiceprint, None, True, False)
                for voiceprint in self._unassigned.values()
            ]
            if include_session:
                identities.extend(
                    self._identity_detail(identity, False)
                    for identity in self._session_identities.values()
                )
                unassigned.extend(
                    self._voiceprint_summary(voiceprint, None, False, False)
                    for voiceprint in self._session_unassigned.values()
                )
            identities.sort(key=lambda item: (item["kind"] != "owner", item["display_name"], item["identity_id"]))
            unassigned.sort(key=lambda item: item["created_at"])
            return {
                "version": self.VERSION,
                "identities": identities,
                "unassigned_candidates": unassigned,
                "auto_organize_enabled": self.auto_organize_enabled,
                "auto_merge_threshold": self.auto_merge_threshold,
                "blocked_auto_pair_count": len(self._blocked_auto_pairs),
                "operations": [
                    self._public_operation(item) for item in self._operations[-20:]
                ],
            }

    def operation_history(self, limit: int = 20) -> List[dict]:
        with self._lock:
            count = max(1, min(100, int(limit)))
            return [
                self._public_operation(item) for item in self._operations[-count:]
            ]

    def describe_repository(self) -> str:
        """Compact, ID-complete summary intended for the character's read-only tool."""
        with self._lock:
            lines = [
                f"声纹仓库：{len(self._identities)} 个持久身份；"
                f"自动整理={'开启' if self.auto_organize_enabled else '关闭'}，"
                f"阈值={self.auto_merge_threshold:.2f}。"
            ]
            for identity in sorted(
                self._identities.values(),
                key=lambda item: (
                    item.get("kind") != "owner",
                    item.get("display_name", ""),
                    item.get("identity_id", ""),
                ),
            ):
                voiceprints = identity.get("voiceprints", [])
                vp_text = ", ".join(
                    f"{vp.get('voiceprint_id')}[{vp.get('status', 'candidate')}]"
                    for vp in voiceprints
                )
                lines.append(
                    f"- 身份 id={identity.get('identity_id')} name={identity.get('display_name')} "
                    f"kind={identity.get('kind')} locked={bool(identity.get('locked'))}；"
                    f"声纹：{vp_text or '无'}"
                )
            active = [item for item in self._operations if not item.get("undone_at")]
            if active:
                lines.append("最近可撤销的整理（只能从最后一项开始撤销）：")
                for item in active[-5:]:
                    public = self._public_operation(item)
                    lines.append(
                        f"- operation={public['operation_id']} mode={public['mode']} "
                        f"{public['source_name']}({public['source_id']}) -> "
                        f"{public['target_name']}({public['target_id']}) "
                        f"similarity={float(public['best_similarity']):.3f}；"
                        f"移动声纹={','.join(public['moved_voiceprint_ids'])}"
                    )
            else:
                lines.append("最近没有可撤销的身份合并。")
            lines.append("只能照抄这里出现的完整 ID；不要猜测或截短。")
            return "\n".join(lines)

    def list_profiles(self, include_session: bool = True) -> List[dict]:
        """Legacy flat summaries retained for existing clients and health output."""
        with self._lock:
            result = []
            for identity, persistent in self._all_identities():
                if not include_session and not persistent:
                    continue
                voiceprints = identity.get("voiceprints", [])
                representative = next(
                    (vp for vp in voiceprints if vp.get("status") == "confirmed"),
                    voiceprints[0] if voiceprints else None,
                )
                if representative is None:
                    continue
                item = self._summary(identity, representative, 1.0, False, persistent)
                item.update(
                    {
                        "utterance_count": self._aggregate(identity, "utterance_count"),
                        "enroll_utterances": self._aggregate(identity, "enroll_utterances"),
                        "total_speech_ms": self._aggregate(identity, "total_speech_ms"),
                        "locked": bool(identity.get("locked", False)),
                        "created_at": identity.get("created_at", ""),
                        "last_seen": identity.get("last_seen", ""),
                        "voiceprint_count": len(voiceprints),
                    }
                )
                result.append(item)
            for voiceprint, persistent in self._all_unassigned():
                if not include_session and not persistent:
                    continue
                item = self._summary(None, voiceprint, 1.0, False, persistent)
                item.update(
                    {
                        "utterance_count": int(voiceprint.get("utterance_count", 0)),
                        "enroll_utterances": int(voiceprint.get("enroll_utterances", 0)),
                        "total_speech_ms": int(voiceprint.get("total_speech_ms", 0)),
                        "locked": False,
                        "created_at": voiceprint.get("created_at", ""),
                        "last_seen": voiceprint.get("last_seen", ""),
                        "voiceprint_count": 1,
                    }
                )
                result.append(item)
            return result

    # ------------------------------ identity management ------------------------------

    def rename(self, speaker_id: str, display_name: str) -> dict:
        name = display_name.strip()
        if not name:
            raise ValueError("display_name is required")
        with self._lock:
            identity, persistent = self._find_identity(speaker_id, include_aliases=True)
            if identity is not None:
                identity["display_name"] = name[:64]
                if persistent:
                    self._save()
                voiceprint = identity.get("voiceprints", [None])[0]
                return self._summary(identity, voiceprint, 1.0, False, persistent)

            voiceprint, parent, persistent, assigned = self._find_voiceprint(speaker_id)
            if voiceprint is None:
                raise KeyError(speaker_id)
            if assigned and parent is not None:
                parent["display_name"] = name[:64]
                if persistent:
                    self._save()
                return self._summary(parent, voiceprint, 1.0, False, persistent)

            # Naming an unassigned candidate establishes a person folder while
            # preserving the voiceprint's candidate lifecycle.
            self._unassigned.pop(speaker_id, None)
            self._session_unassigned.pop(speaker_id, None)
            voiceprint.pop("provisional_name", None)
            identity = self._new_identity(speaker_id, name, "guest", voiceprint)
            if persistent:
                self._identities[speaker_id] = identity
                self._save()
            else:
                self._session_identities[speaker_id] = identity
            return self._summary(identity, voiceprint, 1.0, False, persistent)

    def merge_preview(
        self, source_id: str, target_id: str, source_type: str = ""
    ) -> dict:
        with self._lock:
            target, target_persistent = self._find_identity(target_id)
            if target is None:
                raise KeyError("target identity not found")
            source = None
            source_persistent = False
            if source_type != "voiceprint":
                source, source_persistent = self._find_identity(source_id)
            if source is not None:
                source_voiceprints = source.get("voiceprints", [])
                resolved_source_type = "identity"
                source_name = source.get("display_name", source_id)
            else:
                voiceprint, parent, source_persistent, _ = self._find_voiceprint(source_id)
                if voiceprint is None:
                    raise KeyError("source identity or voiceprint not found")
                source_voiceprints = [voiceprint]
                resolved_source_type = "voiceprint"
                source_name = (
                    parent.get("display_name", source_id)
                    if parent is not None
                    else voiceprint.get("provisional_name", source_id)
                )

            scores = []
            for source_voiceprint in source_voiceprints:
                source_centroid = _unit(source_voiceprint.get("centroid", []))
                if source_centroid is None:
                    continue
                for target_voiceprint in target.get("voiceprints", []):
                    scores.append(self._score(target_voiceprint, source_centroid))
            return {
                "source_id": source_id,
                "source_name": source_name,
                "source_type": resolved_source_type,
                "source_persistent": source_persistent,
                "source_voiceprint_count": len(source_voiceprints),
                "target_id": target_id,
                "target_name": target.get("display_name", target_id),
                "target_kind": target.get("kind", "guest"),
                "target_persistent": target_persistent,
                "target_voiceprint_count": len(target.get("voiceprints", [])),
                "best_similarity": round(max(scores), 4) if scores else -1.0,
                "match_threshold": self.match_threshold,
                "session_threshold": self.session_threshold,
            }

    def assign_voiceprint(self, voiceprint_id: str, target_identity_id: str) -> dict:
        if not voiceprint_id or not target_identity_id:
            raise ValueError("voiceprint_id and target_identity_id are required")
        with self._lock:
            snapshot = self._snapshot()
            try:
                target, target_persistent = self._find_identity(target_identity_id)
                if target is None:
                    raise KeyError("target identity not found")
                if target.get("kind") == "ai":
                    raise ValueError("human voiceprints cannot be assigned to AI_SELF")
                voiceprint, source, source_persistent, assigned = self._find_voiceprint(voiceprint_id)
                if voiceprint is None:
                    raise KeyError("voiceprint not found")
                if source is target:
                    return self._identity_detail(target, target_persistent)
                if source_persistent and not target_persistent:
                    raise ValueError("persistent voiceprints cannot move into a session identity")
                if source is not None and source.get("locked") and len(source.get("voiceprints", [])) <= 1:
                    raise ValueError("the last voiceprint of a locked identity cannot be moved")
                if source is not None and source.get("kind") == "ai":
                    raise ValueError("AI voiceprints cannot be reassigned")

                if assigned and source is not None:
                    source["voiceprints"].remove(voiceprint)
                    if not source["voiceprints"]:
                        self._identities.pop(source["identity_id"], None)
                        self._session_identities.pop(source["identity_id"], None)
                else:
                    self._unassigned.pop(voiceprint_id, None)
                    self._session_unassigned.pop(voiceprint_id, None)
                    voiceprint.pop("provisional_name", None)

                target.setdefault("voiceprints", []).append(voiceprint)
                target["last_seen"] = _latest(target.get("last_seen", ""), voiceprint.get("last_seen", ""))
                self._save(backup_reason=f"assign-{voiceprint_id}-to-{target_identity_id}")
                return self._identity_detail(target, target_persistent)
            except Exception:
                self._restore(snapshot)
                raise

    def detach_voiceprint(self, voiceprint_id: str) -> dict:
        with self._lock:
            snapshot = self._snapshot()
            try:
                voiceprint, source, persistent, assigned = self._find_voiceprint(voiceprint_id)
                if voiceprint is None or source is None or not assigned:
                    raise KeyError("assigned voiceprint not found")
                if source.get("kind") == "ai":
                    raise ValueError("AI voiceprints cannot be detached")
                if source.get("locked") and len(source.get("voiceprints", [])) <= 1:
                    raise ValueError("the last voiceprint of a locked identity cannot be detached")

                source["voiceprints"].remove(voiceprint)
                if not source["voiceprints"]:
                    self._identities.pop(source["identity_id"], None)
                    self._session_identities.pop(source["identity_id"], None)
                voiceprint["previous_status"] = voiceprint.get("status", "candidate")
                voiceprint["status"] = "candidate"
                voiceprint["provisional_name"] = source.get("display_name", voiceprint_id)
                if persistent:
                    self._unassigned[voiceprint_id] = voiceprint
                else:
                    self._session_unassigned[voiceprint_id] = voiceprint
                self._save(backup_reason=f"detach-{voiceprint_id}")
                return self._voiceprint_summary(voiceprint, None, persistent, False)
            except Exception:
                self._restore(snapshot)
                raise

    def confirm_voiceprint(self, voiceprint_id: str) -> dict:
        with self._lock:
            snapshot = self._snapshot()
            try:
                voiceprint, identity, persistent, assigned = self._find_voiceprint(voiceprint_id)
                if voiceprint is None or identity is None or not assigned:
                    raise ValueError("an unassigned voiceprint must be assigned before confirmation")
                voiceprint["status"] = "confirmed"
                voiceprint["enroll_utterances"] = max(
                    1, int(voiceprint.get("enroll_utterances", 0))
                )
                voiceprint["total_speech_ms"] = max(
                    self.min_enroll_ms, int(voiceprint.get("total_speech_ms", 0))
                )
                if identity.get("kind") == "owner":
                    identity["locked"] = True
                if not persistent:
                    self._move_session_identity_to_persistent(identity)
                    persistent = True
                self._save(backup_reason=f"confirm-{voiceprint_id}")
                return self._voiceprint_summary(voiceprint, identity, persistent, True)
            except Exception:
                self._restore(snapshot)
                raise

    def auto_organize(self, force: bool = False) -> List[dict]:
        """Conservatively merge persistent identities using mutual-nearest pairs."""
        if not self.auto_organize_enabled and not force:
            return []
        completed = []
        with self._lock:
            while True:
                eligible = [
                    identity
                    for identity in self._identities.values()
                    if self._eligible_for_auto_organize(identity)
                ]
                if len(eligible) < 2:
                    break

                nearest = {}
                for index, left in enumerate(eligible):
                    left_id = left["identity_id"]
                    for right in eligible[index + 1 :]:
                        right_id = right["identity_id"]
                        if tuple(sorted((left_id, right_id))) in self._blocked_auto_pairs:
                            continue
                        # Neither identity may be merged away when both are protected.
                        if left.get("locked") and right.get("locked"):
                            continue
                        score = self._identity_similarity(left, right)
                        if left_id not in nearest or score > nearest[left_id][1]:
                            nearest[left_id] = (right_id, score)
                        if right_id not in nearest or score > nearest[right_id][1]:
                            nearest[right_id] = (left_id, score)

                candidates = []
                for left_id, (right_id, score) in nearest.items():
                    if left_id >= right_id or score < self.auto_merge_threshold:
                        continue
                    reverse = nearest.get(right_id)
                    if reverse is not None and reverse[0] == left_id:
                        candidates.append((score, left_id, right_id))
                if not candidates:
                    break

                score, left_id, right_id = max(candidates)
                left = self._identities[left_id]
                right = self._identities[right_id]
                if self._auto_target_priority(left) >= self._auto_target_priority(right):
                    target, source = left, right
                else:
                    target, source = right, left
                result_name = self._auto_result_name(target, source)
                self.merge_identities(
                    source["identity_id"],
                    target["identity_id"],
                    display_name=result_name,
                    operation_mode="auto",
                )
                operation = self._public_operation(self._operations[-1])
                completed.append(operation)
                print(
                    f"[Speaker/Auto] {operation['source_id']} -> "
                    f"{operation['target_id']} score={score:.4f}",
                    flush=True,
                )
        return completed

    def merge_identities(
        self,
        source_id: str,
        target_id: str,
        display_name: str = "",
        operation_mode: str = "manual",
    ) -> dict:
        if source_id == target_id:
            raise ValueError("source and target must differ")
        with self._lock:
            snapshot = self._snapshot()
            try:
                source, source_persistent = self._find_identity(source_id)
                target, target_persistent = self._find_identity(target_id)
                if source is None or target is None:
                    raise KeyError("source or target identity not found")
                if source.get("kind") in ("owner", "ai") or source.get("locked"):
                    raise ValueError("locked identities cannot be merged away")
                if target.get("kind") == "ai":
                    raise ValueError("human identities cannot be merged into AI_SELF")
                if source_persistent and not target_persistent:
                    raise ValueError("a persistent identity cannot merge into a session identity")

                similarity = self.merge_preview(
                    source_id, target_id, source_type="identity"
                )["best_similarity"]
                operation_id = f"speaker_op_{uuid.uuid4().hex[:12]}"
                source_metadata = {
                    key: copy.deepcopy(value)
                    for key, value in source.items()
                    if key != "voiceprints"
                }
                target_name_before = target.get("display_name", target_id)
                target_aliases_before = copy.deepcopy(target.get("aliases", []))
                target_merged_from_before = copy.deepcopy(target.get("merged_from", []))
                existing_ids = {
                    vp.get("voiceprint_id") for vp in target.get("voiceprints", [])
                }
                moved_voiceprint_ids = []
                for voiceprint in source.get("voiceprints", []):
                    if voiceprint.get("voiceprint_id") in existing_ids:
                        voiceprint["voiceprint_id"] = f"voice_{uuid.uuid4().hex[:12]}"
                    target.setdefault("voiceprints", []).append(voiceprint)
                    existing_ids.add(voiceprint.get("voiceprint_id"))
                    moved_voiceprint_ids.append(voiceprint.get("voiceprint_id"))

                target["created_at"] = _earliest(
                    target.get("created_at", ""), source.get("created_at", "")
                )
                target["last_seen"] = _latest(
                    target.get("last_seen", ""), source.get("last_seen", "")
                )
                if display_name.strip():
                    target["display_name"] = display_name.strip()[:64]
                aliases = list(target.get("aliases", []))
                for alias in [source_id] + list(source.get("aliases", [])):
                    if alias and alias != target_id and alias not in aliases:
                        aliases.append(alias)
                target["aliases"] = aliases
                target.setdefault("merged_from", []).append(
                    {
                        "identity_id": source_id,
                        "display_name": source.get("display_name", source_id),
                        "merged_at": _utc_now(),
                        "operation_id": operation_id,
                        "best_similarity": similarity,
                        "voiceprint_count": len(source.get("voiceprints", [])),
                    }
                )

                self._identities.pop(source_id, None)
                self._session_identities.pop(source_id, None)
                self._append_operation(
                    {
                        "operation_id": operation_id,
                        "type": "merge",
                        "mode": operation_mode if operation_mode in ("auto", "manual", "character") else "manual",
                        "created_at": _utc_now(),
                        "undone_at": "",
                        "source_id": source_id,
                        "source_name": source.get("display_name", source_id),
                        "source_persistent": source_persistent,
                        "source_metadata": source_metadata,
                        "target_id": target_id,
                        "target_name_before": target_name_before,
                        "target_name_after": target.get("display_name", target_id),
                        "target_aliases_before": target_aliases_before,
                        "target_merged_from_before": target_merged_from_before,
                        "best_similarity": similarity,
                        "moved_voiceprint_ids": moved_voiceprint_ids,
                    }
                )
                if target_persistent:
                    self._save(
                        backup_reason=f"{operation_mode}-merge-{source_id}-into-{target_id}"
                    )
                return self._identity_detail(target, target_persistent)
            except Exception:
                self._restore(snapshot)
                raise

    # Backward-compatible name for the old endpoint/callers.
    def merge(self, source_id: str, target_id: str) -> dict:
        return self.merge_identities(source_id, target_id)

    def undo_merge(self, operation_id: str) -> dict:
        with self._lock:
            active = [item for item in self._operations if not item.get("undone_at")]
            operation = next(
                (item for item in reversed(active) if item.get("operation_id") == operation_id),
                None,
            )
            if operation is None:
                raise KeyError("active merge operation not found")
            if operation is not active[-1]:
                raise ValueError("merge operations must be undone newest first")
            if operation.get("type") != "merge":
                raise ValueError("operation is not an identity merge")

            snapshot = self._snapshot()
            try:
                source_id = operation["source_id"]
                target_id = operation["target_id"]
                if self._find_identity(source_id)[0] is not None:
                    raise ValueError("source identity already exists")
                target, target_persistent = self._find_identity(target_id)
                if target is None:
                    raise ValueError("target identity no longer exists; undo newer changes first")

                moved_ids = set(operation.get("moved_voiceprint_ids", []))
                moved = [
                    voiceprint
                    for voiceprint in target.get("voiceprints", [])
                    if voiceprint.get("voiceprint_id") in moved_ids
                ]
                if len(moved) != len(moved_ids):
                    raise ValueError("one or more moved voiceprints are no longer under the target")
                if len(moved) >= len(target.get("voiceprints", [])):
                    raise ValueError("undo would leave the target identity without a voiceprint")

                target["voiceprints"] = [
                    voiceprint
                    for voiceprint in target.get("voiceprints", [])
                    if voiceprint.get("voiceprint_id") not in moved_ids
                ]
                target["aliases"] = copy.deepcopy(
                    operation.get("target_aliases_before", [])
                )
                target["merged_from"] = copy.deepcopy(
                    operation.get("target_merged_from_before", [])
                )
                if target.get("display_name") == operation.get("target_name_after"):
                    target["display_name"] = operation.get(
                        "target_name_before", target.get("display_name", target_id)
                    )

                source = copy.deepcopy(operation.get("source_metadata", {}))
                source["identity_id"] = source_id
                source["voiceprints"] = moved
                if operation.get("source_persistent", True):
                    self._identities[source_id] = source
                else:
                    self._session_identities[source_id] = source
                operation["undone_at"] = _utc_now()
                # Block the entire two components, not only their current folder IDs.
                # A previous merge may have left an alias whose voiceprint still forms
                # the strongest bridge; blocking only source_id/target_id would let the
                # organizer immediately reconstruct the same mistaken cluster by alias.
                source_ids = [source_id] + list(source.get("aliases", []))
                target_ids = [target_id] + list(target.get("aliases", []))
                for left_id in source_ids:
                    for right_id in target_ids:
                        if left_id and right_id and left_id != right_id:
                            self._blocked_auto_pairs.add(
                                tuple(sorted((left_id, right_id)))
                            )
                if target_persistent or operation.get("source_persistent", True):
                    self._save(backup_reason=f"undo-{operation_id}")
                return {
                    "operation": self._public_operation(operation),
                    "restored_identity": self._identity_detail(
                        source, bool(operation.get("source_persistent", True))
                    ),
                    "target_identity": self._identity_detail(target, target_persistent),
                }
            except Exception:
                self._restore(snapshot)
                raise

    def delete_voiceprint(self, voiceprint_id: str) -> None:
        with self._lock:
            snapshot = self._snapshot()
            try:
                voiceprint, identity, persistent, assigned = self._find_voiceprint(voiceprint_id)
                if voiceprint is None:
                    raise KeyError(voiceprint_id)
                if identity is not None and identity.get("kind") == "ai":
                    raise ValueError("AI voiceprints cannot be deleted")
                if identity is not None and identity.get("locked") and len(identity.get("voiceprints", [])) <= 1:
                    raise ValueError("the last voiceprint of a locked identity cannot be deleted")

                if assigned and identity is not None:
                    identity["voiceprints"].remove(voiceprint)
                    if not identity["voiceprints"]:
                        self._identities.pop(identity["identity_id"], None)
                        self._session_identities.pop(identity["identity_id"], None)
                else:
                    self._unassigned.pop(voiceprint_id, None)
                    self._session_unassigned.pop(voiceprint_id, None)
                if persistent:
                    self._save(backup_reason=f"delete-voiceprint-{voiceprint_id}")
            except Exception:
                self._restore(snapshot)
                raise

    def delete_identity(self, identity_id: str, allow_locked: bool = False) -> None:
        with self._lock:
            identity, persistent = self._find_identity(identity_id)
            if identity is None:
                raise KeyError(identity_id)
            if identity.get("locked") and not allow_locked:
                raise ValueError("locked identity cannot be deleted")
            snapshot = self._snapshot()
            try:
                self._identities.pop(identity_id, None)
                self._session_identities.pop(identity_id, None)
                if persistent:
                    self._save(backup_reason=f"delete-identity-{identity_id}")
            except Exception:
                self._restore(snapshot)
                raise

    def delete(self, speaker_id: str, allow_locked: bool = False) -> None:
        """Legacy delete: prefer an exact identity, otherwise delete a voiceprint."""
        with self._lock:
            identity, _ = self._find_identity(speaker_id)
            if identity is not None:
                self.delete_identity(speaker_id, allow_locked=allow_locked)
                return
            self.delete_voiceprint(speaker_id)

    def reset_owner(self) -> None:
        with self._lock:
            snapshot = self._snapshot()
            try:
                self._identities.pop(self.OWNER_ID, None)
                self._session_identities.pop(self.OWNER_ID, None)
                self._save(backup_reason="reset-owner")
            except Exception:
                self._restore(snapshot)
                raise
