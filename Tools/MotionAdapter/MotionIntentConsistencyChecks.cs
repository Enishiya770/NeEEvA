using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;

// Public-fixture inspector. Runs production routing/parser/ledger/compiler without Unity or model loading.
// Input is stdin so an unexpected private model channel is never first persisted as a reply file.
public static class MotionIntentConsistencyChecks
{
    public static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var ledger = new ArdyActionConstraintLedger(); ledger.Reset();
            ledger.RecordUserTurn(args.Length == 1 ? File.ReadAllText(args[0]) : "Public fixture user turn.");
            var channels = RoleOutputChannels.Parse(Console.In.ReadToEnd());
            string executable = channels.ToExecutableText();
            string originalExecutable = executable;
            bool accepted = DialogueMotionProtocol.TryExtract(ref executable, 7, 11, out var intent, out string rejection);
            ArdyActionPlan effective = null;
            ArdyCompiledActionPlan compiled = null;
            string compileError = "";
            if (accepted && intent.ActionPlan != null)
                try
                {
                    var prepared = ledger.Prepare(intent.ActionPlan, ledger.CurrentUserTurn);
                    effective = prepared.effective;
                    compiled = ArdyActionPlanCompiler.Compile(effective);
                }
                catch (ArgumentException error) { compileError = error.Message; }
                catch (InvalidOperationException error) { compileError = error.Message; }
            var result = new Dictionary<string, object> {
                {"accepted", accepted}, {"name", accepted ? intent.Name : null},
                {"rejection", rejection}, {"compileError", compileError},
                {"intent", accepted ? (object)intent : null}, {"effective", effective}, {"compiled", compiled},
                {"publicSpeech", channels.Speech}, {"executableChannel", originalExecutable},
                {"privateCharactersDiscarded", channels.PrivateCharacters}, {"malformedTool", channels.HasMalformedTool},
                {"userTurn", ledger.CurrentUserTurn}, {"ledgerValidated", accepted && compileError.Length == 0},
                {"executionAdmissionTested", false}, {"motionSuccess", null}
            };
            Console.WriteLine(Json(result));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
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
        if (value is float || value is double || value is int || value is long)
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) parts.Add(Json(entry.Key.ToString()) + ":" + Json(entry.Value));
            return "{" + string.Join(",", parts) + "}";
        }
        if (value is IEnumerable sequence)
        {
            foreach (object item in sequence) parts.Add(Json(item));
            return "[" + string.Join(",", parts) + "]";
        }
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            parts.Add(Json(field.Name) + ":" + Json(field.GetValue(value)));
        return "{" + string.Join(",", parts) + "}";
    }
}
