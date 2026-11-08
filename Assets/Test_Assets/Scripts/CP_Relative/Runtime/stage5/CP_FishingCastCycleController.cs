using System.Collections.Generic;
using UnityEngine;

// Fishing finite semantic action controller:
// owns one complete F3 cast/wait/reel/recover cycle and delegates visual object
// lifecycle to CP_CompanionFishingVisualController.
public class CP_FishingCastCycleController : MonoBehaviour
{
    private const string PrimitiveTypeFishingCastCycle = "perform_fishing_cast_cycle";
    private const string PoseFishingReady = "fishing_ready";
    private const string PoseFishingHold = "fishing_hold";
    private const string PoseFishingCast = "fishing_cast";
    private const string PoseFishingReel = "fishing_reel";
    private const string ActivityFishing = "fishing";
    private const string PhaseEngagement = "engagement";
    private const string InternalPoseOwnerId = "fishing_cast_cycle";

    private enum CP_FishingCastCyclePhase
    {
        Idle,
        Preparing,
        Casting,
        SettlingToHold,
        Waiting,
        Reeling,
        RecoveringToReady,
        Completed,
        Cancelled,
        Failed,
    }

    [Header("References")]
    public CP_ActivityContextRouter activityContextRouter;
    public CP_FishingAnchorManager fishingAnchorManager;
    public CP_PropController propController;
    public CP_HandPoseController handPoseController;
    public CP_CompanionFishingVisualController companionFishingVisualController;

    [Header("Timing")]
    [Min(0.1f)] public float castDurationSeconds = 0.9f;
    [Min(0.1f)] public float settleToHoldDurationSeconds = 0.25f;
    [Min(0f)] public float fixedWaitDurationSeconds = 18.0f;
    [Min(0.1f)] public float reelDurationSeconds = 0.6f;
    [Min(0.1f)] public float recoverToReadyDurationSeconds = 0.25f;
    [Min(0f)] public float hardTimeoutGraceSeconds = 2.0f;

    [Header("Runtime Debug")]
    public CP_PrimitiveExecutionStatus status = CP_PrimitiveExecutionStatus.Idle;
    public string cyclePhase = "idle";
    public float elapsedSeconds;
    public float phaseElapsedSeconds;
    public float waitRemainingSeconds;
    public string lastFailureReason = string.Empty;
    public string lastCancellationReason = string.Empty;
    public bool runtimeContextValid;
    public bool rodVisible;
    public bool readyPoseReady;
    public bool visualRuntimeReady;
    public bool castPoseRequested;
    public bool castVisualRequested;
    public bool holdPoseRequested;
    public bool reelPoseRequested;
    public bool reelVisualRequested;
    public bool readyRecoveryRequested;
    public bool reelFinalized;
    public float expectedCycleDurationSeconds;

    private CP_FishingCastCyclePhase internalPhase = CP_FishingCastCyclePhase.Idle;

    public CP_PrimitiveExecutionStatus Status => status;
    public string LastFailureReason => lastFailureReason;
    public bool IsRunning => status == CP_PrimitiveExecutionStatus.Running;
    public float ExpectedCycleDurationSeconds => CalculateExpectedCycleDurationSeconds();

    private void Awake()
    {
        RefreshDebugState();
    }

    private void Update()
    {
        RefreshDebugState();
    }

    private void OnDisable()
    {
        if (status == CP_PrimitiveExecutionStatus.Running)
        {
            Cancel("fishing_cast_cycle_controller_disabled");
            return;
        }

        RefreshDebugState();
    }

