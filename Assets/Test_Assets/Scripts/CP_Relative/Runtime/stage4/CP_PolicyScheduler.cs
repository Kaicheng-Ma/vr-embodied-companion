using System.Collections.Generic;
using UnityEngine;

// The sole Stage4B scheduling owner. It coordinates one HTTP job and one future policy slot.
public class CP_PolicyScheduler : MonoBehaviour
{
    public const string SoftFinishCurrentPrimitive = "soft_finish_current_primitive";
    public const string HardInterruptToTransition = "hard_interrupt_to_transition";
    public const string ExecutionContextCommonS1 = "COMMON_S1";
    public const string ExecutionContextFishingF2 = "FISHING_F2";
    public const string ExecutionContextFishingF3 = "FISHING_F3";
    public const string ExecutionContextCampfireS2 = "CAMPFIRE_S2";
    public const string ExecutionContextCampfireS3 = "CAMPFIRE_S3";

    [Header("References")]
    public CP_PerceptionBuilder perceptionBuilder;
    public CP_PythonPolicyClient pythonPolicyClient;
    public CP_PolicyReceiver policyReceiver;
    public CP_MotionExecutor motionExecutor;
    public CP_DefaultAttentionController defaultAttentionController;

    [Header("Settings")]
    public bool schedulerEnabled = true;
    public string schedulerMode = "single_step_lookahead";
    public bool allowLookaheadWhileMotionBusy = true;
    public string stateChangeInterruptionMode = SoftFinishCurrentPrimitive;
    public bool forceHardInterruptOnIllegalPrimitive = true;
    [Range(1, 1)] public int maxPendingPolicyCount = 1;
    [Min(0.1f)] public float requestRetryDelaySeconds = 0.5f;
    [SerializeField, Min(0.1f)] private float pendingPolicyMaxAgeSeconds = 15f;
    public bool logScheduler = true;

    [Header("Scheduler Debug")]
    public string currentState = CP_StateNames.Unknown;
    public string previousState = CP_StateNames.Unknown;
    public int currentStateEpoch;
    public string stateTransitionLabel = string.Empty;
    public string currentExecutionContextKey = ExecutionContextCommonS1;
    public string previousExecutionContextKey = ExecutionContextCommonS1;
    public string lastExecutionContextChangeReason = string.Empty;
    // currentPolicyIndex tracks the scheduler's current execution slot; activeJobPolicyIndex and pendingPolicySourceIndex keep the in-flight and pending views separate.
    public int currentPolicyIndex;
    public int currentAiGenerationJobId = -1;
    public int activeJobStateEpoch = -1;
    public int activeJobPolicyIndex = -1;
    public string activeJobRole = string.Empty;
    public string lastRequestRole = string.Empty;
    public string lastResponseRole = string.Empty;
    public string schedulerState = "inactive";
    public string lastInterruptionType = string.Empty;
    public string forcedHardInterruptReason = string.Empty;
    public string lastSchedulerWarning = string.Empty;
    public bool shutdownLatched;
    public bool policyErrorActive;
    public bool pipelineStarted;
    public bool pipelineRestartRequired;
    public bool barrierReady;
    public bool barrierCommitInProgress;
    public int activeMotionGroupId = -1;
    public int lastCompletedMotionGroupId = -1;
    public int lastInterruptedMotionGroupId = -1;
    public string lastInterruptedMotionGroupReason = string.Empty;
    public bool automaticallyRetryPolicyError = true;
    [Min(0.1f)] public float policyErrorRetryDelaySeconds = 5f;
    public bool policyErrorRecoveryPending;
    public float policyErrorRetryAtTime;
    public float policyErrorRetryRemainingSeconds;

    [Header("Request Intent / Retry")]
    public bool requestPending;
    public string pendingRequestRole = string.Empty;
    public int pendingRequestEpoch = -1;
    public string pendingRequestReason = string.Empty;
    public float retryAtTime;

    [Header("Single Pending Policy")]
    public bool pendingSlotOccupied;
    public string pendingNextPolicyStatus = "empty";
    public string pendingPolicyId = string.Empty;
    public string pendingPolicySource = string.Empty;
    public string pendingPolicyPriority = string.Empty;
    public int pendingPolicySourceStateEpoch = -1;
    public int pendingPolicySourceIndex = -1;
    public float pendingPolicyReceivedTimeSeconds;
    // Effective pending expiration threshold used by Scheduler.
    // This value represents pendingPolicyMaxAgeSeconds.
    // It is NOT the AI policy_lifetime returned inside a policy response.
    // policy_lifetime describes execution duration;
    // this value describes how long pending_next_policy can wait before expiring.
    public float pendingPolicyEffectiveLifetimeSeconds = 0f;
    public float pendingPolicyCreatedTime = -1f;
    public float pendingPolicyAgeSeconds;
    public string pendingPolicyDiscardReason = string.Empty;

    // Keeps the most recent terminal pending slot for debug after consume, discard, or replacement.
    [Header("Last Pending Policy History")]
    public string lastPendingPolicyId = string.Empty;
    public string lastPendingPolicySource = string.Empty;
    public string lastPendingPolicyPriority = string.Empty;
    public int lastPendingPolicySourceStateEpoch = -1;
    public int lastPendingPolicySourceIndex = -1;
    public string lastPendingPolicyDiscardReason = string.Empty;

    [System.NonSerialized]
    private CP_PolicyResponse pendingNextPolicy;

    private bool hasObservedState;
    private int nextGenerationJobId;
    private bool aiPolicyReady;
    private bool motionGroupFinished;
    private bool motionExecutorCallbacksRegistered;
    private bool runtimeInitialized;
    private bool reconciliationRequiredAfterEnable;
    private bool bootstrapRecoveryRequiredAfterEnable;
    public bool AiPolicyReady => aiPolicyReady;
    public bool MotionGroupFinished => motionGroupFinished;
    public bool PendingPayloadPresent => pendingNextPolicy != null;

    private void OnValidate()
    {
        NormalizeInterruptionMode();
        maxPendingPolicyCount = 1;
        requestRetryDelaySeconds = Mathf.Max(0.1f, requestRetryDelaySeconds);
        pendingPolicyMaxAgeSeconds = Mathf.Max(0.1f, pendingPolicyMaxAgeSeconds);
    }

    private void ResetRuntimeSchedulerState()
    {
        ClearRequestIntent();
        currentAiGenerationJobId = -1;
        activeJobStateEpoch = -1;
        activeJobPolicyIndex = -1;
        activeJobRole = string.Empty;

        currentPolicyIndex = 0;
        shutdownLatched = false;
        policyErrorActive = false;
        pipelineStarted = false;
        pipelineRestartRequired = false;
        activeMotionGroupId = -1;
        lastCompletedMotionGroupId = -1;
        lastInterruptedMotionGroupId = -1;
        lastInterruptedMotionGroupReason = string.Empty;
        policyErrorRetryDelaySeconds = Mathf.Max(0.1f, policyErrorRetryDelaySeconds);
        policyErrorRecoveryPending = false;
        policyErrorRetryAtTime = 0f;
        policyErrorRetryRemainingSeconds = 0f;
        ResetBarrierState();

        pendingSlotOccupied = false;
        pendingNextPolicy = null;
        pendingNextPolicyStatus = "empty";
        pendingPolicyId = string.Empty;
        pendingPolicySource = string.Empty;
        pendingPolicyPriority = string.Empty;
        pendingPolicySourceStateEpoch = -1;
        pendingPolicySourceIndex = -1;
        pendingPolicyReceivedTimeSeconds = 0f;
        pendingPolicyEffectiveLifetimeSeconds = 0f;
        pendingPolicyCreatedTime = -1f;
        pendingPolicyAgeSeconds = 0f;
        pendingPolicyDiscardReason = string.Empty;

        lastSchedulerWarning = "Scheduler runtime state reset.";
        lastPendingPolicyId = string.Empty;
        lastPendingPolicySource = string.Empty;
        lastPendingPolicyPriority = string.Empty;
        lastPendingPolicySourceStateEpoch = -1;
        lastPendingPolicySourceIndex = -1;
        lastPendingPolicyDiscardReason = string.Empty;
    }

    private void Start()
    {
        NormalizeInterruptionMode();
        ResetRuntimeSchedulerState();
        runtimeInitialized = false;
        reconciliationRequiredAfterEnable = false;
        bootstrapRecoveryRequiredAfterEnable = false;
        if (pythonPolicyClient != null)
        {
            pythonPolicyClient.policyScheduler = this;
            pythonPolicyClient.schedulerControlsRequestTiming = true;
        }
        if (policyReceiver != null)
        {
            policyReceiver.policyScheduler = this;
            policyReceiver.schedulerOwnershipEnabled = schedulerEnabled;
        }
        if (policyReceiver != null && policyReceiver.legalityChecker != null)
        {
            policyReceiver.legalityChecker.policyReceiver = policyReceiver;
        }
        RefreshAuthoritativeState();
        schedulerState = schedulerEnabled ? "armed" : "disabled";
        runtimeInitialized = true;
        if (!requestPending && currentAiGenerationJobId < 0 && !IsTransportInFlight())
        {
            QueueRequest("initial_policy", "runtime_start");
        }
    }

