using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatQW
{
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
    };

    private static bool TryFocusCurrentUserBodyPlanning(string input, out string focused)
    {
        focused=input;
        JObject value;
        try { value=JObject.Parse(input); } catch (JsonException) { return false; }
        if(!RoomTaskContextPolicy.InitialUserBodyAttention(value) || (bool?)value["answerRequired"]==true ||
            !string.IsNullOrWhiteSpace((string)value.SelectToken("currentRequestSemantics.currentTurnEvidence.answerQuote")) ||
            value["workContinuation"] is JObject || value["taskSequence"] is JObject) return false;
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
            "objectTargetReview","planningHistoryImageCaptureIds"})
        {
            if(value[field]==null)continue;
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
            ["engineMemoryMutated"]=false,["actionAuthorization"]=false,
            ["scope"]="The complete current utterance and current body/route/support/ownership are retained. This is an attention view, not permission. If the utterance actually needs a scene referent, independent answer, another task or missing evidence, do not infer it from omitted records; return intent=none. All action menu and execution checks remain unchanged." };
        focused=value.ToString(Formatting.None);
        Debug.Log("[Room/PlanningFactScope] "+new JObject { ["purpose"]="current-user-body",["sourceCharacters"]=input.Length,
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
        string[] containers={"current","memory","candidates","objects","currentObjects","historicalObjects","historicalViews",
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