    public bool TryBegin(CP_MotionPrimitive primitive, CP_PrimitiveExecutionContext context, out string failureReason)
    {
        failureReason = string.Empty;
        RefreshDebugState();

        if (status == CP_PrimitiveExecutionStatus.Running)
        {
            failureReason = "fishing_cast_cycle_already_running";
            lastFailureReason = failureReason;
            return false;
        }

        if (primitive == null || NormalizePrimitiveType(primitive.type) != PrimitiveTypeFishingCastCycle)
        {
            failureReason = "fishing_cast_cycle_primitive_type_invalid";
            SetFailedState(failureReason);
            return false;
        }

        if (!ValidateStartPrerequisites(out failureReason))
        {
            SetFailedState(failureReason);
            return false;
        }

        if (!companionFishingVisualController.PrepareForNewCycle(out failureReason))
        {
            SetFailedState(failureReason);
            return false;
        }

        expectedCycleDurationSeconds = CalculateExpectedCycleDurationSeconds();
        elapsedSeconds = 0f;
        phaseElapsedSeconds = 0f;
        waitRemainingSeconds = Mathf.Max(0f, fixedWaitDurationSeconds);
        lastFailureReason = string.Empty;
        lastCancellationReason = string.Empty;
        castPoseRequested = false;
        castVisualRequested = false;
        holdPoseRequested = false;
        reelPoseRequested = false;
        reelVisualRequested = false;
        readyRecoveryRequested = false;
        reelFinalized = false;
        status = CP_PrimitiveExecutionStatus.Running;
        SetPhase(CP_FishingCastCyclePhase.Preparing);
        RefreshDebugState();
        return true;
    }

    public CP_PrimitiveExecutionStatus Tick(float deltaTime)
    {
        RefreshDebugState();

        if (status != CP_PrimitiveExecutionStatus.Running)
        {
            return status;
        }

        if (!ValidateOngoingRuntime(out string invalidReason) || !ValidatePhasePose(out invalidReason))
        {
            Cancel(invalidReason);
            return status;
        }

        float safeDeltaTime = Mathf.Max(0f, deltaTime);
        elapsedSeconds += safeDeltaTime;
        phaseElapsedSeconds += safeDeltaTime;
        expectedCycleDurationSeconds = CalculateExpectedCycleDurationSeconds();
        if (elapsedSeconds > expectedCycleDurationSeconds)
        {
            SetFailedState("fishing_cast_cycle_timeout");
            return status;
        }

        switch (internalPhase)
        {
            case CP_FishingCastCyclePhase.Preparing:
                SetPhase(CP_FishingCastCyclePhase.Casting);
                break;

            case CP_FishingCastCyclePhase.Casting:
                if (!castPoseRequested)
                {
                    if (!handPoseController.TryBeginInternalPose(PoseFishingCast, castDurationSeconds, InternalPoseOwnerId, out string beginCastFailure))
                    {
                        SetFailedState(beginCastFailure);
                        break;
                    }

                    castPoseRequested = true;
                }

                if (!castVisualRequested)
                {
                    if (!companionFishingVisualController.BeginCast(out string beginCastVisualFailure))
                    {
                        SetFailedState(beginCastVisualFailure);
                        break;
                    }

                    castVisualRequested = true;
                }

                if (handPoseController.IsInternalPoseComplete(InternalPoseOwnerId, PoseFishingCast) &&
                    companionFishingVisualController.IsCastMotionComplete())
                {
                    SetPhase(CP_FishingCastCyclePhase.SettlingToHold);
                }
                break;

            case CP_FishingCastCyclePhase.SettlingToHold:
                if (!holdPoseRequested)
                {
                    if (!handPoseController.TryBeginInternalPose(PoseFishingHold, settleToHoldDurationSeconds, InternalPoseOwnerId, out string beginHoldFailure))
                    {
                        SetFailedState(beginHoldFailure);
                        break;
                    }

                    holdPoseRequested = true;
                }

                if (handPoseController.IsInternalPoseComplete(InternalPoseOwnerId, PoseFishingHold))
                {
                    companionFishingVisualController.BeginWaiting();
                    SetPhase(CP_FishingCastCyclePhase.Waiting);
                }
                break;

            case CP_FishingCastCyclePhase.Waiting:
                waitRemainingSeconds = Mathf.Max(0f, fixedWaitDurationSeconds - phaseElapsedSeconds);
                if (phaseElapsedSeconds >= Mathf.Max(0f, fixedWaitDurationSeconds))
                {
                    waitRemainingSeconds = 0f;
                    SetPhase(CP_FishingCastCyclePhase.Reeling);
                }
                break;

            case CP_FishingCastCyclePhase.Reeling:
                if (!reelPoseRequested)
                {
                    if (!handPoseController.TryBeginInternalPose(PoseFishingReel, reelDurationSeconds, InternalPoseOwnerId, out string beginReelFailure))
                    {
                        SetFailedState(beginReelFailure);
                        break;
                    }

                    reelPoseRequested = true;
                }

                if (!reelVisualRequested)
                {
                    if (!companionFishingVisualController.BeginReel(out string beginReelVisualFailure))
                    {
                        SetFailedState(beginReelVisualFailure);
                        break;
                    }

                    reelVisualRequested = true;
                }

                if (!reelFinalized &&
                    handPoseController.IsInternalPoseComplete(InternalPoseOwnerId, PoseFishingReel) &&
                    companionFishingVisualController.IsReelMotionComplete())
                {
                    if (!companionFishingVisualController.CompleteReelAndPersistFish(out string finalizeReelFailure))
                    {
                        SetFailedState(finalizeReelFailure);
                        break;
                    }

                    reelFinalized = true;
                    SetPhase(CP_FishingCastCyclePhase.RecoveringToReady);
                }
                break;

            case CP_FishingCastCyclePhase.RecoveringToReady:
                if (!readyRecoveryRequested)
                {
                    if (!handPoseController.TryBeginInternalPose(PoseFishingReady, recoverToReadyDurationSeconds, InternalPoseOwnerId, out string beginReadyFailure))
                    {
                        SetFailedState(beginReadyFailure);
                        break;
                    }

                    readyRecoveryRequested = true;
                }

                if (handPoseController.IsInternalPoseComplete(InternalPoseOwnerId, PoseFishingReady))
                {
                    if (!handPoseController.CompleteInternalPose(InternalPoseOwnerId, out string releaseFailure))
                    {
                        SetFailedState(releaseFailure);
                        break;
                    }

                    status = CP_PrimitiveExecutionStatus.Succeeded;
                    SetPhase(CP_FishingCastCyclePhase.Completed);
                    lastFailureReason = string.Empty;
                }
                break;
        }

        RefreshDebugState();
        return status;
    }