    private void OnEnable()
    {
        RegisterMotionExecutorCallbacks();
        if (policyReceiver != null)
        {
            policyReceiver.schedulerOwnershipEnabled = schedulerEnabled && !shutdownLatched;
        }
        if (!Application.isPlaying || !runtimeInitialized || !reconciliationRequiredAfterEnable || !schedulerEnabled || shutdownLatched)
        {
            return;
        }

        ReconcileMotionStateAfterReenable();
        reconciliationRequiredAfterEnable = false;
    }

    private void OnDisable()
    {
        UnregisterMotionExecutorCallbacks();
        if (policyReceiver != null)
        {
            policyReceiver.schedulerOwnershipEnabled = false;
        }
        ClearRequestIntent();
        ClearActiveJobTracking();
        policyErrorRecoveryPending = false;
        policyErrorRetryAtTime = 0f;
        policyErrorRetryRemainingSeconds = 0f;
        if (Application.isPlaying && runtimeInitialized)
        {
            reconciliationRequiredAfterEnable = true;
            bootstrapRecoveryRequiredAfterEnable = !pipelineStarted;
        }
        schedulerState = "disabled";
        if (defaultAttentionController != null)
        {
            defaultAttentionController.SetSchedulerState(false, false, "disabled");
        }
    }

    private void OnDestroy()
    {
        UnregisterMotionExecutorCallbacks();
    }

    private void Update()
    {
        policyErrorRetryRemainingSeconds = policyErrorRecoveryPending
            ? Mathf.Max(0f, policyErrorRetryAtTime - Time.time)
            : 0f;

        if (perceptionBuilder != null && perceptionBuilder.shutdownSignal && !shutdownLatched)
        {
            EnterShutdownState("local_shutdown_signal");
        }

        if (!schedulerEnabled)
        {
            ClearRequestIntent();
            ClearActiveJobTracking();
            schedulerState = "disabled";
            policyErrorRecoveryPending = false;
            policyErrorRetryAtTime = 0f;
            policyErrorRetryRemainingSeconds = 0f;
            if (policyReceiver != null)
            {
                policyReceiver.schedulerOwnershipEnabled = false;
            }
            if (defaultAttentionController != null)
            {
                defaultAttentionController.SetSchedulerState(false, false, "disabled");
            }
            return;
        }

        if (shutdownLatched)
        {
            if (policyReceiver != null)
            {
                policyReceiver.schedulerOwnershipEnabled = true;
            }
            UpdateDefaultAttention();
            return;
        }

        if (policyReceiver != null)
        {
            policyReceiver.schedulerOwnershipEnabled = true;
        }
        if (policyReceiver != null && policyReceiver.legalityChecker != null)
        {
            policyReceiver.legalityChecker.policyReceiver = policyReceiver;
        }
        RefreshAuthoritativeState();
        RefreshPendingPolicyAgeDebug();
        if (!ValidatePendingSlotConsistency())
        {
            UpdateDefaultAttention();
            return;
        }

        if (HasOccupiedPendingSlot() && IsPendingPolicyExpired())
        {
            InvalidatePendingPolicy("pending_timeout");
            if (policyErrorActive)
            {
                lastSchedulerWarning = "Pending policy expired while policy_error recovery is active.";
                SchedulePolicyErrorRecovery();
                schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
            }
            else
            {
                QueueRequest(GetRecoveryRequestRole(), CurrentStateRequiresTransition() ? "transition_requirement" : "pending_timeout_recovery");
                schedulerState = "waiting_for_policy_recovery";
                lastSchedulerWarning = "Pending recovery generated new current_policy request.";
            }
            UpdateDefaultAttention();
            return;
        }

        bool busy = motionExecutor != null && motionExecutor.IsBusy;
        if (!busy && HasOccupiedPendingSlot())
        {
            TryAdvanceStage();
            busy = motionExecutor != null && motionExecutor.IsBusy;
        }

        if (policyErrorActive && policyErrorRecoveryPending && Time.time >= policyErrorRetryAtTime)
        {
            if (!requestPending && currentAiGenerationJobId < 0 && !IsTransportInFlight() && !HasOccupiedPendingSlot())
            {
                QueueRequest(GetRecoveryRequestRole(), "policy_error_recovery");
                policyErrorRecoveryPending = false;
                policyErrorRetryAtTime = 0f;
                policyErrorRetryRemainingSeconds = 0f;
                schedulerState = "policy_error_recovery_request_queued";
            }
        }

        if (policyErrorActive &&
            policyErrorRecoveryPending &&
            CurrentStateRequiresTransition() &&
            !requestPending &&
            currentAiGenerationJobId < 0 &&
            !IsTransportInFlight() &&
            !HasOccupiedPendingSlot())
        {
            QueueRequest("state_change_transition", "policy_error_recovery", true);
            policyErrorRecoveryPending = false;
            policyErrorRetryAtTime = 0f;
            policyErrorRetryRemainingSeconds = 0f;
            schedulerState = "policy_error_transition_recovery_queued";
        }

        if (!policyErrorActive && !policyErrorRecoveryPending && pipelineStarted && !pipelineRestartRequired && busy && allowLookaheadWhileMotionBusy && !HasOccupiedPendingSlot() && !requestPending && currentAiGenerationJobId < 0)
        {
            bool requiresEntryTransition = CurrentStateRequiresTransition();
            // Re-requesting the same unfinished entry transition while it is already
            // executing creates duplicate transition groups and makes F2/F3 entry
            // appear like free movement. Let the active transition finish first.
            if (requiresEntryTransition && IsExecutingEntryTransitionForCurrentContext())
            {
                schedulerState = "entry_transition_running";
            }
            else
            {
                QueueRequest(requiresEntryTransition ? "state_change_transition" : "lookahead_next_policy", requiresEntryTransition ? "transition_requirement" : "motion_group_running");
            }
        }
        else if (!policyErrorActive && !policyErrorRecoveryPending && pipelineStarted && !pipelineRestartRequired && !busy && !HasOccupiedPendingSlot() && !requestPending && currentAiGenerationJobId < 0)
        {
            QueueRequest(GetRecoveryRequestRole(), CurrentStateRequiresTransition() ? "transition_requirement" : "no_executable_policy");
        }

        if (bootstrapRecoveryRequiredAfterEnable &&
            !pipelineStarted &&
            !policyErrorActive &&
            !requestPending &&
            currentAiGenerationJobId < 0 &&
            !IsTransportInFlight() &&
            !HasOccupiedPendingSlot())
        {
            QueueRequest("initial_policy", "scheduler_reenabled_before_bootstrap");
            bootstrapRecoveryRequiredAfterEnable = false;
            schedulerState = "reenabled_before_bootstrap";
        }

        ProcessRequestIntent();
        UpdateDefaultAttention();
    }

    public void RequestManualPolicy()
    {
        if (!isActiveAndEnabled || !schedulerEnabled || shutdownLatched)
        {
            return;
        }

        if (policyErrorActive)
        {
            if (!requestPending && currentAiGenerationJobId < 0 && !IsTransportInFlight() && !HasOccupiedPendingSlot())
            {
                policyErrorRecoveryPending = false;
                policyErrorRetryAtTime = 0f;
                policyErrorRetryRemainingSeconds = 0f;
                QueueRequest(GetRecoveryRequestRole(), "manual_policy_error_recovery", true);
                schedulerState = "policy_error_manual_recovery_queued";
            }
            return;
        }

        QueueRequest("manual_current_policy", "manual_request");
    }

