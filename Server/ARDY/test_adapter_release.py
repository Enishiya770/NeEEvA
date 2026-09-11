"""Synthetic CPU release-proof tests; these fixtures are not real acceptance evidence."""
import copy
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from Server.ARDY.motion_service.adapter_release import (ACTIVE_RELATIVE, create_release,
    digest, load_release_cpu, validate_evidence)
from Server.ARDY.motion_service.app import adapter_configuration
from Server.ARDY.motion_service.protocol import FEATURE_CONTRACT, QWEN_MODEL_SHA256


class ReleaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from Server.ARDY.test_adapter_selection import CandidateLockTests
        cls.fixture = CandidateLockTests
        cls.fixture.setUpClass()

    @classmethod
    def tearDownClass(cls):
        cls.fixture.tearDownClass()

    def setUp(self):
        f = self.fixture
        f.selection.write_text(json.dumps(f.lock), encoding="utf-8")
        self.sha = f.lock["checkpoint_sha256"]
        self.selection_sha = digest(f.selection.read_bytes())
        self.folder = f.root / self._testMethodName
        self.folder.mkdir()
        supported = [f"s{i:03}" for i in range(64)]
        boundary = [f"b{i:03}" for i in range(16)]
        results = []
        for identity in supported:
            for name, modelsha in (("teacher", None), ("old", f.baseline_sha), ("candidate", self.sha)):
                for seed in (0, 1):
                    results.append({"recordId": identity, "condition": name, "history": "grounded16", "seed": seed,
                                    "conditionProvenance": {"adapterSha256": modelsha},
                                    "windows": [{"window": i, "historyFrames": 16} for i in range(3)]})
        feature = {"status": "complete", "explicit_final_test": True, "used_for_selection": False,
                   "plan": {"lockedCandidateSha256": self.sha, "selectionSha256": self.selection_sha},
                   "per_record": [{"id": r, "track": "supported_upper_body"} for r in supported]
                               + [{"id": r, "track": "capability_boundary"} for r in boundary]}
        native = {"status": "complete", "plan": {"finalTest": True, "split": "test", "recordIds": supported,
                  "adapters": {"old": {"sha256": f.baseline_sha}, "candidate": {"sha256": self.sha}},
                  "histories": {"grounded16": {}}, "seeds": [0, 1], "windows": 3, "continuationHistoryFrames": 16},
                  "results": results}
        http = {"passed": True, "mode": "locked-candidate-live-http", "checkpointSha256": self.sha,
                "selectionSha256": self.selection_sha, "initialHealth": {"backend": {"adapterSha256": self.sha}},
                "recordIds": ["v"], "cases": [{"generatedFrames": 120, "responses": [{}, {}, {}]}]}
        trace = {"finishReason": "completed", "characterId": "test", "turnId": "turn", "revision": 1,
                 "description": "Synthetic evidence fixture", "seed": 0, "entries": []}
        for i in range(3):
            request = {key: trace[key] for key in ("characterId", "turnId", "revision", "description", "seed")}
            request.update(chunkIndex=i, requestId=f"r{i}")
            if i == 0:
                request["initialHistory"] = {"frames": [{}]*16}
            response = {key: request[key] for key in ("characterId", "turnId", "revision", "requestId", "chunkIndex")}
            response.update(newFrames=40, startFrame=i*40, final=i == 2, historyFrames=16,
                            clip={"source": {"adapterSha256": self.sha}, "frames": [{}]*40},
                            provenance={"featureContract": FEATURE_CONTRACT, "modelSha256": QWEN_MODEL_SHA256,
                                        "featureSource": "live-qwen" if i == 0 else "live-condition-reused", "featureRequestId": "r0"})
            trace["entries"].extend([{"kind": "request", "json": json.dumps(request)},
                                     {"kind": "response", "httpStatus": 200, "json": json.dumps(response)}])
        unity = {"status": "passed", "receivedChunks": 3, "timelineFrames": 120, "diagnosticResponses": 3,
                 "diagnosticTracePath": str(self.folder / "unityTrace.json")}
        self.documents = dict(selection=f.lock, nativeFinal=native, featureFinal=feature, httpLive=http,
                              unityLive=unity, unityTrace=trace)
        for key, data in self.documents.items():
            if key != "selection":
                (self.folder / (key + ".json")).write_text(json.dumps(data), encoding="utf-8")

    def create(self, output=None):
        return create_release(self.fixture.root, self.fixture.selection, self.folder / "nativeFinal.json",
                              self.folder / "featureFinal.json", self.folder / "httpLive.json", self.folder / "unityLive.json",
                              output or self.folder / "preview.json")

    def test_complete_create_archive_and_cpu_verify_without_activation(self):
        value = self.create()
        self.assertFalse((self.fixture.root / ACTIVE_RELATIVE).exists())
        (self.folder / "unityTrace.json").unlink()  # The live eight-file ring may rotate after publication.
        model, payload, info = load_release_cpu(self.fixture.root, self.folder / "preview.json")
        self.assertEqual(info["adapterMode"], "verified-preview")
        self.assertEqual(info["releaseId"], value["releaseId"])
        self.assertFalse(info["naturalnessAccepted"])
        self.assertFalse(payload["candidate"]["approved_for_runtime"])
        self.assertEqual(next(model.parameters()).device.type, "cpu")

    def test_incomplete_or_wrong_model_reports_fail(self):
        for mutation in ("native_val", "missing_trajectory", "feature_running", "http_model", "unity_failed", "trace_missing", "trace_other_model"):
            with self.subTest(mutation=mutation):
                docs = copy.deepcopy(self.documents)
                if mutation == "native_val": docs["nativeFinal"]["plan"]["finalTest"] = False
                elif mutation == "missing_trajectory": docs["nativeFinal"]["results"].pop()
                elif mutation == "feature_running": docs["featureFinal"]["status"] = "running"
                elif mutation == "http_model": docs["httpLive"]["checkpointSha256"] = "0"*64
                elif mutation == "unity_failed": docs["unityLive"]["status"] = "failed"
                elif mutation == "trace_missing": docs["unityTrace"]["entries"].pop()
                else:
                    entry = docs["unityTrace"]["entries"][1]
                    response = json.loads(entry["json"])
                    response["clip"]["source"]["adapterSha256"] = "0"*64
                    entry["json"] = json.dumps(response)
                with self.assertRaises(ValueError):
                    validate_evidence(docs, self.sha, self.selection_sha, self.fixture.baseline_sha)

    def test_changed_archived_evidence_or_manifest_fails_closed(self):
        manifest = self.create()
        evidence = Path(manifest["evidence"]["httpLive"]["path"])
        evidence.write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "evidence changed"):
            load_release_cpu(self.fixture.root, self.folder / "preview.json")
        path = self.folder / "preview.json"
        manifest["checkpointSha256"] = "0"*64
        path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "manifest"):
            load_release_cpu(self.fixture.root, path)

    def test_creation_cannot_write_active_or_overwrite_manifest(self):
        with self.assertRaisesRegex(ValueError, "active"):
            self.create(self.fixture.root / ACTIVE_RELATIVE)
        self.create()
        with self.assertRaises(FileExistsError):
            self.create()

    def test_default_active_explicit_selection_and_baseline_resolution(self):
        root = self.folder
        args = SimpleNamespace(baseline_adapter=False, adapter_selection=None, adapter_release=None)
        self.assertEqual(adapter_configuration(args, root), (None, None))
        active = root / ACTIVE_RELATIVE
        active.parent.mkdir(parents=True)
        active.write_text("bad json", encoding="utf-8")
        self.assertEqual(adapter_configuration(args, root), (None, active))
        with self.assertRaises(ValueError):
            load_release_cpu(root, active)
        args.adapter_selection = Path("explicit-selection.json")
        self.assertEqual(adapter_configuration(args, root), (args.adapter_selection, None))
        args.adapter_selection = None
        args.baseline_adapter = True
        self.assertEqual(adapter_configuration(args, root), (None, None))

    def test_cli_modes_are_mutually_exclusive(self):
        from Server.ARDY.motion_service.app import main
        with patch("sys.argv", ["app", "--adapter-release", "r.json", "--baseline-adapter"]):
            with self.assertRaises(SystemExit) as result:
                main()
            self.assertEqual(result.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
