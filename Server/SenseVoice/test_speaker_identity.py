import json
import os
import sys
import tempfile
import unittest

import numpy as np


sys.path.insert(0, os.path.dirname(__file__))
from speaker_identity import SpeakerIdentityStore  # noqa: E402


def unit(*values):
    vector = np.asarray(values, dtype=np.float32)
    return vector / np.linalg.norm(vector)


class SpeakerIdentityStoreTests(unittest.TestCase):
    def make_store(self, path, **overrides):
        options = {
            "auto_owner_bootstrap": False,
            "auto_organize": False,
            "promote_utterances": 3,
            "promote_speech_ms": 10000,
        }
        options.update(overrides)
        return SpeakerIdentityStore(path, **options)

    def test_migrates_v1_profile_without_blending_or_loss(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            legacy = {
                "version": 1,
                "profiles": [
                    {
                        "speaker_id": "owner",
                        "display_name": "ユウ",
                        "kind": "owner",
                        "status": "confirmed",
                        "locked": True,
                        "centroid": unit(1, 0, 0).tolist(),
                        "exemplars": [unit(1, 0, 0).tolist()],
                        "utterance_count": 12,
                        "enroll_utterances": 6,
                        "total_speech_ms": 42000,
                        "created_at": "2026-01-01T00:00:00+00:00",
                        "last_seen": "2026-01-02T00:00:00+00:00",
                    }
                ],
            }
            with open(path, "w", encoding="utf-8") as handle:
                json.dump(legacy, handle)

            store = self.make_store(path)
            repository = store.list_repository()

            self.assertEqual(2, repository["version"])
            self.assertEqual(1, len(repository["identities"]))
            identity = repository["identities"][0]
            self.assertEqual("owner", identity["identity_id"])
            self.assertEqual("ユウ", identity["display_name"])
            self.assertEqual(1, identity["voiceprint_count"])
            self.assertEqual("owner", identity["voiceprints"][0]["voiceprint_id"])

            with open(path, "r", encoding="utf-8") as handle:
                migrated = json.load(handle)
            self.assertEqual(2, migrated["version"])
            backup_dir = os.path.join(directory, "speaker_profile_backups")
            self.assertTrue(os.path.isdir(backup_dir))
            self.assertEqual(1, len(os.listdir(backup_dir)))

    def test_identity_matches_best_child_voiceprint_not_average(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)

            candidate = store.identify_and_learn(unit(0, 1, 0), 2000)
            voiceprint_id = candidate["speaker_voiceprint_id"]
            store.assign_voiceprint(voiceprint_id, "owner")

            result = store.identify_only(unit(0, 1, 0), 2000)
            self.assertEqual("owner", result["speaker_id"])
            self.assertEqual("ユウ", result["speaker_name"])
            self.assertEqual(voiceprint_id, result["speaker_voiceprint_id"])
            self.assertEqual("candidate", result["speaker_voiceprint_status"])

    def test_candidate_updates_itself_without_contaminating_confirmed_sibling(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)
            candidate = store.identify_and_learn(unit(0, 1, 0), 2000)
            voiceprint_id = candidate["speaker_voiceprint_id"]
            store.assign_voiceprint(voiceprint_id, "owner")

            confirmed_before = np.asarray(
                store._identities["owner"]["voiceprints"][0]["centroid"],
                dtype=np.float32,
            )
            store.identify_and_learn(unit(0.02, 1, 0), 2000)
            confirmed_after = np.asarray(
                store._identities["owner"]["voiceprints"][0]["centroid"],
                dtype=np.float32,
            )

            np.testing.assert_allclose(confirmed_before, confirmed_after, atol=1e-6)
            child = next(
                vp
                for vp in store._identities["owner"]["voiceprints"]
                if vp["voiceprint_id"] == voiceprint_id
            )
            self.assertEqual(2, child["enroll_utterances"])
            self.assertEqual("candidate", child["status"])

    def test_identity_merge_reparents_voiceprints_and_keeps_target_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)
            store.enroll_fixed(
                "guest_variant", "小悠", "guest", unit(0, 1, 0), 12000
            )

            result = store.merge_identities(
                "guest_variant", "owner", display_name="ユウ"
            )

            self.assertEqual("owner", result["identity_id"])
            self.assertEqual("owner", result["kind"])
            self.assertEqual(2, result["voiceprint_count"])
            self.assertIn("guest_variant", result["aliases"])
            self.assertNotIn("guest_variant", store._identities)
            matched = store.identify_only(unit(0, 1, 0), 2000)
            self.assertEqual("owner", matched["speaker_id"])
            self.assertEqual("guest_variant", matched["speaker_voiceprint_id"])

    def test_unassigned_candidate_stays_independent_until_manual_assignment(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)

            candidate = store.identify_and_learn(unit(0, 1, 0), 2000)
            voiceprint_id = candidate["speaker_voiceprint_id"]
            repository = store.list_repository(include_session=True)
            self.assertEqual(1, len(repository["unassigned_candidates"]))
            self.assertEqual("", candidate["speaker_identity_id"])

            store.assign_voiceprint(voiceprint_id, "owner")
            repository = store.list_repository(include_session=True)
            self.assertEqual(0, len(repository["unassigned_candidates"]))
            self.assertEqual(2, repository["identities"][0]["voiceprint_count"])

    def test_cannot_delete_last_voiceprint_of_locked_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)

            with self.assertRaises(ValueError):
                store.delete_voiceprint("owner")

    def test_detach_resets_only_selected_voiceprint_to_unassigned_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)
            store.enroll_fixed("variant", "別声線", "guest", unit(0, 1, 0), 12000)
            store.merge_identities("variant", "owner")

            detached = store.detach_voiceprint("variant")

            self.assertFalse(detached["assigned"])
            self.assertEqual("candidate", detached["status"])
            repository = store.list_repository()
            owner = next(item for item in repository["identities"] if item["identity_id"] == "owner")
            self.assertEqual(1, owner["voiceprint_count"])
            self.assertEqual("variant", repository["unassigned_candidates"][0]["voiceprint_id"])

    def test_naming_unassigned_candidate_creates_identity_folder(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            candidate = store.identify_and_learn(unit(0, 1, 0), 2000)

            renamed = store.rename(candidate["speaker_voiceprint_id"], "访客甲")

            self.assertEqual("访客甲", renamed["speaker_name"])
            self.assertEqual(candidate["speaker_voiceprint_id"], renamed["speaker_id"])
            repository = store.list_repository(include_session=True)
            self.assertEqual(1, len(repository["identities"]))
            self.assertEqual(0, len(repository["unassigned_candidates"]))
            self.assertEqual("candidate", repository["identities"][0]["status"])

    def test_human_voiceprint_cannot_be_assigned_to_ai_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("ai_self", "角色自己", "ai", unit(1, 0, 0), 12000)
            candidate = store.identify_and_learn(unit(0, 1, 0), 2000)

            with self.assertRaises(ValueError):
                store.assign_voiceprint(candidate["speaker_voiceprint_id"], "ai_self")

    def test_preview_can_disambiguate_voiceprint_id_from_same_identity_id(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "ユウ", "owner", unit(1, 0, 0), 12000)
            store.enroll_fixed("guest_a", "访客甲", "guest", unit(0, 1, 0), 12000)
            candidate = store.identify_and_learn(unit(0, 0, 1), 2000)
            store.assign_voiceprint(candidate["speaker_voiceprint_id"], "guest_a")

            identity_preview = store.merge_preview("guest_a", "owner", "identity")
            voiceprint_preview = store.merge_preview("guest_a", "owner", "voiceprint")

            self.assertEqual(2, identity_preview["source_voiceprint_count"])
            self.assertEqual("identity", identity_preview["source_type"])
            self.assertEqual(1, voiceprint_preview["source_voiceprint_count"])
            self.assertEqual("voiceprint", voiceprint_preview["source_type"])

    def test_auto_organize_iteratively_folds_voice_cluster_into_owner(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            owner = unit(1, 0, 0)
            bridge = unit(0.66, 0.7512656, 0)
            variant = unit(0.57, 0.44165, 0.6924)
            store.enroll_fixed("owner", "主人", "owner", owner, 12000)
            store.enroll_fixed("guest_a", "小悠", "guest", bridge, 12000)
            store.enroll_fixed("guest_b", "小悠", "guest", variant, 12000)
            store.auto_organize_enabled = True
            store.auto_min_enroll_utterances = 1

            operations = store.auto_organize()

            self.assertEqual(2, len(operations))
            self.assertTrue(all(item["mode"] == "auto" for item in operations))
            repository = store.list_repository()
            self.assertEqual(1, len(repository["identities"]))
            identity = repository["identities"][0]
            self.assertEqual("owner", identity["identity_id"])
            self.assertEqual("小悠", identity["display_name"])
            self.assertEqual(3, identity["voiceprint_count"])

    def test_undo_auto_merge_is_lifo_and_blocks_automatic_repeat(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "speaker_profiles.json")
            store = self.make_store(path)
            store.enroll_fixed("owner", "主人", "owner", unit(1, 0, 0), 12000)
            store.enroll_fixed(
                "guest_a", "小悠", "guest", unit(0.66, 0.7512656, 0), 12000
            )
            store.enroll_fixed(
                "guest_b", "小悠", "guest", unit(0.57, 0.44165, 0.6924), 12000
            )
            store.auto_organize_enabled = True
            store.auto_min_enroll_utterances = 1
            operations = store.auto_organize()

            with self.assertRaises(ValueError):
                store.undo_merge(operations[0]["operation_id"])

            store.undo_merge(operations[1]["operation_id"])
            self.assertEqual([], store.auto_organize())
            repository = store.list_repository()
            self.assertEqual(2, len(repository["identities"]))
            self.assertEqual(2, repository["blocked_auto_pair_count"])

            store.undo_merge(operations[0]["operation_id"])
            self.assertEqual([], store.auto_organize())
            repository = store.list_repository()
            self.assertEqual(3, len(repository["identities"]))
            self.assertEqual(3, repository["blocked_auto_pair_count"])

            reloaded = self.make_store(path)
            self.assertEqual(3, len(reloaded.list_repository()["identities"]))
            self.assertEqual(3, reloaded.list_repository()["blocked_auto_pair_count"])


if __name__ == "__main__":
    unittest.main()
