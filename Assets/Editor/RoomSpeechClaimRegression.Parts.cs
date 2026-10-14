using System;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechClaimRegression
{
    private static JObject Part(string owner, string text) => new JObject { ["owner"] = owner, ["text"] = text };

    private static void CheckSpeechParts()
    {
        const string speech = "小灯は点いていますよ。では、戻りますね。二足す二は四よ。";
        var review = new JObject { ["speechParts"] = new JArray(Part("independent", "小灯は点いていますよ。"),
            Part("current-body", "では、戻りますね。"), Part("independent", "二足す二は四よ。")) };
        Check(RoomSpeechPartsProtocol.TryRead(review, speech, out string kept, out _) &&
            kept == "小灯は点いていますよ。二足す二は四よ。", "Movement promises leave the prefix, independent results/answers survive exactly.");
        foreach (string defect in new[] { "missing", "reordered", "rewritten", "invented-owner", "extra-field" })
        {
            var bad = (JObject)review.DeepClone(); var parts = (JArray)bad["speechParts"];
            if (defect == "missing") parts.RemoveAt(2);
            if (defect == "reordered") { var first = parts[0]; parts.RemoveAt(0); parts.Add(first); }
            if (defect == "rewritten") parts[0]["text"] = "ランプは点いています。";
            if (defect == "invented-owner") parts[1]["owner"] = "new-action";
            if (defect == "extra-field") parts[1]["action"] = "follow";
            Check(!RoomSpeechPartsProtocol.TryRead(bad, speech, out _, out _), "Malformed partition cannot publish: " + defect);
        }
        var ordinary = Input("The lamp is off.", "off", "device", "en");
        Check(JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(ordinary.ToString()))["properties"]["bodyProgressParts"] == null,
            "Non-compound replies do not pay for partition output.");
        ordinary["speechProgressOwner"] = ProgressOwner();
        Check(JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(ordinary.ToString()))["properties"]["bodyProgressParts"] != null,
            "A current owned body step requires a complete partition even before independent claim extraction.");
        ordinary["publicSpeech"] = "The lamp is off. I'll come back. Two plus two is four.";
        ordinary["independentSpeechClaim"] = Claim("off", "The lamp is off.", "en");
        var fragments = RoomSpeechPartsProtocol.Fragments(ordinary);
        Check(string.Concat(fragments.Select(p => (string)p["text"])) == (string)ordinary["publicSpeech"],
            "Program-produced fragments cover every original character without model copying.");
        var selection = new JObject { ["bodyProgressParts"] = new JArray("p1") };
        Check(RoomSpeechPartsProtocol.Resolve(ordinary, selection, out _) &&
            RoomSpeechPartsProtocol.TryRead(selection, (string)ordinary["publicSpeech"], out kept, out _) &&
            kept == "The lamp is off. Two plus two is four.", "Only a selected owned fragment is removed; unrelated answer remains.");
        Check(!RoomSpeechPartsProtocol.Resolve(ordinary, new JObject { ["bodyProgressParts"] = new JArray("p0") }, out _),
            "Even a mistaken reviewer cannot select the independently extracted device result for removal.");
        Check(!RoomSpeechPartsProtocol.Resolve(ordinary, new JObject { ["bodyProgressParts"] = new JArray("p1", "p1") }, out _),
            "Duplicate selections cannot consume text twice.");
        Check(!RoomSpeechPartsProtocol.Resolve(ordinary, new JObject { ["bodyProgressParts"] = new JArray("p1"),
            ["verdict"] = "consistent", ["otherClaimsSupport"] = "none" }, out _),
            "A contradictory no-own-action judgment cannot silently remove an independent clause.");
    }

    private static JObject ProgressOwner(string intent = "approach") => new JObject {
        ["actionId"] = "return-owned", ["intent"] = intent, ["targetKind"] = "user", ["actor"] = "CHARACTER", ["target"] = "USER" };

    private static void CheckCapturedProgressReplies(ChatQW model)
    {
        var captured = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/SpeechParts.model-20261012.json"));
        foreach (JObject row in captured["cases"])
        {
            string name = (string)row["name"];
            var input = (JObject)row["input"];
            var review = (JObject)row["review"].DeepClone();
            Check(RoomTaskProtocol.ReviewAllows(review.ToString()) == (bool)row["approved"],
                "Actual captured model verdict preserves the expected publication decision: " + name);
            Check(RoomSpeechPartsProtocol.Resolve(input, review, out _) &&
                RoomSpeechPartsProtocol.TryRead(review, (string)input["publicSpeech"], out string stable, out _) &&
                stable == (string)row["expectedIndependent"], "Actual model selection passes production resolution without a word rewrite: " + name);
            foreach (string kind in new[] { "claim", "review" })
            {
                var actual = JObject.Parse((string)(kind == "claim"
                    ? Call(model, "BuildSpeechClaimRequestJson", input.ToString())
                    : Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(), null, true)));
                var expected = (JObject)row[kind + "Request"];
                actual["model"] = expected["model"].DeepClone();
                // This immutable capture predates the completed-event/live-pose
                // distinction. Reconsume its real verdict, but do not pretend it
                // was generated with today's instructions. Current contracts get
                // exported and replayed separately, including past-arrival cases.
                // Both the general factual contract and the target/relationship
                // contract now distinguish failure from role reversal. Only
                // normalize those historical instructions; native schema,
                // controls and every fact-bearing message still compare exactly.
                foreach (int contractMessage in kind == "claim" ? new[] { 0 } : new[] { 0, 1 })
                {
                    Check((string)actual["messages"][contractMessage]["role"]=="system" &&
                        (string)expected["messages"][contractMessage]["role"]=="system", "Historical contract slot remains a system instruction.");
                    actual["messages"][contractMessage]["content"] = expected["messages"][contractMessage]["content"].DeepClone();
                }
                if(kind=="claim") {
                    // This historical six-field schema predates independent
                    // event tuples. Check the additive migration explicitly;
                    // continue comparing every old field, fact and control.
                    var schema=(JObject)actual.SelectToken("response_format.schema");
                    Check(schema.SelectToken("properties.eventClaims")!=null && ((JArray)schema["required"]).Values<string>().Contains("eventClaims"),"Current claim schema requires event tuples.");
                    ((JObject)schema["properties"]).Remove("eventClaims");
                    ((JArray)schema["required"]).First(v=>(string)v=="eventClaims").Remove();
                    Check(schema.SelectToken("properties.languageReason.type")?.ToString()=="string" &&
                        (string)((JArray)schema["required"])[0]=="languageReason",
                        "Current extractor grounds language in grammar before classifying every domain.");
                    ((JObject)schema["properties"]).Remove("languageReason");
                    ((JArray)schema["required"]).First(v=>(string)v=="languageReason").Remove();
                }
                Check(JToken.DeepEquals(actual, expected), "Captured facts, schema and request controls remain unchanged (historical instructions retained): " + name + "/" + kind);
            }
        }
    }

    private static void AddProgressModelCases(ChatQW model, JArray rows)
    {
        void Add(string name, string speech, string language, string state, string[] retained, string[] replaced, bool approved = true)
        {
            var input = Input(speech, state, "device", language);
            input["requestedUserText"] = (state == "on" ? "打开刚才那盏灯" : "关掉那盏灯") + "，然后回到我身边。顺便告诉我二加二是多少。";
            input["sceneActionFacts"]["goal"]["originalRequest"] = input["requestedUserText"].DeepClone();
            input["speechProgressOwner"] = ProgressOwner();
            input["taskSequence"] = new JObject { ["request"] = input["requestedUserText"].DeepClone(), ["steps"] = new JArray(
                new JObject { ["id"] = "s1", ["kind"] = "device-state", ["desiredState"] = state, ["status"] = "completed", ["actionId"] = "owned" },
                new JObject { ["id"] = "s2", ["kind"] = "approach", ["quote"] = "然后回到我身边。", ["status"] = "running", ["actionId"] = "return-owned", ["after"] = new JArray("s1") }) };
            // The arithmetic question is included only for cases that actually answer it.
            if (!retained.Any(q => q.Contains("四") || q.Contains("four")))
                input["requestedUserText"] = (state == "on" ? "打开刚才那盏灯" : "关掉那盏灯") + "，然后回到我身边。";
            input["sceneActionFacts"]["goal"]["originalRequest"] = input["requestedUserText"].DeepClone();
            input["taskSequence"]["request"] = input["requestedUserText"].DeepClone();
            input["currentRoomFacts"] = new JObject { ["execution"] = new JObject {
                ["actionId"] = "return-owned", ["targetKind"] = "user", ["phase"] = "moving" } };
            input["independentSpeechClaim"] = Claim(state, retained[0], language);
            rows.Add(new JObject { ["name"] = "parts-" + name, ["input"] = input, ["expectedSpokenState"] = state,
                ["expectedLanguage"] = language, ["expectedAdmission"] = true, ["expectedFactualApproval"] = approved,
                ["retainedQuotes"] = new JArray(retained), ["replacedQuotes"] = new JArray(replaced),
                ["claimRequest"] = JObject.Parse((string)Call(model, "BuildSpeechClaimRequestJson", input.ToString())),
                ["reviewRequest"] = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(), null, true)) });
        }
        Add("latest-ja", "小灯は点いていますよ。では、戻りますね。", "ja", "on", new[] { "小灯は点いていますよ" }, new[] { "では、戻りますね" });
        Add("future-zh", "灯已经关了。我这就回到你身边。", "zh", "off", new[] { "灯已经关了" }, new[] { "我这就回到你身边" });
        Add("future-en", "The lamp is off. I'll come back to you now.", "en", "off", new[] { "The lamp is off" }, new[] { "I'll come back to you now" });
        Add("joined-zh", "灯已经关了，我正在往你身边走，二加二等于四。", "zh", "off", new[] { "灯已经关了", "二加二等于四" }, new[] { "我正在往你身边走" });
        Add("answer-ja", "ランプは消えたわ。二足す二は四よ。今あなたのところへ歩いているわ。", "ja", "off", new[] { "ランプは消えたわ", "二足す二は四よ" }, new[] { "今あなたのところへ歩いているわ" });
        Add("answer-en", "The lamp is off, and I'm coming back to you. Two plus two is four.", "en", "off", new[] { "The lamp is off", "Two plus two is four" }, new[] { "I'm coming back to you" });
        Add("courtesy", "小灯は点いていますよ。待ってくれてありがとう。", "ja", "on", new[] { "小灯は点いていますよ", "待ってくれてありがとう" }, new string[0]);
        Add("not-arrived-yet", "灯已经关了，我还没有到你身边。", "zh", "off", new[] { "灯已经关了" }, new[] { "我还没有到你身边" });
        Add("question", "The lamp is off. Would you like to chat?", "en", "off", new[] { "The lamp is off", "Would you like to chat?" }, new string[0]);
        Add("reversed-invitation", "灯已经关了。你到我这边来吧。", "zh", "off", new[] { "灯已经关了", "你到我这边来吧" }, new string[0], false);
    }
}
