using System;
using UnityEngine;

[Serializable]
public sealed class CP_HandPoseBinding
{
    public bool configured;
    public string poseId = string.Empty;
    public Vector3 localPosition;
    public Vector3 localEulerAngles;
    public float defaultBlendSeconds = 0.25f;
}

// Stage 5B-2 hand pose controller:
// owns only the visible right-hand pose offset at RightHandPosePivot.
// It never writes the animator-driven anchor, prop anchor, or Blackboard state.
public class CP_HandPoseController : MonoBehaviour
{
    [Header("References")]
    public Transform companionRoot;
    public Transform rightHandPosePivot;
    public RoastingStateBlackboard blackboard;
    public CP_ActivityContextRouter activityContextRouter;

    [Header("Optional Debug Reference")]
    public Transform bodyReference;

    [Header("Pose Bindings")]
    public CP_HandPoseBinding relaxedBinding = new CP_HandPoseBinding { poseId = "relaxed", defaultBlendSeconds = 0.25f };
    public CP_HandPoseBinding readyBinding = new CP_HandPoseBinding { poseId = "ready", defaultBlendSeconds = 0.25f };
    public CP_HandPoseBinding roastPoseBinding = new CP_HandPoseBinding { poseId = "roast_pose", defaultBlendSeconds = 0.25f };
    public CP_HandPoseBinding fishingReadyBinding = new CP_HandPoseBinding { poseId = "fishing_ready", defaultBlendSeconds = 0.25f };
    public CP_HandPoseBinding fishingHoldBinding = new CP_HandPoseBinding { poseId = "fishing_hold", defaultBlendSeconds = 0.25f };
    public CP_HandPoseBinding fishingCastBinding = new CP_HandPoseBinding { poseId = "fishing_cast", defaultBlendSeconds = 0.25f };
    public CP_HandPoseBinding fishingReelBinding = new CP_HandPoseBinding { poseId = "fishing_reel", defaultBlendSeconds = 0.25f };

    [Header("Validation")]
    public float minimumBlendSeconds = 0.1f;
    public float maximumBlendSeconds = 2.0f;
    public float completionPositionTolerance = 0.0025f;
    public float completionRotationToleranceDegrees = 1.0f;

    [Header("Runtime Debug")]
    public CP_PrimitiveExecutionStatus status = CP_PrimitiveExecutionStatus.Idle;
    public string currentPose = "neutral";
    public string targetPose = "neutral";
    public float blendProgress;
    public bool poseTargetResolved;
    public bool runtimeReady;
    public string lastFailureReason = string.Empty;
    public Vector3 currentHandWorldPosition;
    public float distanceFromCompanionRoot = -1f;
    public float heightOffsetFromBodyReference;

    private Vector3 neutralLocalPosition;
    private Quaternion neutralLocalRotation = Quaternion.identity;
    // Confirmed transform = the last fully completed pose that should be preserved while idle.
    private Vector3 confirmedLocalPosition;
    private Quaternion confirmedLocalRotation = Quaternion.identity;
    private Vector3 blendStartLocalPosition;
    private Quaternion blendStartLocalRotation = Quaternion.identity;
    // Active target transform = the currently requested pose target while a blend is running.
    private Vector3 activeTargetLocalPosition;
    private Quaternion activeTargetLocalRotation = Quaternion.identity;
    private float blendDurationSeconds = 0.25f;
    private float blendElapsedSeconds;
    private bool neutralCaptured;
    private bool pendingTerminalApply;
    private bool reportedRuntimeFailure;
    private bool internalPoseActive;
    private string internalPoseOwner = string.Empty;

    public string CurrentPose => currentPose;
    public string TargetPose => targetPose;
    public CP_PrimitiveExecutionStatus Status => status;
    public bool IsRunning => status == CP_PrimitiveExecutionStatus.Running;
    public bool IsRuntimeReady => runtimeReady;
    public string LastFailureReason => lastFailureReason;
    public bool IsInternalPoseActive => internalPoseActive;
    public string InternalPoseOwner => internalPoseOwner;

    private void Awake()
    {
        CacheNeutralBaselineIfPossible();
        RefreshRuntimeReadiness();
        if (neutralCaptured)
        {
            ForceNeutralRuntimeBaseline();
        }
    }