    public void ReceivePolicyJsonFromPython(string policyJson, int generationJobId, string requestRole)
    {
        lastResponseRole = requestRole ?? string.Empty;
        if (!isActiveAndEnabled || !schedulerEnabled)
        {
            RecordDiscard("scheduler_component_disabled", generationJobId);
            return;
        }
        if (shutdownLatched || generationJobId != currentAiGenerationJobId)
        {
            RecordDiscard(shutdownLatched ? "shutdown_latched" : "stale_generation_job", generationJobId);
            return;
        }
        if (policyReceiver == null)
        {
            schedulerState = "missing_policy_receiver";
            lastSchedulerWarning = "Policy receiver is not assigned.";
            return;
        }

        string completedJobRole = string.IsNullOrWhiteSpace(activeJobRole) ? "current_policy" : activeJobRole;
        int completedJobEpoch = activeJobStateEpoch;
        int completedJobPolicyIndex = activeJobPolicyIndex;
        ClearActiveJobTracking();

        if (!policyReceiver.TryPreparePolicyJson(policyJson, out CP_PolicyResponse policy, out string preparationReason))
        {
            if (IsParseFailure(policy))
            {
                schedulerState = "parse_failed";
                lastSchedulerWarning = GetResponseFailureReason(policy, preparationReason);
                if (policyErrorActive)
                {
                    HandlePolicyErrorRecoveryFailure(lastSchedulerWarning);
                }
                else
                {
                    QueueRequest(completedJobRole, "parse_failure_retry");
                }
                return;
            }

            if (IsShutdown(policy))
            {
                policyReceiver.TryExecutePreparedPolicy(policy, out string shutdownReason);
                EnterShutdownState(!string.IsNullOrWhiteSpace(shutdownReason)
                    ? shutdownReason
                    : GetResponseFailureReason(policy, preparationReason));
                return;
            }

            if (IsPolicyError(policy))
            {
                policyReceiver.TryExecutePreparedPolicy(policy, out string policyErrorReason);
                policyErrorActive = true;
                ClearRequestIntent();
                InvalidatePendingPolicy("policy_error_invalidation");
                ResetBarrierState();
                pipelineRestartRequired = pipelineStarted;
                schedulerState = "policy_error";
                lastSchedulerWarning = !string.IsNullOrWhiteSpace(policyErrorReason)
                    ? policyErrorReason
                    : GetResponseFailureReason(policy, preparationReason);
                if (logScheduler)
                {
                    Debug.Log("CP_PolicyScheduler policy_error entered.", this);
                }
                SchedulePolicyErrorRecovery();
                return;
            }

            if (IsNonExecutable(policy))
            {
                policyReceiver.TryExecutePreparedPolicy(policy, out string nonExecutableReason);
                schedulerState = "non_executable_response";
                lastSchedulerWarning = !string.IsNullOrWhiteSpace(nonExecutableReason)
                    ? nonExecutableReason
                    : GetResponseFailureReason(policy, preparationReason);
                if (policyErrorActive)
                {
                    HandlePolicyErrorRecoveryFailure(lastSchedulerWarning);
                }
            }
            else
            {
                schedulerState = "response_rejected";
                lastSchedulerWarning = preparationReason;
                if (policyErrorActive)
                {
                    HandlePolicyErrorRecoveryFailure(lastSchedulerWarning);
                }
                else if (ShouldRetryPreparationFailure(preparationReason))
                {
                    QueueRequest(completedJobRole, "response_rejected_retry");
                }
            }
            return;
        }

        if (!IsResponseFreshForCurrentState(policy, completedJobEpoch, completedJobPolicyIndex, completedJobRole, out string freshnessReason))
        {
            RecordDiscard(freshnessReason, generationJobId);
            if (policyErrorActive)
            {
                HandlePolicyErrorRecoveryFailure(freshnessReason);
            }
            else
            {
                QueueRequest("current_policy", "stale_response_replacement");
            }
            return;
        }

        if (!pipelineStarted)
        {
            if (!TryStartBootstrapPolicy(policy, out string bootstrapFailureReason))
            {
                schedulerState = "bootstrap_execution_rejected";
                lastSchedulerWarning = bootstrapFailureReason;
                if (policyErrorActive)
                {
                    HandlePolicyErrorRecoveryFailure(bootstrapFailureReason);
                }
                else
                {
                    QueueRequest("initial_policy", "bootstrap_execution_failed");
                }
            }
            return;
        }

        if (pipelineRestartRequired || policyErrorActive)
        {
            if (!TryStartRestartPolicy(policy, out string restartFailureReason))
            {
                schedulerState = "restart_execution_rejected";
                lastSchedulerWarning = restartFailureReason;
                if (policyErrorActive)
                {
                    HandlePolicyErrorRecoveryFailure(restartFailureReason);
                }
                else
                {
                    QueueRequest("current_policy", "restart_execution_failed");
                }
            }
            return;
        }

        StorePendingPolicy(policy);
    }

    public void NotifyPythonRequestFailed(string requestRole, int generationJobId, string errorText)
    {
        if (!isActiveAndEnabled || !schedulerEnabled)
        {
            if (policyReceiver != null)
            {
                policyReceiver.schedulerOwnershipEnabled = false;
            }
            lastSchedulerWarning = "job=" + generationJobId + ": scheduler_component_disabled";
            schedulerState = "disabled";
            return;
        }
        if (shutdownLatched || generationJobId != currentAiGenerationJobId)
        {
            return;
        }

        string retryRole = string.IsNullOrWhiteSpace(requestRole)
            ? (string.IsNullOrWhiteSpace(activeJobRole) ? "current_policy" : activeJobRole)
            : requestRole;

        ClearActiveJobTracking();
        lastResponseRole = retryRole;
        lastSchedulerWarning = errorText ?? "Python request failed.";
        schedulerState = "waiting_retry";
        if (policyErrorActive)
        {
            HandlePolicyErrorRecoveryFailure(lastSchedulerWarning);
            return;
        }

        QueueRequest(retryRole, "transport_retry");
    }

    private void RefreshAuthoritativeState()
    {
        if (perceptionBuilder == null)
        {
            schedulerState = "missing_perception_builder";
            return;
        }
        CP_PerceptionPacket packet = perceptionBuilder.RefreshPerception();
        string observed = packet != null && packet.companion != null ? packet.companion.current_state : CP_StateNames.Unknown;
        string observedExecutionContext = DeriveExecutionContextKey(packet, observed);
        if (!hasObservedState)
        {
            currentState = observed;
            previousState = observed;
            currentExecutionContextKey = observedExecutionContext;
            previousExecutionContextKey = observedExecutionContext;
            hasObservedState = true;
        }

        bool legacyStateChanged = observed != currentState;
        if (legacyStateChanged)
        {
            previousState = currentState;
            currentState = observed;
            stateTransitionLabel = previousState + "->" + currentState;
        }

        if (observedExecutionContext != currentExecutionContextKey)
        {
            previousExecutionContextKey = currentExecutionContextKey;
            currentExecutionContextKey = observedExecutionContext;
            currentStateEpoch++;
            lastExecutionContextChangeReason = BuildExecutionContextChangeReason(packet, previousExecutionContextKey, currentExecutionContextKey, legacyStateChanged);
            HandleStateChange();
        }
        SetPerceptionSchedulingContext();
    }

    private void HandleStateChange()
    {
        if (shutdownLatched)
        {
            return;
        }

        InvalidatePendingPolicy("state_change_invalidation");
        // Late transport callbacks retain their old job id and therefore fail the active-job comparison.
        ClearActiveJobTracking();
        ClearRequestIntent();
        ResetBarrierState();
        pipelineRestartRequired = false;

        if (policyErrorActive)
        {
            pipelineRestartRequired = pipelineStarted;
            motionGroupFinished = false;
            RefreshBarrierState();
            lastSchedulerWarning = "Execution context changed while policy_error recovery is active.";
            policyErrorRecoveryPending = false;
            policyErrorRetryAtTime = 0f;
            policyErrorRetryRemainingSeconds = 0f;
            QueueRequest(GetRecoveryRequestRole(), "policy_error_recovery", true);
            schedulerState = "policy_error_context_changed";
            return;
        }

        NormalizeInterruptionMode();
        bool requiresEntryTransition = CurrentStateRequiresTransition();
        bool legalityRequiresInterrupt = ShouldForceHardInterruptForStateChange(out string forceReason);
        bool requiresHardInterrupt = legalityRequiresInterrupt || stateChangeInterruptionMode == HardInterruptToTransition;
        bool busy = motionExecutor != null && motionExecutor.IsBusy;

        if (busy)
        {
            if (requiresHardInterrupt)
            {
                forcedHardInterruptReason = string.IsNullOrEmpty(forceReason) ? "configured_hard_interrupt" : forceReason;
                pipelineRestartRequired = true;
                motionExecutor.AbortCurrentAndClearQueue("state_change_hard_interrupt: " + forcedHardInterruptReason);
                lastInterruptionType = "hard_interrupt";
            }
            else
            {
                motionExecutor.ClearPendingPrimitivesKeepCurrent("state_change_soft_finish");
                lastInterruptionType = SoftFinishCurrentPrimitive;
                forcedHardInterruptReason = string.Empty;
            }
        }
        else
        {
            motionGroupFinished = !pipelineRestartRequired;
            if (requiresHardInterrupt)
            {
                pipelineRestartRequired = true;
                motionGroupFinished = false;
                forcedHardInterruptReason = string.IsNullOrEmpty(forceReason) ? "configured_hard_interrupt" : forceReason;
                lastInterruptionType = "hard_interrupt";
            }
            else
            {
                lastInterruptionType = string.Empty;
                forcedHardInterruptReason = string.Empty;
            }
        }

        RefreshBarrierState();
        // State change invalidates previous policy context.
        // Even without an entry transition requirement,
        // the next policy must be generated as current_policy,
        // not lookahead_next_policy.
        // Lookahead is only valid when state context remains unchanged.
        string stateChangeRequestRole = requiresEntryTransition ? "state_change_transition" : "current_policy";
        string stateChangeRequestReason = requiresEntryTransition ? "state_change_new_authoritative_state" : "state_change_context_refresh";
        QueueRequest(stateChangeRequestRole, stateChangeRequestReason);
        schedulerState = "state_changed";
    }

