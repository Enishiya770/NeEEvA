using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomDecisionBudgetRegression
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static JObject LastReport { get; private set; }
    private static int checks;

    public static int RunChecks()
    {
        checks = 0;
        var host = new GameObject("InactiveRoomDecisionBudgetFixture"); host.SetActive(false);
        LastReport = new JObject { ["status"] = "running", ["httpRequests"] = 0,
            ["scope"] = "Full production decision serializer, large evidence projection and preflight admission. Estimates are not tokenizer counts. No model or action execution." };
        try
        {
            var provider = host.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.Local;
            var source = BuildLargeInput();
            string rawInput = source.ToString(Formatting.None);
            string wire = Wire(provider, rawInput);
            var rawDiagnostic = (JObject)Call(null, "BuildRoomTaskRequestDiagnostic", wire);
            Require((int)rawDiagnostic["textCharacters"] >= 201541 && source["visibleObjectCatalog"].ToString(Formatting.None).Length >= 96000,
                "Stress input meets or exceeds the observed 201541-character request / 96000-character catalog failure.");
            var result = Budget(wire, false);
            Require((bool)result["ok"], "Large ordinary evidence can be projected before its first HTTP: " + result["diagnostic"]);
            string admitted = (string)result["wire"];
            var projected = Input(admitted);
            var sharedTable = projected["requestEvidenceTable"] as JObject;
            Require(sharedTable == null || !sharedTable.Descendants().OfType<JProperty>().Any(property => property.Name == "requestEvidenceRef"),
                "Cross-branch shared scope/pose/bearing values never create nested request evidence references.");
            Require((bool)Call(null, "RestoreRoomDecisionSharedEvidence", projected), "Admitted shared evidence is valid and expandable.");
            foreach (string field in new[] { "currentUserText", "latestUserText", "autonomous", "currentFacts", "currentSupport", "visualObservation",
                "decisionContractFacts", "pendingObservationGoal", "objectTargetReview", "referentSources", "unknownSafetyConstraint" })
                Require(JToken.DeepEquals(source[field], projected[field]), "Protected current evidence / authority remains exact: " + field);
            Require(JToken.DeepEquals(source["sceneSpatialMemory"]["dialogueViews"], projected["sceneSpatialMemory"]["dialogueViews"]) &&
                JToken.DeepEquals(source["turnSweep"], projected["turnSweep"]), "Attached historical view IDs and their pose/metadata pointers are never orphaned.");
            var beforeWire = JObject.Parse(wire); var afterWire = JObject.Parse(admitted);
            Require(JToken.DeepEquals(beforeWire["response_format"], afterWire["response_format"]) &&
                JToken.DeepEquals(beforeWire["messages"][0], afterWire["messages"][0]) &&
                JToken.DeepEquals(((JArray)beforeWire["messages"]).Last, ((JArray)afterWire["messages"]).Last),
                "Full native schema, system contract and final original-turn authority are protected from projection.");
            var candidates = (JArray)projected["visibleObjectCatalog"]["current"]["candidates"];
            Require(candidates.Count == 6 && (string)projected["visibleObjectCatalog"]["current"]["catalogId"] == "catalog-current" &&
                (int)projected["visibleObjectCatalog"]["current"]["pageIndex"] == 2,
                "Every current candidate and its page/catalog namespace survives even when optional history is bounded.");
            for (int index = 0; index < candidates.Count; ++index)
                foreach (string key in new[] { "entityId", "referenceId", "sourceCaptureId", "imagePoint", "pointAtCapture", "directionFromCurrentPose", "modelDescription" })
                    Require(JToken.DeepEquals(source["visibleObjectCatalog"]["current"]["candidates"][index][key], candidates[index][key]),
                        "Current identity/source/pixel/world-point/body-camera direction remains exact: " + key);
            var memory = (JArray)projected["visibleObjectCatalog"]["memory"];
            Require(memory.OfType<JObject>().Any(row => (string)row["entityId"] == "remembered-59") &&
                memory.OfType<JObject>().Any(row => (string)row["entityId"] == "remembered-58") &&
                memory.OfType<JObject>().Any(row => row["futureAuthorizationConstraint"] != null),
                "Bound/pending references and unknown future constraints cannot be evicted by earlier generic memory entries.");
            foreach (int index in new[] { 55, 56 })
                Require(JToken.DeepEquals(source["visibleObjectCatalog"]["memory"][index],
                    memory.OfType<JObject>().Single(row => (string)row["entityId"] == "remembered-" + index)),
                    "Unknown nested authored/hierarchy constraints protect the whole row, including diagnostic containers.");
            Require(memory.Count < 60, "Ordinary existing authored/geometry/pose keys remain projectable; nested guarding cannot make every known row mandatory.");
            Require(projected["decisionEvidenceProjection"]?["changes"] is JArray changes && changes.OfType<JObject>().Any(row => (int?)row["omittedCount"] > 0) &&
                (bool?)projected["decisionEvidenceProjection"]["engineMemoryMutated"] == false && rawInput == source.ToString(Formatting.None),
                "Every omission is visible in request metadata and the complete source/engine memory stays unchanged.");
            Require((string)projected["spatialGoal"]["originalRequest"] == (string)source["spatialGoal"]["originalRequest"] &&
                JToken.DeepEquals(source["spatialGoal"]["completionEvidence"], projected["spatialGoal"]["completionEvidence"]) &&
                JToken.DeepEquals(source["spatialGoal"]["searchProgress"], projected["spatialGoal"]["searchProgress"]),
                "Unknown terminal trace shape remains exact alongside original authority, bound identity and actual result validity.");

            // A current cognition view can contain objects on catalog pages not
            // currently offered. They must survive removal of duplicated arrays.
            var cognitionInput = BuildLargeInput();
            cognitionInput["visualEvidence"] = "[current camera evidence]\n" + "\n[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]\n" +
                new JObject { ["currentCaptureId"] = "capture-current", ["currentObjects"] = new JArray(new JObject {
                    ["entityId"] = "other-current-page", ["sourceCaptureId"] = "capture-current", ["pointAtCapture"] = new JObject { ["x"] = 3 },
                    ["modelDescription"] = "A separate current-page object" }), ["historicalObjects"] = new JArray(),
                    ["selfPose"] = source["sceneSpatialMemory"]["selfPose"].DeepClone() }.ToString(Formatting.None);
            var cognitionResult = Budget(Wire(provider, cognitionInput.ToString(Formatting.None)), false);
            Require((bool)cognitionResult["ok"], "Large cognition input is admitted without treating other pages as absent.");
            var cognitionProjected = Input((string)cognitionResult["wire"]); Call(null, "RestoreRoomDecisionSharedEvidence", cognitionProjected);
            Require((string)cognitionProjected["sceneCognitionSummary"]["currentObjects"][0]["entityId"] == "other-current-page" &&
                JToken.DeepEquals(source["sceneSpatialMemory"]["selfPose"], cognitionProjected["sceneCognitionSummary"]["selfPose"]),
                "Unrepresented current-page objects and current/prior self-pose survive cognition deduplication.");

            var canonical = new JObject { ["entityId"] = "same", ["sourceCaptureId"] = "same-capture", ["actionAuthorization"] = true,
                ["positionMemoryValid"] = true, ["authoredHint"] = new JObject { ["source"] = "engine" },
                ["directionFromCurrentPose"] = new JObject { ["relativeToCurrentBody"] = new JObject { ["yawDegrees"] = 23 } } };
            foreach (var annotation in new[] {
                new JObject { ["actionAuthorization"] = false }, new JObject { ["positionMemoryValid"] = false },
                new JObject { ["bearing"] = new JObject { ["bodyYawDegreesRight"] = 24 } },
                new JObject { ["bearing"] = new JObject { ["bodyYawDegreesRight"] = 23, ["futureConstraint"] = "do not rotate" } },
                new JObject { ["authoredHint"] = new JObject { ["source"] = "engine", ["futureConstraint"] = false } } })
            {
                annotation["entityId"] = "same"; annotation["sourceCaptureId"] = "same-capture";
                Require(!(bool)Call(null, "RoomDecisionRowRepresented", annotation, new JArray(canonical.DeepClone()), true),
                    "Same entity/capture cannot erase a differing authority, pose validity, yaw or nested future constraint.");
            }
            var equivalent = new JObject { ["entityId"] = "same", ["sourceCaptureId"] = "same-capture",
                ["bearing"] = new JObject { ["bodyYawDegreesRight"] = 23 }, ["currentOpaquePixels"] = true };
            Require((bool)Call(null, "RoomDecisionRowRepresented", equivalent, new JArray(canonical.DeepClone()), true),
                "Explicit current body-yaw mapping can prove equivalent cognition evidence without treating camera yaw as body yaw.");
            var producerCandidate = (JObject)canonical.DeepClone(); producerCandidate.Remove("actionAuthorization");
            producerCandidate["authoredHint"]["actionAuthorization"] = false;
            producerCandidate["directionFromCurrentPose"]["actionAuthorization"] = false;
            equivalent["actionAuthorization"] = false;
            Require((bool)Call(null, "RoomDecisionRowRepresented", equivalent, new JArray(producerCandidate.DeepClone()), true),
                "Real Candidate shape has no top-level authority, but its explicit authored and geometry no-authority facts represent cognition's false without inventing permission.");
            producerCandidate["authoredHint"]["actionAuthorization"] = true;
            Require(!(bool)Call(null, "RoomDecisionRowRepresented", equivalent, new JArray(producerCandidate.DeepClone()), true),
                "An explicit authority conflict cannot be treated as duplicate current cognition.");
            producerCandidate["authoredHint"].Parent.Remove();
            Require(!(bool)Call(null, "RoomDecisionRowRepresented", equivalent, new JArray(producerCandidate.DeepClone()), true),
                "Missing authority proof is never defaulted to false for deduplication.");

            var noOptional = BuildLargeInput();
            noOptional["visibleObjectCatalog"]["memory"] = new JArray(new JObject { ["entityId"] = "unbound-large-description-a", ["modelDescription"] = new string('a', 100000) },
                new JObject { ["entityId"] = "unbound-large-description-b", ["modelDescription"] = new string('b', 100000) });
            noOptional["sceneSpatialMemory"]["objects"] = new JArray();
            var zeroResult = Budget(Wire(provider, noOptional.ToString(Formatting.None)), false);
            Require((bool)zeroResult["ok"] && (int?)zeroResult["diagnostic"]["optionalHistoryRowsPerCollection"] == 0,
                "First admission tries zero optional rows before blaming protected input for a budget failure.");

            var accountingWire = JObject.Parse(admitted);
            var baseMeasure = (JObject)Call(null, "MeasureRoomDecisionBudget", accountingWire.ToString(Formatting.None), 49152);
            var imageParts = new JArray(new JObject { ["type"] = "text", ["text"] = "historical-view-label!" });
            ((JArray)accountingWire["messages"]).Add(new JObject { ["role"] = "user", ["content"] = imageParts });
            for (int imageIndex = 1; imageIndex <= 5; ++imageIndex)
            {
                imageParts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64,SYNTHETIC" } });
                var charged = (JObject)Call(null, "MeasureRoomDecisionBudget", accountingWire.ToString(Formatting.None), 49152);
                Require((int)charged["imageCount"] == imageIndex && (long)charged["estimatedUnits"] - (long)baseMeasure["estimatedUnits"] ==
                    imageIndex * 4096L + (long)Call(null, "EstimateRoomDecisionTextUnits", "historical-view-label!"),
                    "Each image is charged a fixed reserve plus its historical label, excluding base64 from text accounting.");
            }
            var shortImage = (JObject)Call(null, "MeasureRoomDecisionBudget", accountingWire.ToString(Formatting.None), 49152);
            imageParts[1]["image_url"]["url"] = "data:image/png;base64," + new string('x', 10000);
            var longImage = (JObject)Call(null, "MeasureRoomDecisionBudget", accountingWire.ToString(Formatting.None), 49152);
            Require((long)shortImage["estimatedUnits"] == (long)longImage["estimatedUnits"] &&
                (long)shortImage["textUtf8Bytes"] == (long)longImage["textUtf8Bytes"] &&
                (int)longImage["wireUtf8Bytes"] > (int)shortImage["wireUtf8Bytes"], "Encoded image bytes affect the wire limit, not text-token estimate.");

            var impossible = BuildLargeInput(); impossible["currentUserText"] = new string('密', 80000);
            impossible["unknownSafetyConstraint"] = new JObject { ["forbidMotion"] = true, ["details"] = new string('約', 80000) };
            string impossibleWire = Wire(provider, impossible.ToString(Formatting.None));
            var blocked = Budget(impossibleWire, false);
            Require(!(bool)blocked["ok"] && (string)blocked["wire"] == impossibleWire && (bool?)blocked["diagnostic"]["admitted"] == false,
                "Oversized exact user words or unknown constraints stop before HTTP; neither is silently cut to make admission pass.");
            Require(ChatQW.TryReadRoomTaskFailureStatus("room-task-http-context-budget;attempts=0;recovery=preflight-budget", out var failure) &&
                (int?)failure["attempts"] == 0 && (bool?)failure["httpRequestSent"] == false && (bool?)failure["retryAttempted"] == false &&
                (string)failure["stage"] == "decision-preflight", "Local budget rejection cannot be described as a provider refusal or an attempted retry.");
            var initialMeasure = (JObject)result["diagnostic"]["final"];
            Require((long)initialMeasure["estimatedUnits"] <= (int)initialMeasure["unitBudget"] && (int)initialMeasure["schemaUtf8Bytes"] > 0 &&
                (int)initialMeasure["completionReserve"] == 640 && (int)initialMeasure["templateAndSafetyReserve"] > 0,
                "Admission charges system/suffix texts, full grammar, completion and framing reserve together.");
            Require(((JArray)initialMeasure["messageText"]).OfType<JObject>().Sum(row => (long)row["textUtf8Bytes"]) == (long)initialMeasure["textUtf8Bytes"] &&
                (int)initialMeasure["inputFields"]["currentUserText"]["utf8Bytes"] > 0 &&
                !initialMeasure.ToString().Contains("Come here"), "Per-message and per-field diagnostics account for exact byte totals without logging authority text.");
            Require((long)Call(null, "EstimateRoomDecisionTextUnits", "a3b7c9d1e2f456789abcdef01234567890") >= 16 &&
                (long)Call(null, "EstimateRoomDecisionTextUnits", "123456789012345678901234567890") >= 10 &&
                (long)Call(null, "EstimateRoomDecisionTextUnits", "漢字かなカナ😀") >= 8 &&
                (long)Call(null, "EstimateRoomDecisionTextUnits", "{}[]():;\"\\{}[]():;\"\\") >= 10,
                "Opaque hex IDs, digit runs, punctuation-heavy code and Unicode receive explicit reserves, not an English-only word ratio.");
            var coreOnly = new JObject { ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = new string('密', 50000) }), ["max_tokens"] = 640 };
            var coreBlocked = (JObject)Call(null, "MeasureRoomDecisionBudget", coreOnly.ToString(Formatting.None), 49152);
            Require((bool)coreBlocked["totalWithinBudget"] && !(bool)coreBlocked["textWithinBudget"] && !(bool)coreBlocked["allowed"],
                "The wider total allowance cannot bypass the independent hard core-text ceiling.");
            var multiParts = new JArray(new JObject { ["type"] = "text", ["text"] = new string('密', 40000) });
            for (int i = 0; i < 5; ++i) multiParts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64,FIXTURE" + i } });
            coreOnly["messages"][0]["content"] = multiParts;
            var totalBlocked = (JObject)Call(null, "MeasureRoomDecisionBudget", coreOnly.ToString(Formatting.None), 49152);
            Require((bool)totalBlocked["textWithinBudget"] && !(bool)totalBlocked["totalWithinBudget"] && !(bool)totalBlocked["allowed"] &&
                (int)totalBlocked["textUnitBudget"] == 49152 && (int)totalBlocked["totalUnitBudget"] == 61440,
                "A text-admissible request still fails when five preserved images and output/framing reserves exceed the independent total ceiling.");
            CheckFreshProducerShape(provider);
            CheckRealProducerProjection(provider);
            CheckWorkingMemory(provider);
            LastReport["raw"] = rawDiagnostic; LastReport["budget"] = result["diagnostic"].DeepClone();
            LastReport["status"] = "passed";
            return checks;
        }
        catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
        finally { LastReport["checks"] = checks; UnityEngine.Object.DestroyImmediate(host); }
    }

    public static void ExportFixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        var host = new GameObject("InactiveRoomBudgetExport"); host.SetActive(false);
        try
        {
            var provider = host.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.Local;
            string wire = Wire(provider, BuildLargeInput().ToString(Formatting.None));
            var result = Budget(wire, false);
            File.WriteAllText(Path.Combine(directory, "large-decision-budget.json"), result["diagnostic"].ToString());
            if (!(bool)result["ok"]) throw new InvalidOperationException("Synthetic large decision was not admitted; do not call a backend.");
            File.WriteAllText(Path.Combine(directory, "large-decision-request.json"), (string)result["wire"]);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    // Called by the scene Play runner with its ACTUAL producer input and paired
    // fresh image. This writes reviewable evidence only; no endpoint or executor.
    public static JObject AuditProducerInput(ChatQW provider, string input, string imageDataUrl, string directory)
    {
        Directory.CreateDirectory(directory);
        string wire = (string)Call(provider, "BuildRoomTaskRequestJsonWithImage", input, imageDataUrl, false);
        var result = Budget(wire, false);
        File.WriteAllText(Path.Combine(directory, "raw.request.json"), wire);
        File.WriteAllText(Path.Combine(directory, "budget.json"), result["diagnostic"].ToString());
        if ((bool)result["ok"]) File.WriteAllText(Path.Combine(directory, "admitted.request.json"), (string)result["wire"]);
        else
        {
            // Explicit audit artifact, NOT an admitted production request. Keep
            // the rejected candidate available for controlled tokenizer/model
            // calibration without weakening runtime admission.
            var value = (JObject)Call(null, "ReadRoomTaskRecoveryJson", wire);
            object[] locate = { value, null, null }; Call(null, "TryFindRoomTaskInput", locate);
            var source = (JObject)locate[2];
            if ((bool)Call(null, "RestoreRoomDecisionSharedEvidence", source))
            {
                string original = (string)(JToken)locate[1];
                var projected = (JObject)Call(null, "ProjectRoomDecisionInput", source, 0);
                ((JToken)locate[1]).Replace(original.Substring(0, original.IndexOf('{')) + projected.ToString(Formatting.None));
                string candidate = value.ToString(Formatting.None);
                object[] shared = { candidate, null };
                if ((bool)Call(null, "TryCompactRoomTaskRequest", shared)) candidate = (string)shared[1];
                File.WriteAllText(Path.Combine(directory, "projected-calibration-only.request.json"), candidate);
            }
        }
        return new JObject { ["admitted"] = result["ok"].DeepClone(), ["budget"] = result["diagnostic"].DeepClone(),
            ["scope"] = "Caller-supplied complete production input plus paired image, through the exact production serializer/admission. Export only; no model request or action execution." };
    }

    // Call after the scene runner has established this user's turn and acquired
    // its real observation. This invokes the complete ChatSample producer; no
    // handcrafted replacement for current facts/catalog/metadata is involved.
    public static JObject AuditScene(ChatSample sample, ArdyRoomDialogueBridge bridge,
        NeEEvA.Perception.SceneVisionObservation observation, string currentUserText, string directory, ChatQW provider = null)
    {
        if (sample == null || bridge == null || observation == null || !observation.HasImage)
            throw new ArgumentException("Budget scene audit requires actual sample, bridge and paired image observation.");
        GameObject serializerHost = null;
        try
        {
            if (provider == null)
            {
                serializerHost = new GameObject("InactiveRoomBudgetSceneSerializer"); serializerHost.SetActive(false);
                provider = serializerHost.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.Local;
            }
            string input = (string)typeof(ChatSample).GetMethod("BuildRoomDecisionInputWithObservation", Flags)
                .Invoke(sample, new object[] { currentUserText, false, "", bridge, observation });
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "producer-input.json"), input);
            var result = AuditProducerInput(provider, input, observation.ImageDataUrl, directory);
            result["producer"] = "ChatSample.BuildRoomDecisionInputWithObservation";
            result["serializerSource"] = serializerHost == null ? "supplied-ChatQW" : "inactive-local-ChatQW-no-start-no-http";
            result["currentCandidateCount"] = ((JArray)Input(File.ReadAllText(Path.Combine(directory, "raw.request.json")))
                .SelectToken("visibleObjectCatalog.current.candidates"))?.Count ?? 0;
            return result;
        }
        finally { if (serializerHost != null) UnityEngine.Object.DestroyImmediate(serializerHost); }
    }

    private static void CheckFreshProducerShape(ChatQW provider)
    {
        var fresh = BuildLargeInput();
        fresh["visibleObjectCatalog"]["memory"] = new JArray(); fresh["sceneSpatialMemory"]["objects"] = new JArray();
        fresh["sceneSpatialMemory"]["dialogueViews"] = new JArray();
        fresh["spatialGoal"] = new JObject { ["status"] = "none", ["ownerCurrent"] = false };
        fresh["pendingObservationGoal"] = null; fresh["objectTargetReview"] = null; fresh["referentSources"] = new JArray();
        fresh["decisionContractFacts"]["allowedObservationGoalIds"] = new JArray();
        fresh["sceneActionFacts"]["objectContext"]["selected"] = null;
        fresh["sceneActionFacts"]["objectContext"]["recentObservations"] = new JArray();
        fresh["sceneActionFacts"]["objectContext"]["knownObjectHistory"] = new JArray();
        var current = (JArray)fresh["visibleObjectCatalog"]["current"]["candidates"];
        var cognitionRows = new JArray();
        for (int index = 0; index < 8; ++index)
        {
            var candidate = index < 6 ? (JObject)current[index] : (JObject)current[index - 6].DeepClone();
            candidate.Remove("modelDescription"); candidate.Remove("actionAuthorization");
            candidate["authoredHint"] = new JObject { ["source"] = "unity-authoring-metadata-of-visible-renderer", ["sourceCaptureId"] = "capture-current",
                ["rendererName"] = "SyntheticRenderer" + index, ["entityRootName"] = "SyntheticRoot" + index, ["sceneLabel"] = "SyntheticLabel" + index,
                ["identitySource"] = "observed-renderer", ["semanticIdentityConfirmed"] = false, ["actionAuthorization"] = false };
            candidate["directionFromCurrentPose"]["actionAuthorization"] = false;
            if (index >= 6) candidate["entityId"] = "other-page-" + index;
            cognitionRows.Add(new JObject { ["entityId"] = candidate["entityId"].DeepClone(), ["sourceCaptureId"] = "capture-current",
                ["authoredHint"] = new JObject { ["source"] = candidate["authoredHint"]["source"].DeepClone(),
                    ["rendererName"] = candidate["authoredHint"]["rendererName"].DeepClone(), ["entityRootName"] = candidate["authoredHint"]["entityRootName"].DeepClone(),
                    ["sceneLabel"] = candidate["authoredHint"]["sceneLabel"].DeepClone() },
                ["pointAtCapture"] = candidate["pointAtCapture"].DeepClone(), ["imagePoint"] = candidate["imagePoint"].DeepClone(),
                ["bearing"] = new JObject { ["cameraYawDegreesRight"] = candidate["directionFromCurrentPose"]["relativeToCurrentView"]["yawDegreesRight"].DeepClone(),
                    ["bodyYawDegreesRight"] = candidate["directionFromCurrentPose"]["relativeToCurrentBody"]["yawDegrees"].DeepClone() },
                ["modelDescription"] = null, ["descriptionCaptureId"] = null, ["matchedOriginalTarget"] = null, ["targetMatchCaptureId"] = null,
                ["currentOpaquePixels"] = true, ["actionAuthorization"] = false });
        }
        fresh["visualEvidence"] = "[synthetic current observation]" + "\n[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]\n" +
            new JObject { ["currentCaptureId"] = "capture-current", ["currentObjects"] = cognitionRows, ["historicalObjects"] = new JArray(),
                ["selfPose"] = fresh["sceneSpatialMemory"]["selfPose"].DeepClone(), ["scope"] = "Synthetic producer-shape regression; no live capture or permission." }.ToString(Formatting.None);
        var projected = (JObject)Call(null, "ProjectRoomDecisionInput", fresh, 0);
        Require(((JArray)projected["sceneCognitionSummary"]["currentObjects"]).Count == 2 &&
            ((JArray)projected["visibleObjectCatalog"]["current"]["candidates"]).Count == 6,
            "Real producer authority shape deduplicates six equivalent current rows while retaining both other-page rows.");
        var wire = (string)Call(provider, "BuildRoomTaskRequestJsonWithImage", fresh.ToString(Formatting.None), "data:image/png;base64,SYNTHETIC_CURRENT", false);
        var measured = Budget(wire, false);
        Require((bool)measured["ok"] && (int)measured["diagnostic"]["final"]["imageCount"] == 1,
            "Empty historical collections plus six current/eight cognition rows and a full proposal schema admit a one-image producer-shape request.");
        LastReport["freshProducerShape"] = measured["diagnostic"].DeepClone();
    }

    internal static JObject BuildLargeInput()
    {
        var pose = new JObject { ["captureId"] = "capture-current", ["capturedAtUtc"] = "2026-10-09T10:35:40.0000000Z",
            ["cameraPosition"] = Vector(1, 2, 3), ["bodyPosition"] = Vector(1, 0, 3), ["cameraRotation"] = new JObject { ["x"] = 0, ["y"] = .2, ["z"] = 0, ["w"] = .98 },
            ["bodyForward"] = Vector(0, 0, 1), ["cameraYawRelativeToBodyDegrees"] = 23,
            ["sinceHistoricalCapture"] = new JObject { ["captureId"] = "capture-history", ["bodyYawChangeDegreesRight"] = -123.4, ["bodyTranslationMeters"] = 2.1 },
            ["scope"] = "Measured pose; positive yaw right, historical coordinates are frozen and not navigation permission. " + new string('p', 500) };
        var memory = new JArray();
        for (int index = 0; index < 60; ++index)
        {
            var row = new JObject { ["entityId"] = "remembered-" + index, ["referenceId"] = "capture-history:memory:" + index,
                ["sourceCaptureId"] = "capture-history", ["capturedAtUtc"] = "2026-10-09T10:34:20.0000000Z",
                ["pointAtCapture"] = Vector(index, 1, -2), ["imagePointAtCapture"] = new JObject { ["x"] = .4, ["y"] = .6 },
                ["authoredHint"] = new JObject { ["source"] = "unity-authoring-metadata", ["entityRootName"] = "item " + index, ["actionAuthorization"] = false },
                ["modelDescription"] = "Observed object " + index, ["descriptionCaptureId"] = "capture-history", ["semanticStatus"] = "model-described-target-unconfirmed",
                ["historical"] = true, ["currentVisibility"] = "not-established", ["positionMemoryValid"] = true, ["actionAuthorization"] = false,
                ["relativeToCurrentView"] = new JObject { ["relativeToCurrentView"] = new JObject { ["viewCaptureId"] = "capture-current", ["yawDegreesRight"] = -122.8 },
                    ["relativeToCurrentBody"] = new JObject { ["viewCaptureId"] = "capture-current", ["yawDegrees"] = -123.4 }, ["selfPose"] = pose.DeepClone() },
                ["scope"] = "Old opaque pixels only. No current visibility, state or action permission. " + new string('s', 1800),
                ["localHierarchy"] = new JObject { ["source"] = "authoring-hierarchy", ["partToEntityRoot"] = new JArray(new string('x', 1000)) } };
            if (index == 57) row["futureAuthorizationConstraint"] = new JObject { ["mustNotOperate"] = true };
            if (index == 56) row["authoredHint"]["futurePolicy"] = new JObject { ["mustConfirm"] = true };
            if (index == 55) row["localHierarchy"]["futureConstraint"] = new JObject { ["notAnActionTarget"] = true };
            memory.Add(row);
        }
        var current = new JArray();
        for (int index = 0; index < 6; ++index) current.Add(new JObject { ["entityId"] = "current-" + index, ["referenceId"] = "catalog-current:p2:c" + index,
            ["candidateId"] = "c" + index, ["sourceCaptureId"] = "capture-current", ["imagePoint"] = new JObject { ["x"] = .1 * index, ["y"] = .4 },
            ["pointAtCapture"] = Vector(index, 2, 3), ["modelDescription"] = "Current object " + index,
            ["directionFromCurrentPose"] = new JObject { ["relativeToCurrentView"] = new JObject { ["yawDegreesRight"] = -40 + index },
                ["relativeToCurrentBody"] = new JObject { ["yawDegrees"] = -63 + index }, ["selfPose"] = pose.DeepClone() } });
        return new JObject {
            ["currentUserText"] = "来我身边。\nCome here, please. 😀", ["latestUserText"] = "来我身边。\nCome here, please. 😀", ["autonomous"] = false,
            ["currentFacts"] = new JObject { ["userAnchorValid"] = true, ["userTrackingValid"] = true, ["userSupportVerified"] = true,
                ["bodyReserved"] = false, ["currentlyNearAndFacingUser"] = false, ["actualUserDistance"] = 3.2, ["actualFacingErrorDegrees"] = 80,
                ["actualRoot"] = Vector(0, 0, 0), ["userHeadWorld"] = Vector(3.2, 1.6, 0), ["assessment"] = new JObject { ["canPlanRoute"] = true, ["executionGuaranteed"] = false } },
            ["currentSupport"] = new JObject { ["found"] = true, ["verified"] = true },
            ["visualObservation"] = new JObject { ["captureId"] = "capture-current", ["hasImage"] = false, ["state"] = "no-image-fixture" },
            ["visibleObjectCatalog"] = new JObject { ["current"] = new JObject { ["catalogId"] = "catalog-current", ["captureId"] = "capture-current",
                ["pageIndex"] = 2, ["pageCount"] = 4, ["candidates"] = current }, ["memory"] = memory },
            ["sceneSpatialMemory"] = new JObject { ["selfPose"] = pose, ["objects"] = memory.DeepClone(), ["dialogueViews"] = new JArray(new JObject {
                ["captureId"] = "capture-history", ["imageRole"] = "historical-dialogue-view", ["actionAuthorization"] = false,
                ["visualObservation"] = new JObject { ["captureId"] = "capture-history", ["hasImage"] = false }, ["returnDirection"] = new JObject { ["yawRelativeToCurrentBodyDegrees"] = -123.4 } }) },
            ["sceneActionFacts"] = new JObject { ["objectContext"] = new JObject { ["selected"] = new JObject { ["entityId"] = "remembered-59" },
                ["runtimeDeviceControl"] = new JObject { ["requiresIndependentUserAuthorization"] = true } } },
            ["spatialGoal"] = new JObject { ["status"] = "satisfied", ["goalId"] = "old-device-goal", ["ownerCurrent"] = true,
                ["originalRequest"] = "关闭之前找到的那盏灯。", ["desiredState"] = "off", ["boundEntityId"] = "remembered-59",
                ["completionEvidenceValid"] = true, ["completionEvidence"] = new JObject { ["entityId"] = "remembered-59", ["actual"] = new JObject { ["lightEnabled"] = false } },
                ["searchProgress"] = new JObject { ["completedSteps"] = new JArray(new string('t', 12000)) } },
            ["pendingObservationGoal"] = new JObject { ["goalId"] = "pending-look", ["entityId"] = "remembered-58", ["actionAuthorization"] = false },
            ["objectTargetReview"] = new JObject { ["entityId"] = "remembered-59", ["semanticTargetConfirmed"] = false },
            ["referentSources"] = new JArray(new JObject { ["sourceId"] = "past-device", ["entityId"] = "remembered-59", ["actionAuthorization"] = false }),
            ["decisionContractFacts"] = new JObject { ["currentUserTurn"] = 8, ["allowedObservationGoalIds"] = new JArray("pending-look"), ["deviceGoalAlreadyReviewed"] = false },
            ["turnSweep"] = new JObject { ["frames"] = new JArray(), ["active"] = false },
            ["unknownSafetyConstraint"] = new JObject { ["noPhysicalTouch"] = true, ["provenance"] = "future-engine-field" }
        };
    }

    private static JObject Vector(double x, double y, double z) => new JObject { ["x"] = x, ["y"] = y, ["z"] = z };
    private static string Wire(ChatQW provider, string input) => (string)Call(provider, "BuildRoomTaskRequestJsonWithImage", input, null, false);
    private static JObject Input(string json)
    {
        object[] args = { JObject.Parse(json), null, null };
        if (!(bool)Call(null, "TryFindRoomTaskInput", args)) throw new InvalidOperationException("Missing fixture input.");
        return (JObject)args[2];
    }
    private static JObject Budget(string json, bool recovery)
    {
        object[] args = { json, recovery, null, null };
        bool ok = (bool)Call(null, "TryBudgetRoomDecisionRequest", args);
        return new JObject { ["ok"] = ok, ["wire"] = (string)args[2], ["diagnostic"] = (JObject)args[3] };
    }
    private static object Call(object instance, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(instance, args);
    private static void Require(bool condition, string reason) { ++checks; if (!condition) throw new InvalidOperationException(reason); }
}