    private void Update()
    {
        RefreshRuntimeReadiness();
        RefreshDebugMeasurements();
        ApplyStateSafetyCleanupIfNeeded();
    }

    private void LateUpdate()
    {
        if (rightHandPosePivot == null || !neutralCaptured)
        {
            return;
        }

        if (status == CP_PrimitiveExecutionStatus.Running)
        {
            float safeDuration = Mathf.Max(minimumBlendSeconds, blendDurationSeconds);
            blendElapsedSeconds = Mathf.Min(safeDuration, blendElapsedSeconds + Time.deltaTime);
            blendProgress = safeDuration <= 0.0001f ? 1f : Mathf.Clamp01(blendElapsedSeconds / safeDuration);

            rightHandPosePivot.localPosition = Vector3.Lerp(blendStartLocalPosition, activeTargetLocalPosition, blendProgress);
            rightHandPosePivot.localRotation = Quaternion.Slerp(blendStartLocalRotation, activeTargetLocalRotation, blendProgress);

            if (blendProgress >= 1f)
            {
                rightHandPosePivot.localPosition = activeTargetLocalPosition;
                rightHandPosePivot.localRotation = activeTargetLocalRotation;
                pendingTerminalApply = true;
            }
        }
        else
        {
            ApplyConfirmedTransform();
        }

        if (pendingTerminalApply && IsAtTransform(activeTargetLocalPosition, activeTargetLocalRotation))
        {
            confirmedLocalPosition = activeTargetLocalPosition;
            confirmedLocalRotation = activeTargetLocalRotation;
            currentPose = string.IsNullOrWhiteSpace(targetPose) ? currentPose : NormalizePoseToken(targetPose);
            targetPose = currentPose;
            status = CP_PrimitiveExecutionStatus.Succeeded;
            pendingTerminalApply = false;
            blendProgress = 1f;
            blendElapsedSeconds = blendDurationSeconds;
            lastFailureReason = string.Empty;
        }

        currentHandWorldPosition = rightHandPosePivot.position;
    }

    private void OnDisable()
    {
        RestoreNeutralAndCancel("hand_pose_disabled");
    }

    public bool TryBegin(CP_MotionPrimitive primitive, CP_PrimitiveExecutionContext context, out string failureReason)
    {
        failureReason = string.Empty;
        RefreshRuntimeReadiness();

        if (!runtimeReady)
        {
            failureReason = string.IsNullOrWhiteSpace(lastFailureReason) ? "hand_pose_controller_unbound" : lastFailureReason;
            SetFailureState(failureReason);
            return false;
        }

        string requestedPose = NormalizePoseToken(primitive != null ? primitive.pose : string.Empty);
        if (string.IsNullOrWhiteSpace(requestedPose))
        {
            failureReason = "hand_pose_missing";
            SetFailureState(failureReason);
            return false;
        }

        if (!IsPoseAllowedInContext(requestedPose))
        {
            failureReason = "hand_pose_not_allowed_in_state";
            SetFailureState(failureReason);
            return false;
        }

        if (!TryResolvePoseBinding(requestedPose, out Vector3 targetLocalPosition, out Quaternion targetLocalRotation, out float defaultBlendSeconds, out failureReason))
        {
            SetFailureState(failureReason);
            return false;
        }

        BeginPoseBlend(requestedPose, targetLocalPosition, targetLocalRotation, ResolveBlendDurationSeconds(primitive != null ? primitive.duration : 0f, defaultBlendSeconds));
        internalPoseActive = false;
        internalPoseOwner = string.Empty;

        if (requestedPose == currentPose && IsConfirmedTransformApplied())
        {
            activeTargetLocalPosition = confirmedLocalPosition;
            activeTargetLocalRotation = confirmedLocalRotation;
            blendProgress = 1f;
            blendElapsedSeconds = blendDurationSeconds;
            status = CP_PrimitiveExecutionStatus.Succeeded;
            internalPoseActive = false;
            internalPoseOwner = string.Empty;
            return true;
        }

        status = CP_PrimitiveExecutionStatus.Running;

        if (IsAtTransform(activeTargetLocalPosition, activeTargetLocalRotation))
        {
            pendingTerminalApply = true;
            blendProgress = 1f;
        }

        return true;
    }