    // QueueRequest does not send requests.
    // It only stores the latest valid request intent.
    // ProcessRequestIntent remains the only function that creates AI generation jobs.
    private void QueueRequest(string role, string reason, bool immediateDispatch = false)
    {
        if (!isActiveAndEnabled || !schedulerEnabled)
        {
            lastSchedulerWarning = "Request ignored because scheduler is disabled.";
            return;
        }
        if (shutdownLatched)
        {
            lastSchedulerWarning = "Request ignored because shutdown is latched.";
            return;
        }

        if (policyErrorActive && !IsPolicyErrorRecoveryRequest(role, reason))
        {
            lastSchedulerWarning = "Request ignored because policy_error recovery owns scheduling.";
            return;
        }

        if (string.IsNullOrWhiteSpace(role))
        {
            role = "current_policy";
        }

        if (requestPending)
        {
            if (!ShouldReplacePendingRequest(pendingRequestRole, pendingRequestReason, role, reason))
            {
                int existingPriority = GetRequestPriority(pendingRequestRole, pendingRequestReason);
                int newPriority = GetRequestPriority(role, reason);
                lastSchedulerWarning =
                    "Request retained. Existing request="
                    + pendingRequestRole
                    + " reason="
                    + pendingRequestReason
                    + " priority="
                    + existingPriority
                    + " blocked new request="
                    + role
                    + " reason="
                    + (reason ?? string.Empty)
                    + " priority="
                    + newPriority;
                return;
            }
        }

        requestPending = true;
        pendingRequestRole = role;
        pendingRequestEpoch = currentStateEpoch;
        pendingRequestReason = reason ?? string.Empty;
        // Immediate dispatch is only used for scheduler self-recovery.
        // It does not bypass request priority.
        // It only removes the retry delay because the previous policy context
        // has already been invalidated and a new valid policy is required.
        if (immediateDispatch)
        {
            retryAtTime = Time.time;
        }
        else
        {
            retryAtTime = Time.time + Mathf.Max(0.1f, requestRetryDelaySeconds);
        }

        if (logScheduler)
        {
            Debug.Log(
                $"QueueRequest role={role}, reason={reason}, " +
                $"time={Time.time}, retryAt={retryAtTime}",
                this
            );
        }
    }

    private void HandleMotionGroupStarted(int groupId)
    {
        if (!isActiveAndEnabled || !schedulerEnabled || shutdownLatched || groupId < 0)
        {
            return;
        }

        activeMotionGroupId = groupId;
        motionGroupFinished = false;
        RefreshBarrierState();
    }

    private void HandleMotionGroupCompleted(int groupId)
    {
        if (!isActiveAndEnabled || !schedulerEnabled || shutdownLatched || groupId < 0 || groupId != activeMotionGroupId)
        {
            return;
        }

        activeMotionGroupId = -1;
        lastCompletedMotionGroupId = groupId;
        motionGroupFinished = true;
        RefreshBarrierState();
        TryAdvanceStage();
    }

    private void HandleMotionGroupInterrupted(int groupId, string reason)
    {
        if (!isActiveAndEnabled || groupId < 0 || groupId != activeMotionGroupId)
        {
            return;
        }

        activeMotionGroupId = -1;
        lastInterruptedMotionGroupId = groupId;
        lastInterruptedMotionGroupReason = reason ?? string.Empty;
        RecoverFromTerminalMotionInterruption(
            "motion_group_interrupted",
            "restart_required_after_motion_interruption",
            string.Empty);
    }

    private void ReconcileMotionStateAfterReenable()
    {
        if (motionExecutor == null)
        {
            lastSchedulerWarning = "Scheduler re-enable reconciliation skipped because MotionExecutor is missing.";
            return;
        }

        if (shutdownLatched)
        {
            schedulerState = "shutdown";
            return;
        }

        if (policyErrorActive)
        {
            bootstrapRecoveryRequiredAfterEnable = false;
            pipelineRestartRequired = pipelineStarted;
            motionGroupFinished = false;
            RefreshBarrierState();

            if (automaticallyRetryPolicyError)
            {
                SchedulePolicyErrorRecovery();
            }
            else
            {
                policyErrorRecoveryPending = false;
                policyErrorRetryAtTime = 0f;
                policyErrorRetryRemainingSeconds = 0f;
                schedulerState = "policy_error";
            }

            if (logScheduler)
            {
                Debug.Log(
                    "CP_PolicyScheduler restored policy_error recovery after re-enable.",
                    this
                );
            }

            return;
        }

        int trackedGroupId = activeMotionGroupId;
        bool executorBusy = motionExecutor.IsBusy;
        int executorActiveGroupId = motionExecutor.ActiveMotionGroupId;
        int completedGroupId = motionExecutor.LastCompletedMotionGroupId;
        int interruptedGroupId = motionExecutor.LastInterruptedMotionGroupId;

        if (!pipelineStarted &&
            currentAiGenerationJobId < 0 &&
            !requestPending &&
            !IsTransportInFlight() &&
            !HasOccupiedPendingSlot())
        {
            bootstrapRecoveryRequiredAfterEnable = true;
        }

        if (executorBusy && executorActiveGroupId >= 0)
        {
            if (trackedGroupId >= 0 && trackedGroupId != executorActiveGroupId)
            {
                lastSchedulerWarning = $"Scheduler re-enabled while Motion Group remained active. Tracked={trackedGroupId}, executor={executorActiveGroupId}.";
            }

            activeMotionGroupId = executorActiveGroupId;
            motionGroupFinished = false;
            RefreshBarrierState();
            schedulerState = "reenabled_tracking_active_motion";
            return;
        }

        if (trackedGroupId >= 0 && !executorBusy && completedGroupId == trackedGroupId)
        {
            lastCompletedMotionGroupId = trackedGroupId;
            activeMotionGroupId = -1;
            motionGroupFinished = true;
            RefreshBarrierState();
            schedulerState = "reenabled_motion_completed";
            lastSchedulerWarning = "Scheduler reconciled Motion Group completion missed while disabled.";
            return;
        }

        if (trackedGroupId >= 0 && !executorBusy && interruptedGroupId == trackedGroupId)
        {
            lastInterruptedMotionGroupId = trackedGroupId;
            lastInterruptedMotionGroupReason = string.IsNullOrWhiteSpace(motionExecutor.latestInterruptionReason)
                ? "scheduler_reenable_motion_interrupted"
                : motionExecutor.latestInterruptionReason;
            activeMotionGroupId = -1;
            RecoverFromTerminalMotionInterruption(
                "scheduler_reenable_motion_interrupted",
                "reenabled_restart_required",
                "Scheduler reconciled Motion Group interruption missed while disabled.");
            return;
        }

        if (trackedGroupId >= 0 && !executorBusy)
        {
            activeMotionGroupId = -1;
            RecoverFromTerminalMotionInterruption(
                "scheduler_reenable_motion_result_unknown",
                "reenabled_restart_required",
                "scheduler_reenable_motion_result_unknown");
            return;
        }

        if (activeMotionGroupId < 0 && !executorBusy && pipelineStarted && !pipelineRestartRequired)
        {
            schedulerState = HasValidPendingForCurrentEpoch()
                ? "pending_next_policy_ready"
                : schedulerState;
        }
    }

    private void TryAdvanceStage()
    {
        RefreshBarrierState();
        if (!isActiveAndEnabled || !schedulerEnabled || shutdownLatched || !pipelineStarted || pipelineRestartRequired || barrierCommitInProgress || !barrierReady)
        {
            return;
        }

        if (!TryCommitPendingPolicy(out string failureReason))
        {
            lastSchedulerWarning = failureReason;
        }
    }

    private void RegisterMotionExecutorCallbacks()
    {
        if (motionExecutor == null || motionExecutorCallbacksRegistered)
        {
            return;
        }

        motionExecutor.OnMotionGroupStarted += HandleMotionGroupStarted;
        motionExecutor.OnMotionGroupCompleted += HandleMotionGroupCompleted;
        motionExecutor.OnMotionGroupInterrupted += HandleMotionGroupInterrupted;
        motionExecutorCallbacksRegistered = true;
    }

    private void UnregisterMotionExecutorCallbacks()
    {
        if (motionExecutor == null || !motionExecutorCallbacksRegistered)
        {
            return;
        }

        motionExecutor.OnMotionGroupStarted -= HandleMotionGroupStarted;
        motionExecutor.OnMotionGroupCompleted -= HandleMotionGroupCompleted;
        motionExecutor.OnMotionGroupInterrupted -= HandleMotionGroupInterrupted;
        motionExecutorCallbacksRegistered = false;
    }

