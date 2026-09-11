using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;

// Built outside Assets against the actual production parser/compiler. No Unity, model or network dependency.
public static class SemanticMotionProtocolChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (ArgumentException) { checks++; return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static DialogueMotionIntent Parse(string tag)
    {
        string executable = RoleOutputChannels.Parse(tag).ToExecutableText();
        Check(DialogueMotionProtocol.TryExtract(ref executable, 7, 11, out var intent, out var error), "Parser: " + error);
        Check(executable.Length == 0 || executable == "<silent/>", "Action residue reached speech");
        return intent;
    }
    private static void RejectTag(string tag)
    {
        string executable = RoleOutputChannels.Parse(tag).ToExecutableText();
        Check(!DialogueMotionProtocol.TryExtract(ref executable, 7, 11, out _, out var error) && !string.IsNullOrEmpty(error), "Expected raw rejection: " + tag);
    }
    private const string Wrist = "<motion name=\"plan\" purpose=\"greet\" mode=\"oscillate\" scope=\"new\" left=\"forward\" right=\"forward\" leftPalm=\"partner\" rightPalm=\"partner\" joint=\"wrists\" axis=\"auto\" tempo=\"gentle\" size=\"small\" repeat=\"twice\" end=\"hold\"/>";

    public static int Main(string[] args)
    {
        if ((args.Length == 2 || args.Length == 3) && args[0] == "--inspect")
        { Inspect(File.ReadAllText(args[1]), args.Length == 3 ? args[2] : null); return 0; }
        var intent = Parse(Wrist);
        var compiled = ArdyActionPlanCompiler.Compile(intent.ActionPlan);
        Check(intent.Name == "plan" && intent.ControlPlan == null && compiled.name == "compose", "Route distinction");
        var control = compiled.controlPlan;
        Check(control.axis == "palm-normal" && control.leftBendAuto && control.rightBendAuto, "Auto palm axis and elbow freedom");
        Check(control.timingPolicy == "adaptive-v1" && control.tempo == "gentle" && !control.timingDurationFixed, "Adaptive timing");
        Check(control.amplitude == 10 && control.cycles == 2 && control.end == "hold", "Semantic shape");
        foreach (string purpose in new[] { "greet", "farewell", "display", "explain", "agree", "disagree", "other" })
        {
            var variant = intent.ActionPlan.Copy(); variant.purpose = purpose;
            Check(Json(ArdyActionPlanCompiler.Compile(variant).controlPlan) == Json(control), "Purpose hardcoded an animation/pose");
        }
        foreach (string culture in new[] { "fr-FR", "ja-JP", "en-US" })
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var numeric = Parse("<motion name=\"plan\" mode=\"oscillate\" left=\"forward\" leftPalm=\"down\" joint=\"left-wrist\" leftBend=\"0\" amplitude=\"12.5\" cycles=\"2\" seconds=\"3.5\" userTurn=\"4\" evidence=\"Straight elbow; 12.5 degrees, 2 cycles, 3.5 seconds.\" lock=\"leftBend,amplitude,cycles,seconds\"/>").ActionPlan;
            var fixedControl = ArdyActionPlanCompiler.Compile(numeric).controlPlan;
            Check(!fixedControl.leftBendAuto && fixedControl.leftBend == 0 && fixedControl.amplitude == 12.5f && fixedControl.seconds == 3.5f && fixedControl.timingDurationFixed, "Explicit sourced values/culture");
        }
        var hold = Parse("<motion name=\"plan\" purpose=\"display\" mode=\"hold\" left=\"outward\" leftPalm=\"partner\" end=\"hold\"/>").ActionPlan;
        Check(ArdyActionPlanCompiler.Compile(hold).controlPlan.joint == "none", "Static display added motion");
        var head = Parse("<motion name=\"plan\" purpose=\"agree\" joint=\"head\" axis=\"right\" size=\"large\" repeat=\"once\"/>").ActionPlan;
        Check(ArdyActionPlanCompiler.Compile(head).controlPlan.amplitude == 10, "Head size mapping");
        head.axis = "auto"; Reject(() => ArdyActionPlanCompiler.Compile(head), "Cannot infer head axis from purpose");
        var ignoredBend = Parse("<motion name=\"plan\" scope=\"continue\" joint=\"head\" axis=\"right\" leftBend=\"0\" userTurn=\"2\" evidence=\"straight elbow\" lock=\"leftBend\"/>").ActionPlan;
        Reject(() => ArdyActionPlanCompiler.Compile(ignoredBend), "Uncontrolled arm cannot satisfy fixed elbow constraint");
        ignoredBend.left = "forward";
        Check(ArdyActionPlanCompiler.Compile(ignoredBend).controlPlan.leftBend == 0, "A inherited controlled arm can satisfy the fixed elbow value");
        var slowHead = Parse("<motion name=\"plan\" joint=\"head\" axis=\"right\" tempo=\"gentle\" repeat=\"thrice\"/>").ActionPlan;
        Reject(() => ArdyActionPlanCompiler.Compile(slowHead), "Do not silently accelerate a greater-than-six-second requested tempo");
        var fastFixedHead = Parse("<motion name=\"plan\" joint=\"head\" axis=\"right\" amplitude=\"12\" cycles=\"1\" seconds=\"1\" userTurn=\"2\" evidence=\"12 degrees, 1 cycle, 1 second\" lock=\"amplitude,cycles,seconds\"/>").ActionPlan;
        Reject(() => ArdyActionPlanCompiler.Compile(fastFixedHead), "Fixed timing must satisfy full-curve acceleration/velocity bounds before ledger commit");
        var missing = Parse("<motion name=\"plan\" scope=\"continue\" joint=\"wrists\"/>").ActionPlan;
        Reject(() => ArdyActionPlanCompiler.Compile(missing), "Unmerged wrist pose");
        missing.left = missing.right = "forward"; missing.leftPalm = missing.rightPalm = "down";
        missing.leftBend = "0"; missing.lockFields = "leftBend";
        Check(ArdyActionPlanCompiler.Compile(missing).controlPlan.leftBend == 0, "Inherited sourced number wrongly needs new user quote");
        Reject(() => missing.ValidateRaw(), "Raw numeric value cannot borrow unverified inherited status");
        missing.rightBend = "9"; Reject(() => ArdyActionPlanCompiler.Compile(missing), "Untracked numeric value");
        var copy = intent.ActionPlan.Copy(); copy.SetValue("LEFTPALM", "down");
        Check(intent.ActionPlan.leftPalm == "partner" && copy.GetValue("leftPalm") == "down" && !copy.HasField("seconds"), "Copy/presence");
        Check(Parse("<motion name=\"plan\" mode=\"free\" purpose=\"other\" text=\"Alternate broad arm arcs with a gentle torso sway.\"/>").ActionPlan.mode == "free", "Free parsed");
        var free = Parse("<motion name=\"plan\" mode=\"free\" text=\"Alternate broad arm arcs with a gentle torso sway.\"/>").ActionPlan;
        Check(ArdyActionPlanCompiler.Compile(free).name == "generate", "Free didn't retain ARDY route");
        foreach (string attributes in new[] {
            "purpose=\"dance\"", "left=\"backward\"", "axis=\"world\"", "tempo=\"fast\"", "size=\"tiny\"", "repeat=\"four\"",
            "leftBend=\"0\"", "seconds=\"3\"", "mode=\"hold\" cycles=\"0\"", "mode=\"hold\" tempo=\"gentle\"",
            "mode=\"free\" text=\"Raise both arms.\" left=\"up\"", "mode=\"free\" text=\"举起双手\"",
            "userTurn=\"0\" evidence=\"hello\"", "userTurn=\"1\"", "evidence=\"hello\"", "left=\"\"",
            "left=\"forward\" lock=\"left\"", "left=\"forward\" userTurn=\"1\" evidence=\"forward\" lock=\"left,left\"",
            "purpose=\"greet\" userTurn=\"1\" evidence=\"hello\" lock=\"purpose\"",
            "leftBendAuto=\"true\"", "left=\"current\" leftBend=\"0\" userTurn=\"1\" evidence=\"straight\" lock=\"leftBend\"",
            "amplitude=\"NaN\" userTurn=\"1\" evidence=\"NaN\" lock=\"amplitude\"",
            "cycles=\"2\" repeat=\"twice\" userTurn=\"1\" evidence=\"twice\" lock=\"cycles\"",
            "amplitude=\"10\" size=\"small\" userTurn=\"1\" evidence=\"10\" lock=\"amplitude\"",
            "seconds=\"3\" tempo=\"natural\" userTurn=\"1\" evidence=\"3\" lock=\"seconds\""
        }) RejectTag("<motion name=\"plan\" " + attributes + "/>");
        foreach (string basic in new[] { "left-wave", "right-wave", "nod", "shake-head", "none" }) Check(Parse("<motion name=\"" + basic + "\"/>").Name == basic, "Basic regression");
        Check(Parse("<motion name=\"generate\" text=\"Raise both arms above the head.\"/>").Name == "generate", "Legacy generate regression");
        Check(Parse("<motion name=\"compose\" right=\"forward\" rightPalm=\"partner\" joint=\"right-wrist\" axis=\"palm-normal\"/>").ControlPlan.timingPolicy == "legacy", "Legacy compose timing changed");
        CheckLedger();
        string raw = "明白了。" + Wrist;
        for (int split = 0; split <= raw.Length; split++)
        {
            var channels = new RoleOutputChannels(); channels.Push(raw.Substring(0, split)); channels.Push(raw.Substring(split)); channels.Finish();
            Check(channels.Speech == "明白了。", "Plan/evidence leaked to TTS at split " + split);
            string executable = channels.ToExecutableText();
            Check(DialogueMotionProtocol.TryExtract(ref executable, 3, split, out var parsed, out _) && parsed.ActionPlan != null, "Plan lost at split " + split);
        }
        foreach (string wrapper in new[] { "`{0}`", "<thought>{0}</thought>", "<say>{0}</say>" })
        {
            string executable = RoleOutputChannels.Parse(string.Format(wrapper, Wrist)).ToExecutableText();
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out _), "Quoted/private plan executed");
        }
        Console.WriteLine("{\"status\":\"passed\",\"checks\":" + checks + ",\"runtime\":\"real production C# on Mono, no Unity or model\"}");
        return 0;
    }

    private static void CheckLedger()
    {
        var ledger = new ArdyActionConstraintLedger();
        ledger.RecordUserTurn("Keep both arms forward with straight elbows and palms down.");
        var first = Parse("<motion name=\"plan\" mode=\"hold\" left=\"forward\" right=\"forward\" leftPalm=\"down\" rightPalm=\"down\" leftBend=\"0\" rightBend=\"0\" end=\"hold\" userTurn=\"1\" evidence=\"Keep both arms forward with straight elbows and palms down.\" lock=\"left,right,leftPalm,rightPalm,leftBend,rightBend\"/>").ActionPlan;
        var prepared = ledger.Prepare(first, 1); ArdyActionPlanCompiler.Compile(prepared.effective); ledger.Commit(prepared);
        ledger.RecordUserTurn("Keep that pose and move both wrists gently twice.");
        var next = Parse("<motion name=\"plan\" mode=\"oscillate\" scope=\"continue\" joint=\"wrists\" tempo=\"gentle\" repeat=\"twice\"/>").ActionPlan;
        prepared = ledger.Prepare(next, 2);
        var result = ArdyActionPlanCompiler.Compile(prepared.effective).controlPlan;
        Check(result.leftBend == 0 && !result.leftBendAuto && result.left == "forward" && result.rightPalm == "down", "Inherited user constraints lost");
        ledger.Commit(prepared);
        var forged = next.Copy(); forged.leftBend = "30"; forged.lockFields = "leftBend"; forged.userTurn = 2; forged.evidence = "I explicitly request 30 degrees.";
        Reject(() => ledger.Prepare(forged, 2), "Forged quote");
        Reject(() => ledger.Prepare(next, 1), "Stale request user-turn ID");
        ledger.RecordUserTurn("Release the straight-elbow requirement; keep the arms forward.");
        var released = Parse("<motion name=\"plan\" scope=\"continue\" joint=\"wrists\" userTurn=\"3\" evidence=\"Release the straight-elbow requirement; keep the arms forward.\" release=\"leftBend,rightBend\"/>").ActionPlan;
        prepared = ledger.Prepare(released, 3);
        Check(ArdyActionPlanCompiler.Compile(prepared.effective).controlPlan.leftBendAuto, "Released elbow remained fixed");
        ledger.Commit(prepared);
        ledger.RecordUserTurn("Change the left arm to outward.");
        var replaced = Parse("<motion name=\"plan\" scope=\"continue\" joint=\"wrists\" left=\"outward\" userTurn=\"4\" evidence=\"Change the left arm to outward.\" release=\"left\" lock=\"left\"/>").ActionPlan;
        prepared = ledger.Prepare(replaced, 4); ArdyActionPlanCompiler.Compile(prepared.effective);
        Check(prepared.effective.left == "outward", "Explicit release plus replacement lock rejected");
        ledger.Commit(prepared);
    }

    private static void Inspect(string raw, string contextDirectory)
    {
        ArdyActionConstraintLedger ledger = null;
        if (contextDirectory != null)
        {
            ledger = new ArdyActionConstraintLedger();
            ledger.Reset(); // Matches the first actual Chat ConfigureMotionOutput binding.
            var paths = Directory.GetFiles(contextDirectory, "*.user.txt");
            Array.Sort(paths, StringComparer.Ordinal);
            foreach (string path in paths)
            {
                ledger.RecordUserTurn(File.ReadAllText(path));
                string tagPath = path.Replace(".user.txt", ".tag.txt");
                if (!File.Exists(tagPath)) continue;
                var seeded = Parse(File.ReadAllText(tagPath)).ActionPlan;
                var prepared = ledger.Prepare(seeded, ledger.CurrentUserTurn);
                ArdyActionPlanCompiler.Compile(prepared.effective); ledger.Commit(prepared);
            }
        }
        string executable = RoleOutputChannels.Parse(raw).ToExecutableText();
        bool accepted = DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out var rejection);
        ArdyCompiledActionPlan compiled = null; ArdyActionPlan effective = null; string compileError = "";
        if (accepted && intent.ActionPlan != null)
            try
            {
                effective = ledger != null ? ledger.Prepare(intent.ActionPlan, ledger.CurrentUserTurn).effective : intent.ActionPlan;
                compiled = ArdyActionPlanCompiler.Compile(effective);
            }
            catch (ArgumentException error) { compileError = error.Message; }
        else if (accepted && ledger != null && ledger.HasConstraints && intent.Name != "none")
            compileError = "A legacy action cannot bypass existing user constraints.";
        Console.WriteLine("{\"accepted\":" + (accepted ? "true" : "false") + ",\"name\":" + Json(accepted ? intent.Name : null) +
            ",\"rejection\":" + Json(rejection) + ",\"actionPlan\":" + Json(intent.ActionPlan) + ",\"effective\":" + Json(effective) + ",\"compiled\":" + Json(compiled) +
            ",\"compileError\":" + Json(compileError) + ",\"ledgerValidated\":" +
            (ledger != null && accepted && compileError.Length == 0 ? "true" : "false") + ",\"motionSuccess\":null}");
    }

    private static string Json(object value)
    {
        if (value == null) return "null";
        if (value is string text)
        {
            var result = new StringBuilder("\"");
            foreach (char c in text)
                if (c == '"' || c == '\\') result.Append('\\').Append(c);
                else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4")); else result.Append(c);
            return result.Append('"').ToString();
        }
        if (value is bool flag) return flag ? "true" : "false";
        if (value is float || value is double || value is int) return Convert.ToString(value, CultureInfo.InvariantCulture);
        var parts = new List<string>();
        foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) parts.Add(Json(field.Name) + ":" + Json(field.GetValue(value)));
        return "{" + string.Join(",", parts) + "}";
    }
}
