using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Versioned stationary upper-body transport. No text is interpreted as executable code.</summary>
    public static class ArdyLiveProtocol
    {
        public const string FeatureContract = "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0";
        public const string ModelSha256 = "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4";
        public const int FramesPerChunk = 40, MaxChunks = 3, HistoryFrames = 16;
        public static readonly string[] JointNames = {
            "Hips", "Spine", "Spine1", "Spine2", "Spine3", "Neck", "Head",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandEnd", "RightHandThumb1",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandEnd", "LeftHandThumb1",
            "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase"
        };
        public static readonly int[] JointParents = { -1, 0, 1, 2, 3, 4, 5, 4, 7, 8, 9, 10, 10, 4, 13, 14, 15, 16, 16, 0, 19, 20, 21, 0, 23, 24, 25 };

        public static string SerializeRequest(ArdyGenerateRequest request)
        {
            // Unity's inline serializer materializes null nested classes as default objects.
            // A continuation must OMIT initialHistory instead of sending an empty history.
            string header = JsonUtility.ToJson(new GenerateHeader {
                characterId = request.characterId, turnId = request.turnId, requestId = request.requestId,
                description = request.description, revision = request.revision, chunkIndex = request.chunkIndex,
                seed = request.seed, mask = request.mask, timeoutMs = request.timeoutMs, maxChunks = request.maxChunks
            });
            if (request.initialHistory == null) return header;
            if (request.chunkIndex != 0) throw new ArgumentException("初始历史只能用于首窗");
            ValidateConditioningFrames(request.initialHistory.conditioningFrames);
            // Omit the default extension so a normal 16-frame client still speaks
            // the original strict server schema. Only an explicit experiment adds it.
            string history = JsonUtility.ToJson(new InitialHistoryHeader {
                kind = request.initialHistory.kind, fps = request.initialHistory.fps, frames = request.initialHistory.frames
            });
            if (request.initialHistory.conditioningFrames != HistoryFrames)
                history = history.Substring(0, history.Length - 1) + ",\"conditioningFrames\":"
                    + request.initialHistory.conditioningFrames + "}";
            return header.Substring(0, header.Length - 1) + ",\"initialHistory\":" + history + "}";
        }

        public static void ValidateConditioningFrames(int frames)
        {
            if (frames != 4 && frames != 8 && frames != HistoryFrames)
                throw new ArgumentException("初始历史条件长度只能为 4、8 或 16 帧");
        }

        [Serializable] private sealed class InitialHistoryHeader
        {
            public string kind;
            public float fps;
            public ArdyMotionFrame[] frames;
        }

        [Serializable] private sealed class GenerateHeader
        {
            public string characterId, turnId, requestId, description, mask;
            public long revision;
            public int chunkIndex, seed, timeoutMs, maxChunks;
        }

        public static void ValidateResponse(ArdyGenerateResponse response, ArdyGenerateRequest request)
        {
            if (response == null || response.characterId != request.characterId || response.turnId != request.turnId ||
                response.revision != request.revision || response.requestId != request.requestId || response.chunkIndex != request.chunkIndex)
                throw new ArgumentException("动作响应身份或版本不匹配");
            if (response.startFrame != request.chunkIndex * FramesPerChunk || response.newFrames != FramesPerChunk ||
                response.fps != 20f || response.final != (request.chunkIndex == MaxChunks - 1))
                throw new ArgumentException("动作窗口不连续或帧率错误");
            int expectedHistory = request.chunkIndex > 0 ? HistoryFrames : 0;
            if (request.chunkIndex == 0 && request.initialHistory != null)
            {
                ValidateConditioningFrames(request.initialHistory.conditioningFrames);
                expectedHistory = request.initialHistory.conditioningFrames;
            }
            if (response.historyFrames != expectedHistory)
                throw new ArgumentException("动作历史长度不匹配");
            if (response.provenance == null || response.provenance.mode != "dynamic-ardy-native" ||
                response.provenance.featureSource != (request.chunkIndex == 0 ? "live-qwen" : "live-condition-reused") ||
                response.provenance.featureContract != FeatureContract ||
                !string.Equals(response.provenance.modelSha256, ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("实时动作来源或 Qwen 特征契约不匹配");
            var clip = response.clip;
            if (clip == null) throw new ArgumentException("动作响应缺少旋转数据");
            clip.Validate();
            if (clip.frames.Length != FramesPerChunk || clip.fps != 20f || !string.IsNullOrEmpty(clip.source.rotationApplication))
                throw new ArgumentException("实时动作必须是 40 帧原生旋转窗口");
            for (int i = 0; i < JointNames.Length; i++)
                if (clip.jointNames[i] != JointNames[i] || clip.jointParents[i] != JointParents[i] ||
                    Quaternion.Angle(clip.restGlobalRotations[i], Quaternion.identity) > 0.1f)
                    throw new ArgumentException("实时动作的 Core27 骨架或参考姿态不匹配");
        }
    }

    [Serializable] public sealed class ArdyInitialHistory
    {
        public string kind = "unity-upper-body-projection-v1";
        public float fps = 20;
        public int conditioningFrames = ArdyLiveProtocol.HistoryFrames;
        public ArdyMotionFrame[] frames;
    }

    [Serializable] public sealed class ArdyGenerateRequest
    {
        public string characterId, turnId, requestId, description;
        public long revision;
        public int chunkIndex, seed;
        public string mask = "UpperBody";
        public int timeoutMs = 20000, maxChunks = ArdyLiveProtocol.MaxChunks;
        public ArdyInitialHistory initialHistory;
    }

    [Serializable] public sealed class ArdyGenerateResponse
    {
        public string characterId, turnId, requestId;
        public long revision;
        public int chunkIndex, startFrame, newFrames, historyFrames;
        public float fps;
        public bool final;
        public ArdyMotionClip clip;
        public ArdyMotionProvenance provenance;
        public ArdyMotionTimings timings;
    }

    [Serializable] public sealed class ArdyMotionProvenance
    {
        public string mode, featureSource, featureContract, modelSha256, historySource;
    }

    [Serializable] public sealed class ArdyMotionTimings
    {
        public float queueMs, featureMs, generationMs, totalMs;
    }

    [Serializable] public sealed class ArdyCancelRequest
    {
        public string characterId, turnId, requestId, reason;
        public long revision;
    }
}