    private void ProcessRequestIntent()
    {
        if (!isActiveAndEnabled || !schedulerEnabled || shutdownLatched)
        {
            return;
        }

        if (!requestPending || currentAiGenerationJobId >= 0 || IsTransportInFlight() || Time.time < retryAtTime)
        {
            return;
        }

        if (policyErrorActive && !IsPolicyErrorRecoveryRequest(pendingRequestRole, pendingRequestReason))
        {
            lastSchedulerWarning = "Non-recovery request intent discarded during policy_error.";
            ClearRequestIntent();
            schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
            return;
        }

        if (pendingRequestEpoch != currentStateEpoch)
        {
            ClearRequestIntent();
            if (policyErrorActive)
            {
                HandlePolicyErrorRecoveryFailure("policy_error_recovery_epoch_stale");
            }
            return;
        }
        if (HasValidPendingForCurrentEpoch())
        {
            if (policyErrorActive)
            {
                InvalidatePendingPolicy("policy_error_recovery_pending_conflict");
                ClearRequestIntent();
                HandlePolicyErrorRecoveryFailure("policy_error_recovery_pending_conflict");
                return;
            }

            ClearRequestIntent();
            return;
        }
        if (pythonPolicyClient == null || perceptionBuilder == null)
        {
            string missingReason = pythonPolicyClient == null
                ? "python_policy_client_missing"
                : "perception_builder_missing";

            lastSchedulerWarning = missingReason;
            if (policyErrorActive)
            {
                ClearRequestIntent();
                HandlePolicyErrorRecoveryFailure(missingReason);
                return;
            }

            schedulerState = "missing_transport_reference";
            lastSchedulerWarning = missingReason;
            return;
        }

        currentAiGenerationJobId = ++nextGenerationJobId;
        activeJobStateEpoch = currentStateEpoch;
        activeJobPolicyIndex = currentPolicyIndex;
        activeJobRole = pendingRequestRole;
        lastRequestRole = activeJobRole;
        SetPerceptionSchedulingContext();
        if (pythonPolicyClient.SendSchedulerRequest(activeJobRole, currentAiGenerationJobId))
        {
            ClearRequestIntent();
            schedulerState = "waiting_for_response";
        }
        else
        {
            string failedRole = activeJobRole;
            string failedReason = pythonPolicyClient != null && !string.IsNullOrWhiteSpace(pythonPolicyClient.latestErrorText)
                ? pythonPolicyClient.latestErrorText
                : "scheduler_request_start_failed";

            ClearActiveJobTracking();
            ClearRequestIntent();
            lastSchedulerWarning = failedReason;

            if (policyErrorActive)
            {
                if (logScheduler)
                {
                    Debug.Log("CP_PolicyScheduler recovery transport start failed.", this);
                }
                HandlePolicyErrorRecoveryFailure(failedReason);
                return;
            }

            QueueRequest(string.IsNullOrWhiteSpace(failedRole) ? "current_policy" : failedRole, "transport_start_retry");
            schedulerState = "waiting_retry";
        }
    }

    private void SetPerceptionSchedulingContext()
    {
        if (perceptionBuilder != null)
        {
            string role = currentAiGenerationJobId >= 0 ? activeJobRole : (requestPending ? pendingRequestRole : lastRequestRole);
            perceptionBuilder.SetSchedulingContext(role, currentPolicyIndex, currentStateEpoch, schedulerMode, allowLookaheadWhileMotionBusy, stateChangeInterruptionMode, stateTransitionLabel);
        }
    }

    private bool IsResponseFreshForCurrentState(CP_PolicyResponse policy, int expectedStateEpoch, int expectedPolicyIndex, string expectedRole, out string reason)
    {
        reason = string.Empty;
        if (policy == null || policy.debug == null)
        {
            reason = "response_debug_missing";
            return false;
        }
        if (policy.debug.source_state_epoch < 0 || policy.debug.source_policy_index < 0 || string.IsNullOrWhiteSpace(policy.debug.request_role))
        {
            reason = "response_scheduling_metadata_missing";
            return false;
        }
        if (policy.debug.source_state_epoch != currentStateEpoch || policy.debug.source_state_epoch != expectedStateEpoch)
        {
            reason = "response_state_epoch_mismatch";
            return false;
        }
        if (policy.debug.source_policy_index != expectedPolicyIndex)
        {
            reason = "response_policy_index_mismatch";
            return false;
        }
        if (policy.debug.request_role != expectedRole || !MatchesCurrentState(policy))
        {
            reason = "response_role_or_state_mismatch";
            return false;
        }
        return true;
    }

    private void StorePendingPolicy(CP_PolicyResponse policy)
    {
        if (HasOccupiedPendingSlot())
        {
            if (!HasValidPendingForCurrentEpoch())
            {
                InvalidatePendingPolicy("existing_pending_stale");
            }
            else if (HasPendingPayload() && IsTransitionPriority(policy) && !IsTransitionPriority(pendingNextPolicy))
            {
                // A higher-priority transition policy is allowed to replace a normal pending slot.
                InvalidatePendingPolicy("replaced_by_transition_priority");
            }
            else
            {
                pendingPolicyDiscardReason = IsTransitionPriority(policy)
                    ? "new_transition_priority_rejected_existing_valid_pending"
                    : "new_response_rejected_existing_valid_pending";
                lastSchedulerWarning = pendingPolicyDiscardReason;
                schedulerState = "pending_slot_retained";
                return;
            }
        }

        pendingNextPolicy = policy;
        pendingPolicyId = policy.policy != null ? policy.policy.policy_id : string.Empty;
        pendingPolicySource = policy.policy != null ? policy.policy.policy_source : string.Empty;
        pendingPolicyPriority = IsTransitionPriority(policy) ? "transition_priority" : "normal";
        pendingPolicySourceStateEpoch = policy.debug.source_state_epoch;
        pendingPolicySourceIndex = policy.debug.source_policy_index;
        pendingPolicyReceivedTimeSeconds = Time.time;
        pendingPolicyEffectiveLifetimeSeconds = GetPendingExpirationThresholdSeconds();
        pendingPolicyCreatedTime = Time.time;
        pendingPolicyAgeSeconds = 0f;
        pendingPolicyDiscardReason = string.Empty;
        pendingNextPolicyStatus = "ready";
        pendingSlotOccupied = true;
        aiPolicyReady = true;
        RefreshBarrierState();
        schedulerState = "pending_next_policy_ready";
        TryAdvanceStage();
    }

    private bool TryCommitPendingPolicy(out string failureReason)
    {
        failureReason = string.Empty;
        barrierCommitInProgress = true;
        RefreshBarrierState();

        if (HasOccupiedPendingSlot() && IsPendingPolicyExpired())
        {
            InvalidatePendingPolicy("pending_timeout");
            if (policyErrorActive)
            {
                lastSchedulerWarning = "Pending policy expired during policy_error recovery.";
                SchedulePolicyErrorRecovery();
                schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
            }
            else
            {
                QueueRequest(GetRecoveryRequestRole(), CurrentStateRequiresTransition() ? "transition_requirement" : "pending_timeout_recovery", true);
                schedulerState = "waiting_for_policy_recovery";
                lastSchedulerWarning = "Pending recovery generated new current_policy request.";
            }
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = "pending_timeout";
            return false;
        }
        if (!HasValidPendingForCurrentEpoch())
        {
            InvalidatePendingPolicy("pending_revalidation_failed");
            ClearRequestIntent();
            if (policyErrorActive)
            {
                lastSchedulerWarning = "Pending policy revalidation failed during policy_error recovery.";
                SchedulePolicyErrorRecovery();
                schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
            }
            else
            {
                QueueRequest(GetRecoveryRequestRole(), CurrentStateRequiresTransition() ? "transition_requirement" : "pending_invalid_replacement", true);
                schedulerState = "waiting_for_policy_recovery";
                lastSchedulerWarning = "Pending recovery generated new current_policy request.";
            }
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = "pending_revalidation_failed";
            return false;
        }
        if (IsNormalPolicy(pendingNextPolicy) && CurrentStateRequiresTransition())
        {
            InvalidatePendingPolicy("pending_normal_policy_blocked_by_entry_transition");
            QueueRequest("state_change_transition", "transition_requirement");
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = "transition_requirement";
            return false;
        }
        if (policyReceiver != null && (IsShutdown(policyReceiver.latestPolicyResponse) || IsPolicyError(policyReceiver.latestPolicyResponse)))
        {
            InvalidatePendingPolicy("shutdown_or_policy_error_active");
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = "shutdown_or_policy_error_active";
            return false;
        }

        if (!HasOccupiedPendingSlot())
        {
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = "pending_slot_empty";
            return false;
        }
        if (!HasPendingPayload())
        {
            RepairOccupiedSlotMissingPayload();
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = "pending_slot_payload_missing";
            return false;
        }

        bool prepared = policyReceiver.TryPreparePolicyJson(JsonUtility.ToJson(pendingNextPolicy, true), out CP_PolicyResponse revalidated, out string reason);
        string pendingReason = string.Empty;
        bool fresh = prepared && IsResponseFreshForPending(revalidated, out pendingReason);
        if (!prepared || !fresh)
        {
            InvalidatePendingPolicy("pending_legality_or_freshness_failed: " + (string.IsNullOrEmpty(pendingReason) ? reason : pendingReason));
            // Pending validation failure means the future policy slot cannot be trusted.
            // Immediately request a new current policy instead of waiting for retry delay.
            QueueRequest(CurrentStateRequiresTransition() ? "state_change_transition" : "current_policy", "pending_revalidation_retry", true);
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = string.IsNullOrEmpty(pendingReason) ? reason : pendingReason;
            return false;
        }

        if (TryLoadPreparedPolicyNow(revalidated, "pending_policy_executed", out string executionFailureReason))
        {
            InvalidatePendingPolicy("consumed");
            currentPolicyIndex++;
            policyErrorActive = false;
            policyErrorRecoveryPending = false;
            policyErrorRetryAtTime = 0f;
            policyErrorRetryRemainingSeconds = 0f;
            ResetBarrierState();
            SetPerceptionSchedulingContext();
            schedulerState = "pending_policy_committed";
            return true;
        }

        if (executionFailureReason == "motion_executor_busy")
        {
            pendingNextPolicyStatus = "ready";
            schedulerState = "pending_execution_deferred";
            lastSchedulerWarning = "Pending policy execution deferred because MotionExecutor became busy again.";
            barrierCommitInProgress = false;
            RefreshBarrierState();
            failureReason = executionFailureReason;
            return false;
        }

        InvalidatePendingPolicy("pending_execution_failed: " + executionFailureReason);
        if (policyErrorActive)
        {
            lastSchedulerWarning = executionFailureReason;
            SchedulePolicyErrorRecovery();
            schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
        }
        else
        {
            QueueRequest(GetRecoveryRequestRole(), CurrentStateRequiresTransition() ? "transition_requirement" : "pending_execution_failed");
        }
        barrierCommitInProgress = false;
        RefreshBarrierState();
        failureReason = executionFailureReason;
        return false;
    }

