using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Speech-only Semantic IR. A complete, validated record is the unit of
/// delivery, not a partially decoded string. This compiler cannot produce tools.</summary>
public sealed class RoleDecisionSpeechStream
{
    public const string Contract =
        "[RoleDecision SpeechStream v1]\n本次空间决策已由程序提交，只根据最新事实表达，不再调用任何工具或安排动作。" +
        "只填写一个JSON对象，segments数组依次放完整语义记录；不要代码围栏、XML或解释。可直接从speech开始：\n" +
        "{\"segments\":[{\"kind\":\"speech\",\"language\":\"zh\",\"text\":\"本句公开台词。\"}]}\n" +
        "可选心理活动记录：{\"kind\":\"thought\",\"text\":\"此刻的心理活动\"}，这是不发声的speech。" +
        "每条speech只写一句完整台词，language仅zh/ja/en；先遵从本轮用户指定的语种，否则按语言规则。不要照抄示例台词。需要下一句就再写一条，语种可切换。" +
        "不说话可只写thought或令segments为空数组。写完后关闭数组及对象，不需要结束记录。不得增加字段或在text里写标签。" +
        "历史中的旧标签仅为过去记录，本次不要模仿。省略工具语法不等于失去能力、已完成或取消其他未完事项。";

    private readonly StringBuilder pending = new StringBuilder();
    private readonly StringBuilder compiled = new StringBuilder();
    private readonly Func<bool> isCurrent;
    private readonly Action<string, string> onRecord;
    private readonly bool envelope;
    private readonly StringBuilder header = new StringBuilder();
    // 0=header, 1=first record, 2=separator/array end, 3=record after comma,
    // 4=root close, 5=done. Never accept a second root or root properties.
    private int envelopeState;
    private bool quoted, escaped, started, phaseDeclared, ended, finished;
    private int depth, characters;
    public string Error { get; private set; }
    public int Records { get; private set; }
    public string Compiled => compiled.ToString() + (phaseDeclared ? "" : "<silent/>");

    public RoleDecisionSpeechStream(Action<string, string> onRecord, Func<bool> isCurrent = null, bool envelope = false)
    { this.onRecord = onRecord; this.isCurrent = isCurrent ?? (() => true); this.envelope = envelope; }

    // OpenAI-compatible JSON Schema. Local validation still owns acceptance;
    // the schema prevents ordinary prose/legacy tags before any record is parsed.
    public static JObject ResponseFormat() => JObject.Parse(@"{
      'type':'json_schema','json_schema':{'name':'role_decision_speech','strict':true,'schema':{
        'type':'object','additionalProperties':false,'required':['segments'],'properties':{
          'segments':{'type':'array','minItems':0,'maxItems':32,'items':{'anyOf':[
            {'type':'object','additionalProperties':false,'required':['kind','language','text'],'properties':{
              'kind':{'type':'string','enum':['speech']},'language':{'type':'string','enum':['zh','ja','en']},'text':{'type':'string'}}},
            {'type':'object','additionalProperties':false,'required':['kind','text'],'properties':{
              'kind':{'type':'string','enum':['thought']},'text':{'type':'string'}}}
          ]}}
        }
      }} }");

