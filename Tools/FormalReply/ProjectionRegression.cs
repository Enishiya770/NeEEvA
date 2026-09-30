using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Minimal host seam: the production projection method is compiled unchanged.
// Native Unity TextAsset loading and PlayMode delivery are not simulated here.
namespace UnityEngine
{
    public class SerializeField : Attribute { }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string text) { } }
    public class TextAsset { public string name, text; }
    public static class Debug { public static void Log(object message) { } }
    public static class Resources {
        public static string Root;
        public static T Load<T>(string key) where T : class {
            string path=Path.Combine(Root,"Assets/AIChatTookit/Resources/"+key+".txt");
            return File.Exists(path) ? new TextAsset { name=Path.GetFileNameWithoutExtension(path),text=File.ReadAllText(path) } as T : null;
        }
    }
}

public class RecordingCapabilityStub {
    public RecordingSingingDecisionContext RecordingDecisionContext;
    public virtual bool SupportsRecordingSingingDecision => false;
}
public partial class ChatQW : RecordingCapabilityStub
{
    private UnityEngine.TextAsset[] m_PromptFiles;
    private readonly List<SendData> m_DataList = new List<SendData>();
    private bool m_EnableSemanticRoleDecision, m_LogRequestStats;
    private sealed class SendData
    {
        public string role, content, imageDataUrl;
        public SendData(string role,string content){this.role=role;this.content=content;}
    }
    private sealed class PromptBudgetReport { public int systemTokens,estimatedTotal; }
    private string m_Prompt="";
    private string BuildCharacterPrompt()=>string.Join("\n\n",m_PromptFiles.Select(a=>a.text.Trim()));
    private string BuildSystemPrompt()=>FormalPromptProfile.RolePreferences(BuildCharacterPrompt())+"\n"+FormalPromptProfile.LegacyEnvelope+"\n"+PromptProtocolAssets.GeneralTools();
    private string BuildRoleDecisionPersona()=>m_PromptFiles.FirstOrDefault(a=>a.name=="persona")?.text ?? "";
    private string BuildRoleDecisionLanguageRules()=>FormalPromptProfile.LanguageRules;
    private string BuildRoleDecisionBehaviorRules()=>string.Join("\n",m_PromptFiles.Where(a=>a.name!="persona"&&a.name!="language").Select(a=>a.text));
    private static int EstimateTokens(string value)=>value.Length;
    private const string CurrentModelName="fixture";
    private const int k_SlotMainConversation=0;
    private static void AppendJsonString(System.Text.StringBuilder sb,string value)=>sb.Append(Newtonsoft.Json.JsonConvert.SerializeObject(value));
    private static void AppendMessage(System.Text.StringBuilder sb,SendData value)=>sb.Append(Newtonsoft.Json.JsonConvert.SerializeObject(new { role=value.role,content=value.content }));
    private static void AppendReasoningControls(System.Text.StringBuilder sb,bool value) { }
    private static void AppendSlot(System.Text.StringBuilder sb,int slot) { }