    private bool IsResponseFreshForPending(CP_PolicyResponse policy, out string reason)
    {
        reason = string.Empty;
        if (policy == null || policy.debug == null || policy.debug.source_state_epoch != currentStateEpoch || policy.debug.source_state_epoch != pendingPolicySourceStateEpoch || policy.debug.source_policy_index != pendingPolicySourceIndex || policy.debug.source_policy_index != currentPolicyIndex || !MatchesCurrentState(policy))
        {
            reason = "pending_response_metadata_mismatch";
            return false;
        }
        return true;
    }

    private bool TryStartBootstrapPolicy(CP_PolicyResponse policy, out string failureReason)
    {
        failureReason = string.Empty;
        if (!CanDirectStartPolicy(policy, out failureReason))
        {
            QueueRequest("state_change_transition", "bootstrap_entry_transition_required", true);
            schedulerState = "bootstrap_transition_required";
            return false;
        }

        if (!TryLoadPreparedPolicyNow(policy, "bootstrap_policy_started", out failureReason))
        {
            return false;
        }

        currentPolicyIndex = 0;
        policyErrorActive = false;
        pipelineStarted = true;
        pipelineRestartRequired = false;
        policyErrorRecoveryPending = false;
        policyErrorRetryAtTime = 0f;
        policyErrorRetryRemainingSeconds = 0f;
        ResetBarrierState();
        SetPerceptionSchedulingContext();
        return true;
    }

    private bool TryStartRestartPolicy(CP_PolicyResponse policy, out string failureReason)
    {
        failureReason = string.Empty;
        if (!CanDirectStartPolicy(policy, out failureReason))
        {
            QueueRequest("state_change_transition", "restart_entry_transition_required", true);
            schedulerState = "restart_transition_required";
            return false;
        }

        if (!TryLoadPreparedPolicyNow(policy, "restart_policy_started", out failureReason))
        {
            return false;
        }

        currentPolicyIndex++;
        policyErrorActive = false;
        pipelineStarted = true;
        pipelineRestartRequired = false;
        policyErrorRecoveryPending = false;
        policyErrorRetryAtTime = 0f;
        policyErrorRetryRemainingSeconds = 0f;
        ResetBarrierState();
        SetPerceptionSchedulingContext();
        return true;
    }

    private bool TryLoadPreparedPolicyNow(CP_PolicyResponse policy, string state, out string failureReason)
    {
        failureReason = string.Empty;
        if (policyReceiver == null)
        {
            failureReason = "policy_receiver_missing";
            return false;
        }
        if (motionExecutor != null && motionExecutor.IsBusy)
        {
            failureReason = "motion_executor_busy";
            return false;
        }
        if (!policyReceiver.TryExecutePreparedPolicy(policy, out failureReason))
        {
            return false;
        }
        schedulerState = state;
        return true;
    }

    private bool HasValidPendingForCurrentEpoch()
    {
        RefreshPendingPolicyAgeDebug();
        return HasOccupiedPendingSlot()
            && HasPendingPayload()
            && pendingNextPolicyStatus == "ready"
            && pendingPolicySourceStateEpoch == currentStateEpoch
            && pendingPolicySourceIndex == currentPolicyIndex
            && MatchesCurrentState(pendingNextPolicy)
            && !IsPendingPolicyExpired();
    }

    private bool CanDirectStartPolicy(CP_PolicyResponse policy, out string reason)
    {
        reason = string.Empty;
        if (policy == null || policy.policy == null)
        {
            reason = "direct_start_policy_missing";
            return false;
        }

        if (IsTransitionPriority(policy))
        {
            return true;
        }

        if (IsNormalPolicy(policy) && CurrentStateRequiresTransition())
        {
            reason = "direct_start_blocked_by_entry_transition";
            return false;
        }

        return true;
    }

    private void EnterShutdownState(string reason)
    {
        if (shutdownLatched)
        {
            return;
        }

        shutdownLatched = true;
        schedulerState = "shutdown";
        lastSchedulerWarning = string.IsNullOrWhiteSpace(reason) ? "shutdown_latched" : reason;
        policyErrorActive = false;
        ClearRequestIntent();
        ClearActiveJobTracking();
        InvalidatePendingPolicy("shutdown_latched");
        ResetBarrierState();
        pipelineRestartRequired = false;
        policyErrorRecoveryPending = false;
        policyErrorRetryAtTime = 0f;
        policyErrorRetryRemainingSeconds = 0f;

        if (motionExecutor != null && motionExecutor.IsBusy)
        {
            motionExecutor.AbortCurrentAndClearQueue("scheduler_shutdown");
        }

        activeMotionGroupId = -1;
        RefreshBarrierState();
    }

    private void SchedulePolicyErrorRecovery()
    {
        if (!policyErrorActive || shutdownLatched)
        {
            policyErrorRecoveryPending = false;
            policyErrorRetryAtTime = 0f;
            policyErrorRetryRemainingSeconds = 0f;
            return;
        }

        if (!automaticallyRetryPolicyError)
        {
            policyErrorRecoveryPending = false;
            policyErrorRetryAtTime = 0f;
            policyErrorRetryRemainingSeconds = 0f;
            schedulerState = "policy_error";
            if (logScheduler)
            {
                Debug.Log("CP_PolicyScheduler automatic policy_error recovery disabled.", this);
            }
            return;
        }

        policyErrorRecoveryPending = true;
        policyErrorRetryAtTime = Time.time + Mathf.Max(0.1f, policyErrorRetryDelaySeconds);
        policyErrorRetryRemainingSeconds = Mathf.Max(0f, policyErrorRetryAtTime - Time.time);
        schedulerState = "policy_error_recovery_wait";
        if (logScheduler)
        {
            Debug.Log("CP_PolicyScheduler policy_error recovery scheduled.", this);
        }
    }

    private void ResetBarrierState()
    {
        aiPolicyReady = false;
        motionGroupFinished = false;
        barrierCommitInProgress = false;
        RefreshBarrierState();
    }

    // A terminal interruption removes the old Motion Group that could have satisfied
    // the previous barrier, so recovery must restart scheduling instead of faking completion.
    private void RecoverFromTerminalMotionInterruption(string reason, string nextSchedulerState, string warningMessage)
    {
        motionGroupFinished = false;
        InvalidatePendingPolicy(reason);
        pipelineRestartRequired = pipelineStarted;
        RefreshBarrierState();
        schedulerState = nextSchedulerState;
        lastSchedulerWarning = string.IsNullOrWhiteSpace(warningMessage) ? reason : warningMessage;
        QueueResumeRequestAfterReenable(reason);
    }

    private void RefreshBarrierState()
    {
        barrierReady = aiPolicyReady && motionGroupFinished;
    }

