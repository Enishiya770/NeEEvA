using System;
using NeEEvA.Motion;
using UniVRM10;
using UnityEngine;

/// <summary>Connects the completed role action channel to one avatar; speech keeps its existing pipeline.</summary>
[DisallowMultipleComponent]
public sealed class ArdyDialogueMotionBridge : MonoBehaviour
{
    [SerializeField] private ChatSample chat;
    [SerializeField] private Vrm10Instance target;
    public bool allowGeneratedMotion = true;
    public string serviceUrl = "http://127.0.0.1:8093";
    public Transform interactionTarget;

    private ChatSample subscribedChat;
    private ArdyLiveMotionController controller;
    public bool IsBound => subscribedChat != null && controller != null && controller.IsBound;
    public string Status => controller != null ? controller.Status : "尚未连接对话与角色";

    private void Start()
    {
        if (chat != null && target != null && !IsBound)
        {
            try { Bind(chat, target); }
            catch (Exception error) { Debug.LogException(error, this); }
        }
    }

    public void Bind(ChatSample conversation, Vrm10Instance avatar)
    {
        if (!Application.isPlaying) throw new InvalidOperationException("请在运行模式连接对话动作。");
        if (conversation == null || avatar == null) throw new ArgumentNullException("请选择对话组件和角色。");
        Disconnect("motion-bridge-rebound");
        chat = conversation;
        target = avatar;
        controller = avatar.GetComponent<ArdyLiveMotionController>();
        if (controller == null) controller = avatar.gameObject.AddComponent<ArdyLiveMotionController>();
        controller.allowGeneratedMotion = allowGeneratedMotion;
        controller.serviceUrl = serviceUrl;
        controller.interactionTarget = interactionTarget;
        controller.Bind(avatar);
        controller.MotionFeedback += OnMotionFeedback;
        subscribedChat = chat;
        subscribedChat.MotionIntentRequested += OnMotionIntent;
        subscribedChat.MotionCancelled += OnMotionCancelled;
        subscribedChat.MotionStateContextRequested += DescribeMotionState;
        subscribedChat.BodyInspectionContextRequested += InspectBody;
        subscribedChat.ConfigureMotionOutput(true, allowGeneratedMotion);
    }

    private void OnMotionIntent(DialogueMotionIntent intent)
    {
        if (!isActiveAndEnabled || !IsBound) return;
        controller.interactionTarget = interactionTarget;
        try
        {
            if (intent.Name == "compose") controller.RequestControlPlan(intent.ControlPlan, intent.ResponseGeneration,
                intent.ActionId, intent.ParentActionId, intent.RepairAttempt);
            else if (intent.Name == "replay") controller.RequestReplay(intent.ReplayReference, intent.ResponseGeneration, intent.ActionId);
            else controller.RequestMotion(intent.Name, intent.Description, intent.ResponseGeneration,
                intent.ActionId, intent.Goal, intent.ParentActionId, intent.RepairAttempt);
        }
        catch (Exception error)
        {
            controller.ReportRejectedMotion(intent.ActionId, intent.ParentActionId, intent.ResponseGeneration,
                intent.RepairAttempt, intent.Name, intent.Description, intent.Goal, error.Message);
            Debug.LogWarning("[Dialogue/Motion] execution rejected: " + error.Message, this);
        }
    }

    private void OnMotionFeedback(ArdyMotionExecutionFeedback fact)
    {
        if (isActiveAndEnabled && subscribedChat != null && controller != null)
            subscribedChat.NotifyMotionFeedback(fact);
    }

    private void OnMotionCancelled(int generation, string reason)
    {
        if (controller != null) controller.Cancel(reason);
    }

    private string DescribeMotionState() => controller != null ? controller.DescribeMotionContext() : "";
    private string InspectBody() => controller != null ? controller.InspectCapabilities() : "{}";

    public void Disconnect(string reason = "motion-bridge-disconnected")
    {
        if (subscribedChat != null)
        {
            subscribedChat.ConfigureMotionOutput(false, false);
            subscribedChat.MotionIntentRequested -= OnMotionIntent;
            subscribedChat.MotionCancelled -= OnMotionCancelled;
            subscribedChat.MotionStateContextRequested -= DescribeMotionState;
            subscribedChat.BodyInspectionContextRequested -= InspectBody;
            subscribedChat = null;
        }
        if (controller != null)
        {
            controller.MotionFeedback -= OnMotionFeedback;
            controller.Cancel(reason);
        }
        controller = null;
    }

    private void OnDisable() => Disconnect("motion-bridge-disabled");
    private void OnDestroy() => Disconnect("motion-bridge-destroyed");
}