    public CP_PrimitiveExecutionStatus Tick(float deltaTime)
    {
        RefreshRuntimeReadiness();
        RefreshDebugMeasurements();

        if (status == CP_PrimitiveExecutionStatus.Running && !runtimeReady)
        {
            SetFailureState(string.IsNullOrWhiteSpace(lastFailureReason) ? "hand_pose_controller_unbound" : lastFailureReason);
        }

        return status;
    }

    public void Cancel(string reason)
    {
        RestoreConfirmedPoseVisual();
        activeTargetLocalPosition = confirmedLocalPosition;
        activeTargetLocalRotation = confirmedLocalRotation;
        blendStartLocalPosition = confirmedLocalPosition;
        blendStartLocalRotation = confirmedLocalRotation;
        blendElapsedSeconds = 0f;
        poseTargetResolved = neutralCaptured;
        targetPose = currentPose;
        pendingTerminalApply = false;
        blendProgress = 1f;
        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastFailureReason = string.IsNullOrWhiteSpace(reason) ? "hand_pose_cancelled" : reason;
    }

    public void AcknowledgeTerminalStatus()
    {
        if (status == CP_PrimitiveExecutionStatus.Succeeded ||
            status == CP_PrimitiveExecutionStatus.Failed ||
            status == CP_PrimitiveExecutionStatus.Cancelled)
        {
            status = CP_PrimitiveExecutionStatus.Idle;
        }
    }

    public bool TryBeginInternalPose(string poseToken, float requestedBlendSeconds, string ownerId, out string failureReason)
    {
        failureReason = string.Empty;
        RefreshRuntimeReadiness();

        if (!runtimeReady)
        {
            failureReason = string.IsNullOrWhiteSpace(lastFailureReason) ? "hand_pose_controller_unbound" : lastFailureReason;
            SetFailureState(failureReason);
            return false;
        }

        string requestedPose = NormalizePoseToken(poseToken);
        if (requestedPose != "fishing_cast" &&
            requestedPose != "fishing_hold" &&
            requestedPose != "fishing_reel" &&
            requestedPose != "fishing_ready")
        {
            failureReason = "hand_pose_internal_pose_unknown";
            SetFailureState(failureReason);
            return false;
        }

        if (!IsInternalPoseBindingReady(requestedPose, out failureReason))
        {
            SetFailureState(failureReason);
            return false;
        }

        if (!TryResolvePoseBinding(requestedPose, out Vector3 targetLocalPosition, out Quaternion targetLocalRotation, out float defaultBlendSeconds, out failureReason))
        {
            SetFailureState(failureReason);
            return false;
        }

        internalPoseActive = true;
        internalPoseOwner = string.IsNullOrWhiteSpace(ownerId) ? "internal_pose" : ownerId;
        BeginPoseBlend(requestedPose, targetLocalPosition, targetLocalRotation, ResolveBlendDurationSeconds(requestedBlendSeconds, defaultBlendSeconds));

        if (requestedPose == currentPose && IsConfirmedTransformApplied())
        {
            status = CP_PrimitiveExecutionStatus.Succeeded;
        }
        else
        {
            status = CP_PrimitiveExecutionStatus.Running;
        }

        return true;
    }

    public bool CompleteInternalPose(string ownerId, out string failureReason)
    {
        failureReason = string.Empty;
        if (!internalPoseActive)
        {
            failureReason = "hand_pose_internal_owner_not_active";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ownerId) && internalPoseOwner != ownerId)
        {
            failureReason = "hand_pose_internal_owner_mismatch";
            return false;
        }

        if (status == CP_PrimitiveExecutionStatus.Running || pendingTerminalApply)
        {
            failureReason = "hand_pose_internal_blend_still_running";
            return false;
        }