    public static int CheckProjection(string root)
    {
        int checks=0;
        void Check(bool pass,string label){checks++;if(!pass)throw new Exception(label);}
        UnityEngine.Resources.Root=root;
        var model=new ChatQW();
        model.m_PromptFiles=new[]{"persona","language","behavior"}.Select(name=>new UnityEngine.TextAsset{
            name=name,text=File.ReadAllText(Path.Combine(root,"Assets/AIChatTookit/Prompts/"+name+".txt"))}).ToArray();
        string original=model.BuildSystemPrompt();
        model.m_DataList.Add(new SendData("system",original));
        var memory=new SendData("system","真实记忆：此前的约定。<不要删除这段事实>");
        var user=new SendData("user","最终ASR原话：我说的是 <那一个>，不是另一个。") { imageDataUrl="data:image/png;base64,AAAA" };
        model.m_DataList.Add(memory);model.m_DataList.Add(user);
        string contract=SpeechText.OutputContract+"\n\n"+RoleOutputPublicBoundary.OutputContract;
        string facts="[最新执行事实] 到达失败；尚未播放；保留未完事项。\n\n";
        string context=facts+contract;
        var projected=model.ProjectFormalSource(FormalResponseStage.CommittedSpatialReply,ref context,out bool ir,out _,out _);
        Check(ir && projected[0].content.Length<original.Length,"committed stage uses compact IR");
        Check(context==facts+"\n"+RoleDecisionSpeechStream.Contract,"only exact appended output contract changes");
        Check(model.m_DataList[0].content==original,"stored source/history untouched");
        Check(projected[1].content.Contains("真实记忆：此前的约定") && ReferenceEquals(projected[2],user) && user.imageDataUrl.EndsWith("AAAA"),"facts, final user and image retained by identity");
        context=facts;
        projected=model.ProjectFormalSource(FormalResponseStage.FirstReplyRouting,ref context,out ir,out string routeProfile,out _);
        Check(!ir && routeProfile=="compact-first-reply-route" && projected[0].content.Length<original.Length &&
            !projected[0].content.Contains(FormalPromptProfile.LegacyEnvelope),"first reply projects reviewed rules without the competing legacy tool/speech envelope");
        Check(context==facts && projected[1].content.Contains("真实记忆：此前的约定") && ReferenceEquals(projected[2],user) &&
            model.m_DataList[0].content==original,"first-reply projection preserves current facts user image and stored history");
        context=facts+contract;
        projected=model.ProjectFormalSource(FormalResponseStage.General,ref context,out ir,out string generalProfile,out _);
        Check(!ir && generalProfile=="explicit-general-tools-v2" && context==facts+contract &&
            projected[0].content.Contains(FormalPromptProfile.LegacyEnvelope) && projected[0].content.Contains("<memory_add"),
            "general retains its public/action envelope and general tool constraints");
        // Singing's detailed syntax now has one authored home: the loaded skill,
        // not a second manual in the base system. General/handoff must retain it.
        string singing="[已加载技能: singing]\n"+File.ReadAllText(Path.Combine(root,"Assets/AIChatTookit/Prompts/Skills/singing.txt"))+"\n"+PromptProtocolAssets.SingingTools();
        Check(!projected[0].content.Contains("<song_remember") && singing.Contains("<song_remember") &&
            singing.Contains("<hum_back mode=\"follow\"") &&
            FormalResponseContextPolicy.SkillContext(FormalResponseStage.General,singing,"")==singing,
            "general gets singing/save/future-follow syntax from the unchanged loaded skill");
        var recording=new RecordingSingingDecisionContext {UserText="把刚才那一段唱出来。",Facts="尚未播放。"};
        recording.Materials.Add(new RecordingSingingMaterial {Key="material_1",ClipRef="clip:actual",Revision="2",PlaybackStatus="ready"});
        model.RecordingDecisionContext=recording;context=facts+contract;
        projected=model.ProjectFormalSource(FormalResponseStage.RecordingSingingDecision,ref context,out ir,out string recordingProfile,out _);
        Check(!ir && recordingProfile=="recording-singing-ir-v1" &&
            !projected[0].content.Contains(FormalPromptProfile.LegacyEnvelope) &&
            context==RecordingSingingDecisionCompiler.Prompt(recording),
            "recording projection replaces the legacy envelope with the full typed material contract");
        Check(!FormalResponseContextPolicy.SkillContext(FormalResponseStage.RecordingSingingDecision,singing,"").Contains("<song_remember"),
            "structured recording stage has no competing singing XML manual");
        const string handoff="{\"decision\":\"handoff\",\"materials\":[],\"range\":\"current\",\"intent\":\"perform\",\"expected\":\"需要当前表未提供的操作\",\"user_evidence\":\"\",\"thought\":\"核对完整工具。\",\"speech\":{\"text\":\"\",\"language\":\"zh\"}}";
        Check(RecordingSingingDecisionCompiler.TryCompile(handoff,recording,out string compiled,out _) &&
            compiled==RecordingSingingDecisionCompiler.Handoff,"valid unsupported-operation decision remains a handoff without action tags");
        model.RecordingDecisionContext=null; // Actual provider consumes this once before a handoff.
        context=facts+contract+RecordingSingingDecisionCompiler.HandoffFacts(handoff,recording);
        string handoffContext=context;
        projected=model.ProjectFormalSource(FormalResponseStage.General,ref context,out ir,out generalProfile,out _);
        Check(!ir && generalProfile=="explicit-general-tools-v2" && context==handoffContext &&
            projected[0].content.Contains(FormalPromptProfile.LegacyEnvelope) &&
            FormalResponseContextPolicy.SkillContext(FormalResponseStage.General,singing,"")==singing &&
            projected[1].content.Contains("真实记忆：此前的约定") && ReferenceEquals(projected[2],user),
            "legitimate handoff restores general tool authority without losing feedback, source history or loaded skill");
        model.m_PromptFiles[2].text+="\n用户后来新增的规则";
        model.m_DataList[0].content=model.BuildSystemPrompt();context=facts+contract;
        projected=model.ProjectFormalSource(FormalResponseStage.CommittedSpatialReply,ref context,out ir,out _,out _);
        Check(ir && projected[0].content.Contains("用户后来新增的规则") && !projected[0].content.Contains("<memory_add"),"changed role preferences keep the selected structured protocol");
        projected=model.ProjectFormalSource(FormalResponseStage.FirstReplyRouting,ref context,out ir,out _,out _);
        Check(!ir && projected[0].content.Contains("用户后来新增的规则") && !projected[0].content.Contains("<memory_add"),"custom first-reply preferences survive without an old-format fallback");
        return checks;
    }
}
