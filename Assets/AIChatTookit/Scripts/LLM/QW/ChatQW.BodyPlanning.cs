using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatQW
{
    private const string BodyContinuationPlanningContract =
        "你是同一用户事项的当前身体步骤规划器，不发声、不直接执行，只输出给定schema JSON。" +
        "完整currentUserText仍是唯一用户授权；workContinuation只是下一步提案，taskSequence是引擎记录的历史完成和当前待办。" +
        "逐字核对完整原请求中的角色、目标、否定、条件、顺序、距离和未回答的问题。只选择依赖已完成的ready步骤，保留现有taskStep身份，不重建计划，不重做已完成的动作。" +
        "approach是角色走到当前用户身边，follow是角色持续跟随当前用户，stop-moving是停止角色自身移动。" +
        "若提案误解了原话、目标其实是物件/其他人、条件未满足或需要缺失的证据，返回none，不靠旧图、坐标或历史请求补造权限。" +
        "intent=none时origin=none、evidence为空，空间事项replyScope=spatial；否则origin=user，evidence逐字引用原请求中该步骤的授权短语。" +
        "历史事件只证明当时的结果，不证明当前世界未变。已完成前序保留行动者、对象、时间和结果；独立答案仍欠交付，不因身体步骤完成而消失。" +
        "场景记忆保留在引擎；当前仅投影本步骤所需事实，未加载资料不代表不存在。保留未知限制及currentFacts/currentSupport/execution/decisionContractFacts的约束。" +
        "每次提交仍需当前用户跟踪、路径、地面、姿态与身体归属验证；规划不证明动作已执行或完成。不要输出无关设备提案、像素或转向字段。";
    private const string BodyPlanningContract =
        "你是当前角色的空间意图规划步骤，不发声、不直接执行，只输出给定schema JSON。" +
        "本次只向你提供当前用户身体任务的注意力视图；场景记忆仍保留在引擎，但未加载的资料不能当作当前事实。" +
        "独立前置语义只是提示，不是行动许可：重新理解完整currentUserText，核对全部否定、条件、先后顺序、距离和对象。" +
        "approach表示一次走到当前用户身边；follow表示持续跟随当前用户；stop-moving表示停止角色自身的空间移动与跟随。" +
        "不要把历史停止、设备关闭、用户自己移动、引用、歌词、设想或否定的动作当作当前指令。" +
        "只有原话明确支持相应的当前身体动作时选它，origin=user，evidence逐字引用本轮支持该动作的短语（最多160字）。" +
        "停止请求可以在尚未移动时受理，表示确认保持停止，不虚构此前确实在走。" +
        "schema是完整动作菜单，但本次身体视图只足以判断approach/follow/stop-moving/none。" +
        "若完整原话实际包含另一个物件、独立问题、其他工具、复合先后任务或缺失条件，返回none、origin=none、evidence为空，交完整对话路径处理；不以本视图猜测或删掉请求的一部分。" +
        "none时空间问题的replyScope=spatial，非空间问题=ordinary；不要因有旧任务就恢复旧动作。" +
        "禁止额外设备提案、观察目标、像素、坐标或转向字段；这些不适用字段必须省略，不能写null。taskPlan/taskCoverage/taskStep等任务覆盖字段仍须遵从后附合同，不能因注意力聚焦而省略。" +
        "保留并尊重currentFacts/currentSupport/execution/decisionContractFacts中的控制权、可用性、路径、取消状态及原话的全部约束；没有所需事实就不推断。" +
        "recentDialogue、旧完成/失败记录仅解释历史，不能代替本轮授权或证明本轮完成/不可达。任务替换由引擎核验归属后交接。" +
        "规划不代表已开始、已到达或已经跟上；真实执行、净距、障碍、地面、姿态和最终结果仍由执行器审核。";

    private static readonly string[] BodyObservationMetadata = {
        "viewSamples", "availablePages", "catalogId", "focusImagePoint", "cropSourcePixels", "cropPanel", "fullPanel",
        "imageRole", "loadedSceneFingerprint", "scenePath", "sceneName", "sceneHandle", "frame", "state", "projection",
        "revisitedByTask", "reviewedByTask", "imageAttachedToCurrentLocalization", "truncated", "coverageComplete",
        "historicalOnly", "reviewed", "independentlyReviewedAtThisView", "viewportWidth", "viewportHeight",
        "pixelWidth", "pixelHeight", "fieldOfView", "orthographicSize", "nearClipPlane", "farClipPlane"
        , "currentVisibility", "requiresFreshSemanticMatch", "registeredPowerCapability", "measuredDistanceMeters",
        "deviceDistanceLimitMeters", "withinOperationDistance"
    };

    private static bool TryFocusCurrentUserBodyPlanning(string input, out string focused)
    {
        focused=input;
        JObject value;
        try { value=JObject.Parse(input); } catch (JsonException) { return false; }
        bool continuation = RoomTaskContextPolicy.ReadyUserIntents(value) != null;
        if(!continuation && (!RoomTaskContextPolicy.InitialUserBodyAttention(value) || (bool?)value["answerRequired"]==true ||
            !string.IsNullOrWhiteSpace((string)value.SelectToken("currentRequestSemantics.currentTurnEvidence.answerQuote")) ||
            value["workContinuation"] is JObject || value["taskSequence"] is JObject)) return false;
        // The independently read COMPLETE utterance has no scene referent or
        // separate question. Do not destroy memory; omit scene-only read models
        // from this one consumer. Keep all literal constraints and unknown fields.
        var omitted=new JObject();
        JObject Sizes(JToken before, JToken after) => new JObject {
            ["beforeCharacters"] = before?.ToString(Formatting.None).Length ?? 0,
            ["afterCharacters"] = after?.ToString(Formatting.None).Length ?? 0,
            ["removedCharacters"] = (before?.ToString(Formatting.None).Length ?? 0) - (after?.ToString(Formatting.None).Length ?? 0) };
        foreach(string field in new[]{"visualObservation","visualEvidence","visibleKnownObjects",
            "sceneSpatialMemory","visibleObjectCatalog","turnSweep",
            "sceneCognitionSummary","verifiedDeviceHistory","verifiedObservedDeviceHistory","referentSources",
            "objectTargetReview","planningHistoryImageCaptureIds","sceneActionPrerequisites"})
        {
            if(value[field]==null)continue;
            // Historical event/target facts may explain a compound prerequisite.
            // They are distinct from optional observed-world/camera detail.
            if(continuation && new[]{"verifiedDeviceHistory","verifiedObservedDeviceHistory","referentSources","objectTargetReview"}.Contains(field)) continue;
            var protectedView = BodyPlanningProtectedView(value[field]);
            omitted[field]=Sizes(value[field], protectedView);
            if (protectedView == null) value.Remove(field); else value[field] = protectedView;
        }
        // Runtime goals/task ledgers and their cancellation/ownership remain;
        // only duplicate observed-world detail inside working memory is omitted.
        if(value["workingMemory"] is JObject memory)
            foreach(string field in new[]{"recallIndex","selectedObjects","observations"})
                if(memory[field]!=null){
                    var protectedView=BodyPlanningProtectedView(memory[field]);
                    omitted["workingMemory."+field]=Sizes(memory[field], protectedView);
                    if(protectedView==null)memory.Remove(field);else memory[field]=protectedView;
                }
        if(value["sceneActionFacts"] is JObject scene && scene["objectContext"] is JObject observed) {
            var protectedView=BodyPlanningProtectedView(observed);
            omitted["sceneActionFacts.objectContext"]=Sizes(observed, protectedView);
            if(protectedView==null)scene.Remove("objectContext");else scene["objectContext"]=protectedView;
        }
        value["planningEvidenceScope"]=new JObject { ["purpose"]="current-user-body",["sceneDetailsOmitted"]=true,
            ["phase"] = continuation ? "approved-continuation" : "initial-request",
            ["engineMemoryMutated"]=false,["actionAuthorization"]=false,
            ["scope"]=continuation ? "Complete original request, owned sequence, predecessor events, pending answers and current body constraints are retained. Select only a ready current-user body step after independently checking its authorization. Missing necessary evidence means none; completion of this step cannot erase other obligations. No memory mutation or action permission." :
                "The complete current utterance and current body/route/support/ownership are retained. This is an attention view, not permission. If the utterance actually needs a scene referent, independent answer, another task or missing evidence, do not infer it from omitted records; return intent=none. All action menu and execution checks remain unchanged." };
        focused=value.ToString(Formatting.None);
        Debug.Log("[Room/PlanningFactScope] "+new JObject { ["purpose"]="current-user-body",["sourceCharacters"]=input.Length,
            ["phase"] = continuation ? "approved-continuation" : "initial-request",
            ["projectedCharacters"]=focused.Length,["fieldProjection"]=omitted,["imagesOmitted"]=true,["engineMemoryMutated"]=false }.ToString(Formatting.None));
        return true;
    }

    private static JToken BodyPlanningProtectedView(JToken token)
    {
        if(token is JArray rows) {
            var kept=new JArray(rows.Select(BodyPlanningProtectedView).Where(r=>r!=null));
            return kept.Count>0?kept:null;
        }
        if(!(token is JObject obj)) return token.Type==JTokenType.Null?null:token.DeepClone();
        var result=new JObject();
        string[] containers={"current","memory","candidates","objects","knownObjects","currentObjects","historicalObjects","historicalViews",
            "dialogueViews","recentObservations","knownObjectHistory","visualObservation","authoredNames","currentBearing",
            "relativeToCurrentView","relativeToCurrentBody","returnDirection","authoredHint","directionFromCurrentPose"};
        foreach(var p in obj.Properties()) {
            bool known=RoomDecisionKnownObservationFields.Contains(p.Name)||RoomDecisionKnownObservationChildFields.Contains(p.Name)||containers.Contains(p.Name)||BodyObservationMetadata.Contains(p.Name);
            // Unrecognized annotations and positive authority are never erased
            // by an attention classification. Known containers are inspected too.
            if(!known || p.Name=="actionAuthorization" && (bool?)p.Value==true) {result[p.Name]=p.Value.DeepClone();continue;}
            if(p.Value is JObject || p.Value is JArray) {
                var nested=BodyPlanningProtectedView(p.Value);
                if(nested!=null)result[p.Name]=nested;
            }
        }
        if(result.Count==0)return null;
        foreach(string identity in new[]{"entityId","sourceCaptureId","captureId","name"})
            if(obj[identity]!=null)result[identity]=obj[identity].DeepClone();
        return result;
    }
}