        internalPoseActive = false;
        internalPoseOwner = string.Empty;
        return true;
    }

    public bool IsInternalPoseComplete(string ownerId, string poseToken)
    {
        if (!internalPoseActive)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ownerId) && internalPoseOwner != ownerId)
        {
            return false;
        }

        string requestedPose = NormalizePoseToken(poseToken);
        return status == CP_PrimitiveExecutionStatus.Succeeded &&
               currentPose == requestedPose &&
               targetPose == requestedPose;
    }

    public void CancelInternalPose(string ownerId, string fallbackPose)
    {
        if (internalPoseActive && !string.IsNullOrWhiteSpace(ownerId) && internalPoseOwner != ownerId)
        {
            return;
        }

        string safeFallbackPose = ResolveSafeFallbackPose(fallbackPose);
        internalPoseActive = false;
        internalPoseOwner = string.Empty;

        if (!runtimeReady)
        {
            Cancel("hand_pose_internal_cancelled");
            return;
        }

        if (!TryResolvePoseBinding(safeFallbackPose, out Vector3 targetLocalPosition, out Quaternion targetLocalRotation, out float defaultBlendSeconds, out _))
        {
            Cancel("hand_pose_internal_cancelled");
            return;
        }

        BeginPoseBlend(safeFallbackPose, targetLocalPosition, targetLocalRotation, defaultBlendSeconds);
        status = CP_PrimitiveExecutionStatus.Running;
    }

    public bool IsInternalPoseBindingReady(string poseToken, out string failureReason)
    {
        failureReason = string.Empty;
        string requestedPose = NormalizePoseToken(poseToken);
        switch (requestedPose)
        {
            case "fishing_cast":
                if (fishingCastBinding == null || !fishingCastBinding.configured)
                {
                    failureReason = "hand_pose_fishing_cast_binding_missing";
                    return false;
                }

                if (!IsBindingDefinitionValid(fishingCastBinding, "fishing_cast"))
                {
                    failureReason = "hand_pose_fishing_cast_binding_invalid";
                    return false;
                }
                return true;

            case "fishing_hold":
                if (fishingHoldBinding == null || !fishingHoldBinding.configured)
                {
                    failureReason = "hand_pose_fishing_hold_binding_missing";
                    return false;
                }

                if (!IsBindingDefinitionValid(fishingHoldBinding, "fishing_hold"))
                {
                    failureReason = "hand_pose_fishing_hold_binding_invalid";
                    return false;
                }
                return true;

            case "fishing_reel":
                if (fishingReelBinding == null || !fishingReelBinding.configured)
                {
                    failureReason = "hand_pose_fishing_reel_binding_missing";
                    return false;
                }

                if (!IsBindingDefinitionValid(fishingReelBinding, "fishing_reel"))
                {
                    failureReason = "hand_pose_fishing_reel_binding_invalid";
                    return false;
                }
                return true;

            case "fishing_ready":
                if (fishingReadyBinding == null || !fishingReadyBinding.configured)
                {
                    failureReason = "hand_pose_fishing_ready_binding_missing";
                    return false;
                }

                if (!IsBindingDefinitionValid(fishingReadyBinding, "fishing_ready"))
                {
                    failureReason = "hand_pose_fishing_ready_binding_invalid";
                    return false;
                }
                return true;

            default:
                failureReason = "hand_pose_internal_pose_unknown";
                return false;
        }
    }

    private void CacheNeutralBaselineIfPossible()
    {
        if (neutralCaptured || rightHandPosePivot == null)
        {
            return;
        }

        neutralLocalPosition = rightHandPosePivot.localPosition;
        neutralLocalRotation = rightHandPosePivot.localRotation;
        confirmedLocalPosition = neutralLocalPosition;
        confirmedLocalRotation = neutralLocalRotation;
        activeTargetLocalPosition = neutralLocalPosition;
        activeTargetLocalRotation = neutralLocalRotation;
        blendStartLocalPosition = neutralLocalPosition;
        blendStartLocalRotation = neutralLocalRotation;
        neutralCaptured = true;
        currentPose = "neutral";
        targetPose = "neutral";
        poseTargetResolved = true;
        blendProgress = 1f;
        blendElapsedSeconds = 0f;
        pendingTerminalApply = false;
    }

    private void RefreshRuntimeReadiness()
    {
        CacheNeutralBaselineIfPossible();

        if (companionRoot == null)
        {
            SetRuntimeUnavailable("hand_pose_controller_unbound");
            return;
        }

        if (rightHandPosePivot == null)
        {
            SetRuntimeUnavailable("hand_pose_pivot_missing");
            return;
        }

        if (blackboard == null)
        {
            SetRuntimeUnavailable("hand_pose_controller_unbound");
            return;
        }

        if (!neutralCaptured)
        {
            SetRuntimeUnavailable("hand_pose_neutral_not_captured");
            return;
        }

        if (!IsBindingReadyForRuntime(relaxedBinding, "relaxed", required: false, "hand_pose_relaxed_binding_invalid", out string relaxedFailure))
        {
            SetRuntimeUnavailable(relaxedFailure);
            return;
        }

        if (!IsBindingReadyForRuntime(readyBinding, "ready", required: true, "hand_pose_ready_binding_invalid", out string readyFailure))
        {
            SetRuntimeUnavailable(readyBinding == null || !readyBinding.configured ? "hand_pose_ready_binding_missing" : readyFailure);
            return;
        }

        if (!IsBindingReadyForRuntime(roastPoseBinding, "roast_pose", required: true, "hand_pose_roast_binding_invalid", out string roastFailure))
        {
            SetRuntimeUnavailable(roastPoseBinding == null || !roastPoseBinding.configured ? "hand_pose_roast_binding_missing" : roastFailure);
            return;
        }

        if (!IsBindingReadyForRuntime(fishingReadyBinding, "fishing_ready", required: true, "hand_pose_fishing_ready_binding_invalid", out string fishingReadyFailure))
        {
            SetRuntimeUnavailable(fishingReadyBinding == null || !fishingReadyBinding.configured ? "hand_pose_fishing_ready_binding_missing" : fishingReadyFailure);
            return;
        }

        runtimeReady = true;
        reportedRuntimeFailure = false;
        if (IsReadinessFailureReason(lastFailureReason) && status != CP_PrimitiveExecutionStatus.Failed)
        {
            lastFailureReason = string.Empty;
        }
    }

    private void SetRuntimeUnavailable(string failureReason)
    {
        runtimeReady = false;
        if (!reportedRuntimeFailure || lastFailureReason != failureReason)
        {
            lastFailureReason = failureReason;
            reportedRuntimeFailure = true;
        }
    }

    private void RefreshDebugMeasurements()
    {
        if (rightHandPosePivot != null)
        {
            currentHandWorldPosition = rightHandPosePivot.position;
        }

        distanceFromCompanionRoot = (companionRoot != null && rightHandPosePivot != null)
            ? Vector3.Distance(companionRoot.position, rightHandPosePivot.position)
            : -1f;
        heightOffsetFromBodyReference = (bodyReference != null && rightHandPosePivot != null)
            ? rightHandPosePivot.position.y - bodyReference.position.y
            : 0f;
    }

    private void ApplyStateSafetyCleanupIfNeeded()
    {
        if (!neutralCaptured || rightHandPosePivot == null)
        {
            return;
        }

        string effectivePose = status == CP_PrimitiveExecutionStatus.Running ? targetPose : currentPose;
        if (string.IsNullOrWhiteSpace(effectivePose))
        {
            effectivePose = "neutral";
        }

        if (IsPoseAllowedInContext(effectivePose))
        {
            return;
        }

        ForceNeutralRuntimeBaseline();
    }

    private void ForceNeutralRuntimeBaseline()
    {
        if (!neutralCaptured || rightHandPosePivot == null)
        {
            return;
        }

        rightHandPosePivot.localPosition = neutralLocalPosition;
        rightHandPosePivot.localRotation = neutralLocalRotation;
        confirmedLocalPosition = neutralLocalPosition;
        confirmedLocalRotation = neutralLocalRotation;
        activeTargetLocalPosition = neutralLocalPosition;
        activeTargetLocalRotation = neutralLocalRotation;
        blendStartLocalPosition = neutralLocalPosition;
        blendStartLocalRotation = neutralLocalRotation;
        blendElapsedSeconds = 0f;
        blendProgress = 1f;
        poseTargetResolved = true;
        currentPose = "neutral";
        targetPose = "neutral";
        internalPoseActive = false;
        internalPoseOwner = string.Empty;
        pendingTerminalApply = false;
        if (status == CP_PrimitiveExecutionStatus.Running)
        {
            status = CP_PrimitiveExecutionStatus.Cancelled;
            lastFailureReason = "hand_pose_cancelled";
        }
    }

    private void RestoreNeutralAndCancel(string reason)
    {
        if (neutralCaptured && rightHandPosePivot != null)
        {
            rightHandPosePivot.localPosition = neutralLocalPosition;
            rightHandPosePivot.localRotation = neutralLocalRotation;
        }

        confirmedLocalPosition = neutralLocalPosition;
        confirmedLocalRotation = neutralLocalRotation;
        activeTargetLocalPosition = neutralLocalPosition;
        activeTargetLocalRotation = neutralLocalRotation;
        blendStartLocalPosition = neutralLocalPosition;
        blendStartLocalRotation = neutralLocalRotation;
        currentPose = "neutral";
        targetPose = "neutral";
        internalPoseActive = false;
        internalPoseOwner = string.Empty;
        poseTargetResolved = neutralCaptured;
        runtimeReady = false;
        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastFailureReason = string.IsNullOrWhiteSpace(reason) ? "hand_pose_disabled" : reason;
        blendElapsedSeconds = 0f;
        blendProgress = 1f;
        pendingTerminalApply = false;
    }

    private bool TryResolvePoseBinding(
        string poseToken,
        out Vector3 targetLocalPosition,
        out Quaternion targetLocalRotation,
        out float defaultBlendSeconds,
        out string failureReason)
    {
        targetLocalPosition = neutralLocalPosition;
        targetLocalRotation = neutralLocalRotation;
        defaultBlendSeconds = 0.25f;
        failureReason = string.Empty;

        switch (poseToken)
        {
            case "neutral":
                return true;

            case "relaxed":
                if (relaxedBinding == null || !relaxedBinding.configured)
                {
                    return true;
                }
                return TryApplyConfiguredBinding(relaxedBinding, "relaxed", "hand_pose_relaxed_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            case "ready":
                if (readyBinding == null || !readyBinding.configured)
                {
                    failureReason = "hand_pose_ready_binding_missing";
                    return false;
                }
                return TryApplyConfiguredBinding(readyBinding, "ready", "hand_pose_ready_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            case "roast_pose":
                if (roastPoseBinding == null || !roastPoseBinding.configured)
                {
                    failureReason = "hand_pose_roast_binding_missing";
                    return false;
                }
                return TryApplyConfiguredBinding(roastPoseBinding, "roast_pose", "hand_pose_roast_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            case "fishing_ready":
                if (fishingReadyBinding == null || !fishingReadyBinding.configured)
                {
                    failureReason = "hand_pose_fishing_ready_binding_missing";
                    return false;
                }
                return TryApplyConfiguredBinding(fishingReadyBinding, "fishing_ready", "hand_pose_fishing_ready_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            case "fishing_hold":
                if (fishingHoldBinding == null || !fishingHoldBinding.configured)
                {
                    failureReason = "hand_pose_fishing_hold_binding_missing";
                    return false;
                }
                return TryApplyConfiguredBinding(fishingHoldBinding, "fishing_hold", "hand_pose_fishing_hold_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            case "fishing_cast":
                if (fishingCastBinding == null || !fishingCastBinding.configured)
                {
                    failureReason = "hand_pose_fishing_cast_binding_missing";
                    return false;
                }
                return TryApplyConfiguredBinding(fishingCastBinding, "fishing_cast", "hand_pose_fishing_cast_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            case "fishing_reel":
                if (fishingReelBinding == null || !fishingReelBinding.configured)
                {
                    failureReason = "hand_pose_fishing_reel_binding_missing";
                    return false;
                }
                return TryApplyConfiguredBinding(fishingReelBinding, "fishing_reel", "hand_pose_fishing_reel_binding_invalid", out targetLocalPosition, out targetLocalRotation, out defaultBlendSeconds, out failureReason);

            default:
                failureReason = "hand_pose_unknown";
                return false;
        }
    }

    private bool IsPoseAllowedInContext(string poseToken)
    {
        string pose = NormalizePoseToken(poseToken);
        if (activityContextRouter != null)
        {
            string activity = NormalizePoseToken(activityContextRouter.ActiveActivity);
            string phase = NormalizePoseToken(activityContextRouter.ActivityPhase);
            switch (activity)
            {
                case "common":
                    return pose == "neutral" || pose == "relaxed";
                case "fishing":
                    if (phase == "ready" || phase == "engagement")
                    {
                        return pose == "neutral" ||
                               pose == "relaxed" ||
                               pose == "fishing_ready" ||
                               (internalPoseActive && (pose == "fishing_cast" || pose == "fishing_hold" || pose == "fishing_reel"));
                    }
                    return pose == "neutral" || pose == "relaxed";
                case "campfire":
                    return IsPoseAllowedInState(ResolveAuthoritativeState(), pose);
            }
        }

        return IsPoseAllowedInState(ResolveAuthoritativeState(), pose);
    }

    private bool IsPoseAllowedInState(string authoritativeState, string poseToken)
    {
        string state = authoritativeState ?? string.Empty;
        string pose = NormalizePoseToken(poseToken);

        switch (state)
        {
            case CP_StateNames.S1:
                return pose == "neutral" || pose == "relaxed";
            case CP_StateNames.F2:
                return pose == "neutral" || pose == "relaxed" || pose == "fishing_ready";
            case CP_StateNames.F3:
                return pose == "neutral" ||
                       pose == "relaxed" ||
                       pose == "fishing_ready" ||
                       (internalPoseActive && (pose == "fishing_cast" || pose == "fishing_hold" || pose == "fishing_reel"));
            case CP_StateNames.S2:
                return pose == "neutral" || pose == "relaxed" || pose == "ready";
            case CP_StateNames.S3:
                return pose == "neutral" || pose == "relaxed" || pose == "ready" || pose == "roast_pose";
            default:
                return pose == "neutral";
        }
    }

    private bool IsBindingReadyForRuntime(
        CP_HandPoseBinding binding,
        string expectedPoseToken,
        bool required,
        string invalidFailure,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (binding == null || !binding.configured)
        {
            return !required;
        }

        if (!IsBindingDefinitionValid(binding, expectedPoseToken))
        {
            failureReason = invalidFailure;
            return false;
        }

        return true;
    }

    private bool TryApplyConfiguredBinding(
        CP_HandPoseBinding binding,
        string expectedPoseToken,
        string invalidFailure,
        out Vector3 targetLocalPosition,
        out Quaternion targetLocalRotation,
        out float defaultBlendSeconds,
        out string failureReason)
    {
        targetLocalPosition = neutralLocalPosition;
        targetLocalRotation = neutralLocalRotation;
        defaultBlendSeconds = 0.25f;
        failureReason = string.Empty;

        if (!IsBindingDefinitionValid(binding, expectedPoseToken))
        {
            failureReason = invalidFailure;
            return false;
        }

        targetLocalPosition = binding.localPosition;
        targetLocalRotation = Quaternion.Euler(binding.localEulerAngles);
        defaultBlendSeconds = Mathf.Clamp(binding.defaultBlendSeconds, minimumBlendSeconds, maximumBlendSeconds);
        return true;
    }

    private bool IsBindingDefinitionValid(CP_HandPoseBinding binding, string expectedPoseToken)
    {
        if (binding == null)
        {
            return false;
        }

        if (!binding.configured)
        {
            return false;
        }

        if (NormalizePoseToken(binding.poseId) != expectedPoseToken)
        {
            return false;
        }

        if (ContainsNaN(binding.localPosition) || ContainsNaN(binding.localEulerAngles))
        {
            return false;
        }

        return IsFinite(binding.defaultBlendSeconds);
    }

    private bool ContainsNaN(Vector3 value)
    {
        return float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) ||
               float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z);
    }

    private bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private float ResolveBlendDurationSeconds(float primitiveDuration, float fallbackDuration)
    {
        float candidate = primitiveDuration > 0f ? primitiveDuration : fallbackDuration;
        return Mathf.Clamp(candidate, minimumBlendSeconds, maximumBlendSeconds);
    }

    private bool IsAtTransform(Vector3 localPosition, Quaternion localRotation)
    {
        if (rightHandPosePivot == null)
        {
            return false;
        }

        float positionError = Vector3.Distance(rightHandPosePivot.localPosition, localPosition);
        float rotationError = Quaternion.Angle(rightHandPosePivot.localRotation, localRotation);
        return positionError <= completionPositionTolerance &&
               rotationError <= completionRotationToleranceDegrees;
    }

    private bool IsConfirmedTransformApplied()
    {
        return IsAtTransform(confirmedLocalPosition, confirmedLocalRotation);
    }

    private bool HasConfirmedPose()
    {
        return !string.IsNullOrWhiteSpace(currentPose);
    }

    private void ApplyConfirmedTransform()
    {
        if (!neutralCaptured || rightHandPosePivot == null)
        {
            return;
        }

        rightHandPosePivot.localPosition = confirmedLocalPosition;
        rightHandPosePivot.localRotation = confirmedLocalRotation;
    }

    private void RestoreConfirmedPoseVisual()
    {
        if (!neutralCaptured || rightHandPosePivot == null)
        {
            return;
        }

        rightHandPosePivot.localPosition = confirmedLocalPosition;
        rightHandPosePivot.localRotation = confirmedLocalRotation;
    }

    private void SetFailureState(string failureReason)
    {
        RestoreConfirmedPoseVisual();
        activeTargetLocalPosition = confirmedLocalPosition;
        activeTargetLocalRotation = confirmedLocalRotation;
        blendStartLocalPosition = confirmedLocalPosition;
        blendStartLocalRotation = confirmedLocalRotation;
        status = CP_PrimitiveExecutionStatus.Failed;
        lastFailureReason = string.IsNullOrWhiteSpace(failureReason) ? "hand_pose_unknown" : failureReason;
        poseTargetResolved = neutralCaptured;
        pendingTerminalApply = false;
        blendElapsedSeconds = 0f;
        blendProgress = 1f;
        targetPose = currentPose;
    }

    private bool IsReadinessFailureReason(string failureReason)
    {
        switch (failureReason)
        {
            case "hand_pose_controller_unbound":
            case "hand_pose_pivot_missing":
            case "hand_pose_neutral_not_captured":
            case "hand_pose_ready_binding_missing":
            case "hand_pose_ready_binding_invalid":
            case "hand_pose_roast_binding_missing":
            case "hand_pose_roast_binding_invalid":
            case "hand_pose_fishing_ready_binding_missing":
            case "hand_pose_fishing_ready_binding_invalid":
            case "hand_pose_relaxed_binding_invalid":
                return true;
            default:
                return false;
        }
    }

    private void BeginPoseBlend(string requestedPose, Vector3 targetLocalPosition, Quaternion targetLocalRotation, float resolvedBlendSeconds)
    {
        poseTargetResolved = true;
        targetPose = requestedPose;
        activeTargetLocalPosition = targetLocalPosition;
        activeTargetLocalRotation = targetLocalRotation;
        blendStartLocalPosition = rightHandPosePivot.localPosition;
        blendStartLocalRotation = rightHandPosePivot.localRotation;
        blendDurationSeconds = resolvedBlendSeconds;
        blendElapsedSeconds = 0f;
        blendProgress = 0f;
        lastFailureReason = string.Empty;
        pendingTerminalApply = false;
        reportedRuntimeFailure = false;
    }

    private string ResolveSafeFallbackPose(string requestedFallbackPose)
    {
        string fallbackPose = NormalizePoseToken(requestedFallbackPose);
        if (IsPoseAllowedInContext(fallbackPose))
        {
            return fallbackPose;
        }

        switch (ResolveAuthoritativeState())
        {
            case CP_StateNames.F3:
                return "fishing_ready";
            case CP_StateNames.F2:
                return "fishing_ready";
            case CP_StateNames.S3:
                return "roast_pose";
            case CP_StateNames.S2:
                return "ready";
            case CP_StateNames.S1:
                return "neutral";
            default:
                return "neutral";
        }
    }

    private string NormalizePoseToken(string token)
    {
        return string.IsNullOrWhiteSpace(token) ? string.Empty : token.Trim().ToLowerInvariant();
    }

    private string ResolveAuthoritativeState()
    {
        if (activityContextRouter != null)
        {
            string canonicalState = activityContextRouter.CanonicalState;
            if (!string.IsNullOrWhiteSpace(canonicalState) && canonicalState != CP_StateNames.Unknown)
            {
                return canonicalState;
            }
        }

        if (blackboard == null)
        {
            return string.Empty;
        }

        if (!blackboard.campfire_started)
        {
            return CP_StateNames.S1;
        }

        if (!blackboard.stick_picked)
        {
            return CP_StateNames.S2;
        }

        return CP_StateNames.S3;
    }
}