    public void Push(string delta)
    {
        if (finished || Error != null || !isCurrent()) return;
        foreach (char c in delta ?? "")
        {
            if (!isCurrent() || Error != null) return;
            if (++characters > DecisionCompiler.MaximumCharacters) { Reject("stream-too-long"); return; }
            if (envelope && !started && ConsumeEnvelope(c)) continue;
            if (Error != null) return;
            if (!started)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (ended) { Reject("content-after-end"); return; }
                if (c != '{') { Reject("expected-record"); return; }
                started = true; depth = 0; quoted = escaped = false;
            }
            pending.Append(c);
            if (pending.Length > 8192) { Reject("record-too-long"); return; }
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
            if (depth > 4 || depth < 0) { Reject("invalid-depth"); return; }
            if (depth == 0 && !quoted)
            {
                AcceptRecord(pending.ToString());
                pending.Clear(); started = false;
                if (envelope) envelopeState = 2;
            }
        }
    }

    public bool Finish(string finishReason)
    {
        if (!isCurrent()) { Reject("superseded"); return false; }
        finished = true;
        if (finishReason != "stop") Reject("transport-incomplete");
        else if (started || (envelope ? envelopeState != 5 : !ended)) Reject("missing-end");
        return Error == null;
    }

    private bool ConsumeEnvelope(char c)
    {
        if (envelopeState == 0)
        {
            header.Append(c);
            if (header.Length > 80) { Reject("invalid-envelope"); return true; }
            if (c == '[')
            {
                if (!Regex.IsMatch(header.ToString(), "^\\s*\\{\\s*\"segments\"\\s*:\\s*\\[$")) Reject("invalid-envelope");
                else envelopeState = 1;
            }
            return true;
        }
        if (char.IsWhiteSpace(c)) return true;
        if (envelopeState == 1 || envelopeState == 3)
        {
            if (c == '{') return false;
            if (c == ']' && envelopeState == 1) { envelopeState = 4; return true; }
            Reject("expected-record"); return true;
        }
        if (envelopeState == 2)
        {
            if (c == ',' && !ended) envelopeState = 3;
            else if (c == ']') envelopeState = 4;
            else Reject("invalid-record-separator");
            return true;
        }
        if (envelopeState == 4 && c == '}') envelopeState = 5;
        else Reject("content-after-end");
        return true;
    }

    private void AcceptRecord(string json)
    {
        try
        {
            JObject value;
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 4, DateParseHandling = DateParseHandling.None })
            {
                value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) { Reject("trailing-data"); return; }
            }
            if (++Records > 32 || value["kind"]?.Type != JTokenType.String) { Reject("invalid-record"); return; }
            string kind = (string)value["kind"], segment;
            if (kind == "end" && Fields(value, "kind")) { ended = true; return; }
            if (value["text"]?.Type != JTokenType.String) { Reject("invalid-text"); return; }
            string text = (string)value["text"];
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4000 || text.IndexOfAny(new[] {'<', '>', '`'}) >= 0)
            { Reject("invalid-text"); return; }
            if (kind == "thought" && Fields(value, "kind", "text"))
                segment = "<thought>" + text + "</thought>";
            else if (kind == "speech" && Fields(value, "kind", "language", "text") &&
                value["language"].Type == JTokenType.String &&
                SpeechText.NormalizeLanguage((string)value["language"]) == (string)value["language"])
            {
                segment = "<lang code=\"" + (string)value["language"] + "\"/>" +
                    (phaseDeclared ? "" : "<speech mode=\"independent\"/>") + "<say>" + text + "</say>";
            }
            else { Reject("unsupported-record"); return; }
            // Check the WHOLE candidate before callbacks; this also blocks private
            // producer envelopes disguised as public speech. Prior valid records
            // cannot be revoked, and are retained for the delivery receipt.
            var candidate = RoleOutputChannels.Parse(compiled.ToString() + segment,
                RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            if (candidate.HasInvalidPublicBoundary || candidate.HasMalformedTool || candidate.HasInvalidSpeechPhase ||
                candidate.HasInvalidLanguage || candidate.HasUndeclaredSpeech || candidate.HasActions)
            { Reject("invalid-compiled-boundary"); return; }
            if (!isCurrent()) return;
            compiled.Append(segment);
            if (kind == "speech") phaseDeclared = true;
            onRecord?.Invoke(segment, kind);
        }
        catch (JsonException) { Reject("invalid-json"); }
        catch (ArgumentException) { Reject("invalid-json"); }
    }

    private void Reject(string error) { if (Error == null) Error = error; }
    private static bool Fields(JObject value, params string[] names) =>
        value.Count == names.Length && names.All(name => value.Property(name) != null);
}