    public void Cancel(string reason)
    {
        if (companionFishingVisualController != null)
        {
            companionFishingVisualController.CancelTransientCycleVisuals();
        }

        if (handPoseController != null)
        {
            handPoseController.CancelInternalPose(InternalPoseOwnerId, string.Empty);
        }

        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastCancellationReason = string.IsNullOrWhiteSpace(reason) ? "fishing_cast_cycle_cancelled" : reason;
        lastFailureReason = lastCancellationReason;
        SetPhase(CP_FishingCastCyclePhase.Cancelled);
        RefreshDebugState();
    }

    public void AcknowledgeTerminalStatus()
    {
        if (status == CP_PrimitiveExecutionStatus.Succeeded ||
            status == CP_PrimitiveExecutionStatus.Failed ||
            status == CP_PrimitiveExecutionStatus.Cancelled)
        {
            status = CP_PrimitiveExecutionStatus.Idle;
            castPoseRequested = false;
            castVisualRequested = false;
            holdPoseRequested = false;
            reelPoseRequested = false;
            reelVisualRequested = false;
            readyRecoveryRequested = false;
            reelFinalized = false;
            if (internalPhase == CP_FishingCastCyclePhase.Completed ||
                internalPhase == CP_FishingCastCyclePhase.Cancelled ||
                internalPhase == CP_FishingCastCyclePhase.Failed)
            {
                internalPhase = CP_FishingCastCyclePhase.Idle;
                cyclePhase = "idle";
            }
        }
    }

