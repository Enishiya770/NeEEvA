using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Observe the actual humanoid after Animator, the player and VRM LateUpdate.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(12000)]
    public sealed class ArdyConstraintObserver : MonoBehaviour
    {
        private ArdyMotionPlayer player;
        private ArdyLiveMotionController controller;
        public void Bind(ArdyMotionPlayer value)
        {
            player = value;
            controller = GetComponent<ArdyLiveMotionController>();
            enabled = true;
        }
        private void LateUpdate()
        {
            if (player == null || player.BoundBoneCount == 0) return;
            try
            {
                player.ObserveConstraintPose(Time.deltaTime);
                controller?.RecordConstraintObservation();
            }
            catch (Exception error)
            {
                if (controller != null) controller.Cancel("constraint-observation-error");
                else player.Stop();
                Debug.LogException(error, this);
                enabled = false;
            }
        }
    }
}