    private void InvalidatePendingPolicy(string reason)
    {
        string resultingStatus = "discarded";
        if (reason == "consumed")
        {
            resultingStatus = "consumed";
        }
        else if (reason == "pending_timeout")
        {
            resultingStatus = "expired";
        }
        ClearPendingSlotRuntime(reason, resultingStatus);
    }

    // Scheduler recovery requests are higher priority than normal policy requests.
    // They exist because the previous policy context became invalid.
    // Recovery must replace stale intents instead of waiting behind ordinary requests.
    // Request priority controls replacement only.
    // It does NOT create a request queue.
    // Stage4B intentionally keeps one pending request intent.
    // Higher priority requests may replace lower priority requests.
    // Equal priority requests are preserved to avoid unnecessary churn.
    private int GetRequestPriority(string role, string reason)
    {
        string normalizedRole = string.IsNullOrWhiteSpace(role) ? "current_policy" : role;
        string normalizedReason = reason ?? string.Empty;

        if (normalizedReason == "state_change_new_authoritative_state" ||
            normalizedReason == "state_change_context_refresh" ||
            normalizedReason == "transition_requirement" ||
            normalizedReason == "pending_entry_transition_required")
        {
            return 5;
        }

        if (normalizedRole == "state_change_transition" || normalizedRole == "shutdown_transition")
        {
            return 5;
        }

        if (normalizedReason == "pending_timeout_recovery" ||
            normalizedReason == "pending_invalid_replacement" ||
            normalizedReason == "pending_revalidation_retry" ||
            normalizedReason == "policy_error_recovery" ||
            normalizedReason == "manual_policy_error_recovery")
        {
            return 4;
        }

        if (normalizedRole == "lookahead_next_policy" ||
            normalizedReason == "motion_group_running" ||
            normalizedReason == "no_executable_policy")
        {
            return 1;
        }

        if (normalizedRole == "current_policy" || normalizedRole == "manual_current_policy" || normalizedRole == "response_rejected_retry")
        {
            return 3;
        }

        return 3;
    }

    private bool ShouldReplacePendingRequest(string existingRole, string existingReason, string newRole, string newReason)
    {
        int existingPriority = GetRequestPriority(existingRole, existingReason);
        int newPriority = GetRequestPriority(newRole, newReason);

        if (newPriority > existingPriority)
        {
            return true;
        }

        if (newPriority == existingPriority)
        {
            return false;
        }

        return false;
    }

    private bool ShouldForceHardInterruptForStateChange(out string reason)
    {
        reason = string.Empty;
        if (!forceHardInterruptOnIllegalPrimitive)
        {
            return false;
        }
        if (perceptionBuilder != null && perceptionBuilder.shutdownSignal)
        {
            reason = "shutdown_signal";
            return true;
        }
        if (policyReceiver != null && IsPolicyError(policyReceiver.latestPolicyResponse))
        {
            reason = "policy_error_active";
            return true;
        }
        if (policyReceiver != null && policyReceiver.legalityChecker != null && motionExecutor != null && motionExecutor.IsBusy)
        {
            CP_MotionPrimitive currentPrimitive = motionExecutor.CurrentPrimitive;
            if (currentPrimitive == null)
            {
                reason = "current_running_primitive_missing";
                return false;
            }

            if (policyReceiver.legalityChecker.ValidateCurrentExecution(currentState, currentPrimitive, out string legalityReason))
            {
                return false;
            }

            reason = legalityReason;
            return true;
        }
        return false;
    }

    private void RefreshPendingPolicyAgeDebug()
    {
        if (HasOccupiedPendingSlot() && pendingPolicyCreatedTime >= 0f)
        {
            pendingPolicyAgeSeconds = Mathf.Max(0f, Time.time - pendingPolicyCreatedTime);
            return;
        }

        pendingPolicyAgeSeconds = 0f;
    }

    private bool IsPendingPolicyExpired()
    {
        return HasOccupiedPendingSlot()
            && pendingPolicyCreatedTime >= 0f
            && pendingPolicyAgeSeconds > pendingPolicyMaxAgeSeconds;
    }

    private float GetPendingExpirationThresholdSeconds()
    {
        return pendingPolicyMaxAgeSeconds;
    }

    private bool CurrentStateRequiresTransition()
    {
        CP_PerceptionPacket packet = perceptionBuilder != null ? perceptionBuilder.latestPerceptionPacket : null;
        if (packet == null || packet.entry_checks == null)
        {
            return false;
        }
        if (packet.companion != null && !string.IsNullOrEmpty(packet.companion.current_state) && packet.companion.current_state != currentState)
        {
            lastSchedulerWarning = "Entry-check packet state did not match scheduler authoritative state.";
            return false;
        }
        if (currentExecutionContextKey == ExecutionContextFishingF2)
        {
            bool fishingReady = packet.companion != null &&
                                !string.IsNullOrWhiteSpace(packet.companion.current_hand_pose) &&
                                packet.companion.current_hand_pose.Trim().ToLowerInvariant() == "fishing_ready";
            return !(packet.entry_checks.companion_inside_fishing_interaction_area &&
                     packet.entry_checks.fishing_rod_ready &&
                     fishingReady);
        }

        if (currentExecutionContextKey == ExecutionContextFishingF3)
        {
            bool hasAssignedFishingAnchor = packet.companion != null && !string.IsNullOrWhiteSpace(packet.companion.assigned_fishing_anchor_id);
            string currentHandPose = packet.companion != null && !string.IsNullOrWhiteSpace(packet.companion.current_hand_pose)
                ? packet.companion.current_hand_pose.Trim().ToLowerInvariant()
                : string.Empty;
            bool baseReady = hasAssignedFishingAnchor &&
                             packet.entry_checks.companion_at_valid_fishing_anchor &&
                             packet.entry_checks.fishing_rod_ready;
            bool fishingReady = currentHandPose == "fishing_ready";

            if (!baseReady)
            {
                return true;
            }

            if (fishingReady)
            {
                return false;
            }

            return !IsLegitimateActiveF3CastCyclePose(currentHandPose);
        }

        return (currentState == CP_StateNames.S2 && !packet.entry_checks.companion_inside_interaction_area) || (currentState == CP_StateNames.S3 && !packet.entry_checks.companion_at_valid_roasting_anchor);
    }

    // Internal Fishing cast-cycle poses are legal only while the executor is
    // actually running the one public F3 cast-cycle primitive.
    private bool IsLegitimateActiveF3CastCyclePose(string currentHandPose)
    {
        if (currentExecutionContextKey != ExecutionContextFishingF3)
        {
            return false;
        }

        if (currentHandPose != "fishing_cast" &&
            currentHandPose != "fishing_hold" &&
            currentHandPose != "fishing_reel")
        {
            return false;
        }

        if (motionExecutor == null || !motionExecutor.IsBusy)
        {
            return false;
        }

        string currentPrimitiveType = string.IsNullOrWhiteSpace(motionExecutor.CurrentPrimitiveType)
            ? string.Empty
            : motionExecutor.CurrentPrimitiveType.Trim().ToLowerInvariant();
        return currentPrimitiveType == "perform_fishing_cast_cycle";
    }

    // Scheduler execution context stays additive: Fishing uses activity truth,
    // while Campfire continues to rely on the stable legacy current_state path.
    private string DeriveExecutionContextKey(CP_PerceptionPacket packet, string legacyState)
    {
        string activityId = packet != null && packet.activity != null ? packet.activity.id : string.Empty;
        string activityPhase = packet != null && packet.activity != null ? packet.activity.phase : string.Empty;

        if (activityId == "fishing")
        {
            if (activityPhase == "engagement")
            {
                return ExecutionContextFishingF3;
            }

            if (activityPhase == "ready")
            {
                return ExecutionContextFishingF2;
            }
        }

        if (legacyState == CP_StateNames.S3)
        {
            return ExecutionContextCampfireS3;
        }

        if (legacyState == CP_StateNames.S2)
        {
            return ExecutionContextCampfireS2;
        }

        return ExecutionContextCommonS1;
    }

    private string BuildExecutionContextChangeReason(CP_PerceptionPacket packet, string previousContext, string nextContext, bool legacyStateChanged)
    {
        string activityId = packet != null && packet.activity != null ? packet.activity.id : string.Empty;
        string activityPhase = packet != null && packet.activity != null ? packet.activity.phase : string.Empty;
        string detailPhase = packet != null && packet.activity != null ? packet.activity.detail_phase : string.Empty;
        string reason = $"{previousContext}->{nextContext}";

        if (!string.IsNullOrWhiteSpace(activityId))
        {
            reason += $" via activity={activityId}/{activityPhase}";
            if (!string.IsNullOrWhiteSpace(detailPhase))
            {
                reason += $" detail={detailPhase}";
            }
        }

        if (legacyStateChanged)
        {
            reason += $" legacy={previousState}->{currentState}";
        }

        return reason;
    }