    [ContextMenu("Debug Start One Cast Cycle")]
    public void DebugStartOneCastCycle()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("CP_FishingCastCycleController: Debug start only works in Play Mode.", this);
            return;
        }

        if (TryQueueDebugCycleViaMotionExecutor(out string failureReason))
        {
            Debug.Log("CP_FishingCastCycleController: queued one debug Fishing cast cycle.", this);
            return;
        }

        Debug.LogWarning($"CP_FishingCastCycleController: debug start failed: {failureReason}", this);
    }

    [ContextMenu("Debug Cancel Cast Cycle")]
    public void DebugCancelCastCycle()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("CP_FishingCastCycleController: Debug cancel only works in Play Mode.", this);
            return;
        }

        CP_MotionExecutor motionExecutor = GetComponent<CP_MotionExecutor>();
        if (motionExecutor != null &&
            motionExecutor.IsBusy &&
            NormalizePrimitiveType(motionExecutor.CurrentPrimitiveType) == PrimitiveTypeFishingCastCycle)
        {
            motionExecutor.StopExecution("debug_manual_cancel");
            Debug.Log("CP_FishingCastCycleController: debug cancel requested through MotionExecutor.", this);
            return;
        }

        if (status == CP_PrimitiveExecutionStatus.Running)
        {
            Cancel("debug_manual_cancel");
            Debug.Log("CP_FishingCastCycleController: debug cancel requested directly on the cycle controller.", this);
            return;
        }

        Debug.LogWarning("CP_FishingCastCycleController: no active Fishing cast cycle to cancel.", this);
    }

    private bool TryQueueDebugCycleViaMotionExecutor(out string failureReason)
    {
        failureReason = string.Empty;

        CP_MotionExecutor motionExecutor = GetComponent<CP_MotionExecutor>();
        if (motionExecutor == null)
        {
            failureReason = "motion_executor_missing";
            return false;
        }

        CP_LegalityChecker legalityChecker = motionExecutor.legalityChecker;
        if (legalityChecker == null)
        {
            failureReason = "legality_checker_missing";
            return false;
        }

        CP_PolicyResponse debugPolicy = BuildDebugCyclePolicy();
        CP_PolicyResponse sanitizedPolicy = legalityChecker.SanitizePolicy(debugPolicy, out List<string> warnings);
        if (sanitizedPolicy == null || sanitizedPolicy.motion_primitives == null || sanitizedPolicy.motion_primitives.Count == 0)
        {
            failureReason = warnings != null && warnings.Count > 0 ? warnings[warnings.Count - 1] : "debug_cycle_policy_sanitized_empty";
            return false;
        }

        if (!motionExecutor.TryLoadPolicyWhenIdle(sanitizedPolicy))
        {
            failureReason = "motion_executor_busy";
            return false;
        }

        return true;
    }

    private CP_PolicyResponse BuildDebugCyclePolicy()
    {
        CP_PolicyResponse response = new CP_PolicyResponse();
        response.control.execute_policy = true;
        response.control.companion_should_run = true;
        response.state.current_state = CP_StateNames.F3;
        response.state.previous_state = CP_StateNames.F3;
        response.policy.policy_id = "debug_fishing_cast_cycle";
        response.policy.policy_source = "fallback";
        response.policy.activity_mode = "parallel_engage";
        response.policy.policy_lifetime = CP_LegalityChecker.FishingCastCycleSemanticDurationSeconds;
        response.policy.intent_summary = "Debug manual one-shot Fishing cast cycle.";
        response.debug.request_role = "manual_current_policy";
        response.debug.source_state_epoch = 0;
        response.debug.source_policy_index = 0;
        response.motion_primitives.Add(new CP_MotionPrimitive
        {
            type = PrimitiveTypeFishingCastCycle,
            duration = CP_LegalityChecker.FishingCastCycleSemanticDurationSeconds,
            style = "debug_manual",
            notes = "Manual debug one-shot Fishing cast cycle."
        });
        return response;
    }

    private void SetPhase(CP_FishingCastCyclePhase nextPhase)
    {
        internalPhase = nextPhase;
        phaseElapsedSeconds = 0f;
        cyclePhase = ToPhaseString(nextPhase);

        if (nextPhase == CP_FishingCastCyclePhase.Preparing)
        {
            waitRemainingSeconds = Mathf.Max(0f, fixedWaitDurationSeconds);
        }
        else if (nextPhase != CP_FishingCastCyclePhase.Waiting)
        {
            waitRemainingSeconds = nextPhase == CP_FishingCastCyclePhase.Completed ? 0f : Mathf.Max(0f, fixedWaitDurationSeconds);
        }
    }

    private bool ValidateStartPrerequisites(out string failureReason)
    {
        failureReason = string.Empty;
        if (!ValidateOngoingRuntime(out failureReason))
        {
            return false;
        }

        if (handPoseController == null)
        {
            failureReason = "fishing_cast_cycle_hand_pose_controller_missing";
            return false;
        }

        if (NormalizeToken(handPoseController.CurrentPose) != PoseFishingReady)
        {
            failureReason = "fishing_cast_cycle_ready_pose_not_ready";
            return false;
        }

        if (!handPoseController.IsInternalPoseBindingReady(PoseFishingCast, out failureReason) ||
            !handPoseController.IsInternalPoseBindingReady(PoseFishingHold, out failureReason) ||
            !handPoseController.IsInternalPoseBindingReady(PoseFishingReel, out failureReason) ||
            !handPoseController.IsInternalPoseBindingReady(PoseFishingReady, out failureReason))
        {
            return false;
        }

        if (companionFishingVisualController == null)
        {
            failureReason = "companion_fishing_visual_controller_missing";
            return false;
        }

        return companionFishingVisualController.HasCycleVisualRuntimeReady(out failureReason);
    }

    private bool ValidateOngoingRuntime(out string failureReason)
    {
        failureReason = string.Empty;

        if (activityContextRouter == null)
        {
            failureReason = "fishing_cast_cycle_activity_context_missing";
            runtimeContextValid = false;
            return false;
        }

        if (NormalizeToken(activityContextRouter.CanonicalState) != NormalizeToken(CP_StateNames.F3) ||
            NormalizeToken(activityContextRouter.ActiveActivity) != ActivityFishing ||
            NormalizeToken(activityContextRouter.ActivityPhase) != PhaseEngagement ||
            activityContextRouter.FishingCompleted)
        {
            failureReason = "fishing_cast_cycle_not_in_f3";
            runtimeContextValid = false;
            return false;
        }

        if (fishingAnchorManager == null ||
            !fishingAnchorManager.HasFishingAssignment ||
            string.IsNullOrWhiteSpace(fishingAnchorManager.AssignedFishingAnchorId) ||
            !fishingAnchorManager.CompanionAtAssignedFishingAnchor)
        {
            failureReason = "fishing_cast_cycle_anchor_not_ready";
            runtimeContextValid = false;
            return false;
        }

        if (propController == null ||
            !propController.IsFishingRodRuntimeReady ||
            !propController.HasCompanionFishingRod ||
            NormalizeToken(propController.CurrentFishingRodState) != "visible_in_hand")
        {
            failureReason = "fishing_cast_cycle_rod_not_ready";
            runtimeContextValid = false;
            return false;
        }

        if (handPoseController == null || !handPoseController.IsRuntimeReady)
        {
            failureReason = "fishing_cast_cycle_hand_pose_controller_missing";
            runtimeContextValid = false;
            return false;
        }

        runtimeContextValid = true;
        return true;
    }

    private bool ValidatePhasePose(out string failureReason)
    {
        failureReason = string.Empty;
        if (handPoseController == null)
        {
            failureReason = "fishing_cast_cycle_hand_pose_controller_missing";
            return false;
        }

        string currentPose = NormalizeToken(handPoseController.CurrentPose);
        switch (internalPhase)
        {
            case CP_FishingCastCyclePhase.Preparing:
                return currentPose == PoseFishingReady
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_ready_pose_not_ready");

            case CP_FishingCastCyclePhase.Casting:
                return currentPose == PoseFishingReady || currentPose == PoseFishingCast
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_cast_pose_invalid");

            case CP_FishingCastCyclePhase.SettlingToHold:
                return currentPose == PoseFishingCast || currentPose == PoseFishingHold
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_hold_pose_invalid");

            case CP_FishingCastCyclePhase.Waiting:
                return currentPose == PoseFishingHold
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_wait_pose_invalid");

            case CP_FishingCastCyclePhase.Reeling:
                return currentPose == PoseFishingHold || currentPose == PoseFishingReel
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_reel_pose_invalid");

            case CP_FishingCastCyclePhase.RecoveringToReady:
                return currentPose == PoseFishingReel || currentPose == PoseFishingReady
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_recover_pose_invalid");

            case CP_FishingCastCyclePhase.Completed:
                return currentPose == PoseFishingReady
                    ? true
                    : FailPose(out failureReason, "fishing_cast_cycle_complete_pose_invalid");

            default:
                return true;
        }
    }

    private void SetFailedState(string failureReason)
    {
        if (companionFishingVisualController != null)
        {
            companionFishingVisualController.CancelTransientCycleVisuals();
        }

        if (handPoseController != null)
        {
            handPoseController.CancelInternalPose(InternalPoseOwnerId, string.Empty);
        }

        status = CP_PrimitiveExecutionStatus.Failed;
        lastFailureReason = string.IsNullOrWhiteSpace(failureReason) ? "fishing_cast_cycle_failed" : failureReason;
        SetPhase(CP_FishingCastCyclePhase.Failed);
        runtimeContextValid = false;
    }

    private float CalculateExpectedCycleDurationSeconds()
    {
        float resolvedCastDuration = companionFishingVisualController != null
            ? companionFishingVisualController.CastDurationSeconds
            : Mathf.Max(0.1f, castDurationSeconds);
        float resolvedReelDuration = companionFishingVisualController != null
            ? companionFishingVisualController.ReelDurationSeconds
            : Mathf.Max(0.1f, reelDurationSeconds);

        castDurationSeconds = resolvedCastDuration;
        reelDurationSeconds = resolvedReelDuration;

        return resolvedCastDuration +
               Mathf.Max(0.1f, settleToHoldDurationSeconds) +
               Mathf.Max(0f, fixedWaitDurationSeconds) +
               resolvedReelDuration +
               Mathf.Max(0.1f, recoverToReadyDurationSeconds) +
               Mathf.Max(0f, hardTimeoutGraceSeconds);
    }

    private void RefreshDebugState()
    {
        rodVisible = propController != null && propController.HasCompanionFishingRod;
        readyPoseReady = handPoseController != null && NormalizeToken(handPoseController.CurrentPose) == PoseFishingReady;
        visualRuntimeReady = companionFishingVisualController != null && companionFishingVisualController.HasCycleVisualRuntimeReady(out _);
        expectedCycleDurationSeconds = CalculateExpectedCycleDurationSeconds();
        if (status != CP_PrimitiveExecutionStatus.Running && internalPhase == CP_FishingCastCyclePhase.Idle)
        {
            waitRemainingSeconds = Mathf.Max(0f, fixedWaitDurationSeconds);
        }
    }

    private string NormalizePrimitiveType(string primitiveType)
    {
        return string.IsNullOrWhiteSpace(primitiveType) ? string.Empty : primitiveType.Trim().ToLowerInvariant();
    }

    private string NormalizeToken(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private bool FailPose(out string failureReason, string code)
    {
        failureReason = code;
        return false;
    }

    private string ToPhaseString(CP_FishingCastCyclePhase phase)
    {
        switch (phase)
        {
            case CP_FishingCastCyclePhase.Preparing:
                return "preparing";
            case CP_FishingCastCyclePhase.Casting:
                return "casting";
            case CP_FishingCastCyclePhase.SettlingToHold:
                return "settling_to_hold";
            case CP_FishingCastCyclePhase.Waiting:
                return "waiting";
            case CP_FishingCastCyclePhase.Reeling:
                return "reeling";
            case CP_FishingCastCyclePhase.RecoveringToReady:
                return "recovering_to_ready";
            case CP_FishingCastCyclePhase.Completed:
                return "completed";
            case CP_FishingCastCyclePhase.Cancelled:
                return "cancelled";
            case CP_FishingCastCyclePhase.Failed:
                return "failed";
            default:
                return "idle";
        }
    }
}