    private void NormalizeInterruptionMode()
    {
        if (stateChangeInterruptionMode == SoftFinishCurrentPrimitive || stateChangeInterruptionMode == HardInterruptToTransition)
        {
            return;
        }
        stateChangeInterruptionMode = SoftFinishCurrentPrimitive;
        lastSchedulerWarning = "Invalid interruption mode normalized to soft_finish_current_primitive.";
    }

    private void RecordDiscard(string reason, int jobId)
    {
        if (shutdownLatched)
        {
            lastSchedulerWarning = "job=" + jobId + ": " + reason;
        }
        else if (!isActiveAndEnabled || !schedulerEnabled)
        {
            schedulerState = "disabled";
            lastSchedulerWarning = "job=" + jobId + ": " + reason;
        }
        else
        {
            schedulerState = "response_discarded";
            lastSchedulerWarning = "job=" + jobId + ": " + reason;
        }
        if (logScheduler)
        {
            Debug.Log("CP_PolicyScheduler " + lastSchedulerWarning, this);
        }
    }

    private void UpdateDefaultAttention()
    {
        if (defaultAttentionController == null)
        {
            return;
        }
        bool busy = motionExecutor != null && motionExecutor.IsBusy;
        bool shutdown = perceptionBuilder != null && perceptionBuilder.shutdownSignal;
        bool policyError = policyErrorActive || (policyReceiver != null && IsPolicyError(policyReceiver.latestPolicyResponse));
        bool waitingForPolicy =
            !pipelineStarted ||
            pipelineRestartRequired ||
            requestPending ||
            currentAiGenerationJobId >= 0 ||
            IsTransportInFlight();
        bool allowAttention =
            schedulerEnabled &&
            !shutdownLatched &&
            !busy &&
            !HasOccupiedPendingSlot() &&
            !shutdown &&
            !policyError &&
            waitingForPolicy;
        // Scheduler owns policy_error gating, so Default Attention never uses
        // the controller-side policy-error visual hold path.
        defaultAttentionController.SetSchedulerState(allowAttention, false, schedulerState);
    }

    private void ClearActiveJobTracking()
    {
        currentAiGenerationJobId = -1;
        activeJobStateEpoch = -1;
        activeJobPolicyIndex = -1;
        activeJobRole = string.Empty;
    }

    private void ClearRequestIntent()
    {
        requestPending = false;
        pendingRequestRole = string.Empty;
        pendingRequestEpoch = -1;
        pendingRequestReason = string.Empty;
        retryAtTime = 0f;
    }

    private string GetRecoveryRequestRole()
    {
        return CurrentStateRequiresTransition() ? "state_change_transition" : "current_policy";
    }

    private bool IsExecutingEntryTransitionForCurrentContext()
    {
        if (motionExecutor == null || !motionExecutor.IsBusy || motionExecutor.ActivePolicySource != "transition_priority")
        {
            return false;
        }

        string activeTransitionId = motionExecutor.ActiveTransitionId;
        if (currentExecutionContextKey == ExecutionContextFishingF2)
        {
            return activeTransitionId == "f2_enter_fishing_interaction_area";
        }

        if (currentExecutionContextKey == ExecutionContextFishingF3)
        {
            return activeTransitionId == "f3_enter_assigned_fishing_anchor";
        }

        if (currentState == CP_StateNames.S2)
        {
            return activeTransitionId == "s2_entry_transition";
        }

        if (currentState == CP_StateNames.S3)
        {
            return activeTransitionId == "s3_entry_transition";
        }

        return false;
    }

    private bool HasOccupiedPendingSlot()
    {
        return pendingSlotOccupied;
    }

    private bool HasPendingPayload()
    {
        return pendingNextPolicy != null;
    }

    private void ClearPendingSlotRuntime(string reason, string resultingStatus)
    {
        if (HasOccupiedPendingSlot())
        {
            lastPendingPolicyId = pendingPolicyId;
            lastPendingPolicySource = pendingPolicySource;
            lastPendingPolicyPriority = pendingPolicyPriority;
            lastPendingPolicySourceStateEpoch = pendingPolicySourceStateEpoch;
            lastPendingPolicySourceIndex = pendingPolicySourceIndex;
            lastPendingPolicyDiscardReason = reason;
            pendingPolicyDiscardReason = reason;
        }

        pendingSlotOccupied = false;
        pendingNextPolicy = null;
        pendingNextPolicyStatus = resultingStatus;
        pendingPolicyId = string.Empty;
        pendingPolicySource = string.Empty;
        pendingPolicyPriority = string.Empty;
        pendingPolicySourceStateEpoch = -1;
        pendingPolicySourceIndex = -1;
        pendingPolicyReceivedTimeSeconds = 0f;
        pendingPolicyEffectiveLifetimeSeconds = 0f;
        pendingPolicyCreatedTime = -1f;
        pendingPolicyAgeSeconds = 0f;
        aiPolicyReady = false;
        RefreshBarrierState();
    }

    private bool ValidatePendingSlotConsistency()
    {
        if (!HasOccupiedPendingSlot() && HasPendingPayload())
        {
            pendingNextPolicy = null;
            aiPolicyReady = false;
            RefreshBarrierState();
            lastSchedulerWarning = "Orphan pending payload cleared while slot was unoccupied.";
            return true;
        }

        if (HasOccupiedPendingSlot() && !HasPendingPayload())
        {
            RepairOccupiedSlotMissingPayload();
            return false;
        }

        return true;
    }

    private void RepairOccupiedSlotMissingPayload()
    {
        ClearPendingSlotRuntime("pending_slot_payload_missing", "discarded");
        lastSchedulerWarning = "Occupied pending slot lost its payload.";

        if (policyErrorActive)
        {
            HandlePolicyErrorRecoveryFailure("pending_slot_payload_missing");
            return;
        }

        string replacementRole = CurrentStateRequiresTransition()
            ? "state_change_transition"
            : (motionExecutor != null && motionExecutor.IsBusy && allowLookaheadWhileMotionBusy
                ? "lookahead_next_policy"
                : "current_policy");

        QueueRequest(replacementRole, "pending_invalid_replacement", true);
        schedulerState = "waiting_for_policy_recovery";
    }

    private bool IsPolicyErrorRecoveryRequest(string role, string reason)
    {
        string normalizedReason = reason ?? string.Empty;
        return normalizedReason == "policy_error_recovery" ||
               normalizedReason == "manual_policy_error_recovery";
    }

    private void HandlePolicyErrorRecoveryFailure(string reason)
    {
        lastSchedulerWarning = string.IsNullOrWhiteSpace(reason) ? "policy_error_recovery_failed" : reason;
        pipelineRestartRequired = pipelineStarted;
        ClearRequestIntent();
        ResetBarrierState();
        InvalidatePendingPolicy("policy_error_recovery_failed");
        SchedulePolicyErrorRecovery();
        schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
    }

    private void QueueResumeRequestAfterReenable(string reason)
    {
        if (policyErrorActive)
        {
            pipelineRestartRequired = pipelineStarted;
            SchedulePolicyErrorRecovery();
            schedulerState = policyErrorRecoveryPending ? "policy_error_recovery_wait" : "policy_error";
            return;
        }

        if (requestPending || currentAiGenerationJobId >= 0 || HasValidPendingForCurrentEpoch())
        {
            return;
        }

        QueueRequest(CurrentStateRequiresTransition() ? "state_change_transition" : "current_policy", reason);
    }

    private bool ShouldRetryPreparationFailure(string failureReason)
    {
        return failureReason == "legality_checker_missing" ||
               failureReason == "motion_executor_missing" ||
               failureReason == "prepared_policy_missing";
    }

    private bool MatchesCurrentState(CP_PolicyResponse policy) => policy != null && policy.state != null && policy.state.current_state == currentState;
    private bool IsTransportInFlight() => pythonPolicyClient != null && pythonPolicyClient.IsRequestInFlight;
    private bool IsTransitionPriority(CP_PolicyResponse policy) => policy != null && policy.policy != null && policy.policy.policy_source == "transition_priority";
    private bool IsNormalPolicy(CP_PolicyResponse policy) => policy != null && policy.policy != null && (policy.policy.policy_source == "ai" || policy.policy.policy_source == "fallback");
    private bool IsPolicyError(CP_PolicyResponse policy) => policy != null && ((policy.control != null && policy.control.policy_error) || (policy.policy != null && policy.policy.policy_source == "policy_error"));
    private bool IsParseFailure(CP_PolicyResponse policy) => policy != null && policy.debug != null && (policy.debug.error_code == "parse_failed" || policy.debug.error_code == "empty_policy_json");
    private bool IsShutdown(CP_PolicyResponse policy) => policy != null && policy.control != null && policy.control.shutdown_acknowledged;
    private bool IsNonExecutable(CP_PolicyResponse policy) => policy != null && policy.control != null && !policy.control.execute_policy;
    private string GetResponseFailureReason(CP_PolicyResponse policy, string fallback) => policy != null && policy.debug != null && !string.IsNullOrEmpty(policy.debug.error_code) ? policy.debug.error_code : fallback;
}
