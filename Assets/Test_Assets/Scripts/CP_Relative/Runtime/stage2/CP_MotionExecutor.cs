using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class CP_MotionExecutor : MonoBehaviour
{
    private sealed class CP_QueuedPrimitiveExecution
    {
        public CP_MotionPrimitive primitive;
        public int primitiveIndex;
        public int primitiveTotalCount;
        public int motionGroupId;
        public string policyId = string.Empty;
        public string policySource = string.Empty;
        public string policyActivityMode = string.Empty;
        public string policyState = string.Empty;
        public int stateEpoch = -1;
        public string requestRole = string.Empty;
        public int sourcePolicyIndex = -1;
        public bool isTransitionPriority;
        public int attemptCount;
        public int maxAttempts;
        public string lastFailureReason = string.Empty;
        public CP_PrimitiveExecutionStatus status = CP_PrimitiveExecutionStatus.Idle;
    }

    [Header("References")]
    public Transform companionRoot;
    public CP_LegalityChecker legalityChecker;
    public CP_MovementController movementController;
    public CP_LookController lookController;
    public CP_HandPoseController handPoseController;
    public CP_PropController propController;
    public CP_FishingCastCycleController fishingCastCycleController;

    [Header("Motion Settings")]
    public float turnSpeedDegreesPerSecond = 180f;
    public bool holdPositionLocksVelocity = true;
    public int normalPrimitiveMaxAttempts = 5;
    public int transitionPrimitiveMaxAttempts = 1;

    [Header("Latest Motion")]
    [TextArea(6, 16)]
    public string latestMotionSummary = string.Empty;
    public string latestInterruptionReason = string.Empty;
    public string activeControllerName = string.Empty;
    public string activeControllerFailureReason = string.Empty;
    public int currentPrimitiveAttempt;
    public int currentPrimitiveMaxAttempts;
    public System.Action<int> OnMotionGroupStarted;
    public System.Action<int> OnMotionGroupCompleted;
    public System.Action<int, string> OnMotionGroupInterrupted;
    public int ActiveMotionGroupId { get; private set; } = -1;
    public int LastCompletedMotionGroupId { get; private set; } = -1;
    public int LastInterruptedMotionGroupId { get; private set; } = -1;
    public string ActivePolicyId => ActiveMotionGroupId >= 0 ? activePolicyId : string.Empty;
    public string ActivePolicySource => ActiveMotionGroupId >= 0 ? activePolicySource : string.Empty;
    public string ActiveTransitionId => ActiveMotionGroupId >= 0 ? activeTransitionId : string.Empty;
    public string LastCompletedPolicyId { get; private set; } = string.Empty;
    public string LastInterruptedPolicyId { get; private set; } = string.Empty;

    private readonly Queue<CP_QueuedPrimitiveExecution> pendingPrimitives = new Queue<CP_QueuedPrimitiveExecution>();
    private readonly StringBuilder summaryBuilder = new StringBuilder();

    private CP_QueuedPrimitiveExecution activeExecution;
    private float activeElapsedSeconds;
    private float activeDurationSeconds;
    private string activePolicyId = string.Empty;
    private string activePolicySource = string.Empty;
    private string activePolicyActivityMode = string.Empty;
    private string activePolicyState = string.Empty;
    private int activePolicyStateEpoch = -1;
    private string activeRequestRole = string.Empty;
    private string activeTransitionId = string.Empty;
    private int activeSourcePolicyIndex = -1;
    private string activePrimitiveStyle = string.Empty;
    private string activePrimitiveTargetLabel = string.Empty;
    private int nextMotionGroupId;
    private bool motionGroupStarted;
    private bool motionGroupCompletionNotified;
    private bool waitingToRetryPrimitive;

    // Minimal scheduler-facing state; the executor retains ownership of the actual primitive queue.
    public bool IsBusy => activeExecution != null || pendingPrimitives.Count > 0 || waitingToRetryPrimitive;
    public bool HasQueuedPrimitives => pendingPrimitives.Count > 0;
    public string CurrentPrimitiveType => activeExecution != null && activeExecution.primitive != null ? activeExecution.primitive.type : string.Empty;
    public CP_MotionPrimitive CurrentPrimitive => activeExecution != null ? ClonePrimitive(activeExecution.primitive) : null;

    private void Update()
    {
        if (companionRoot == null)
        {
            latestMotionSummary = "Motion Executor:\nError: companionRoot is not assigned";
            return;
        }

        if (waitingToRetryPrimitive)
        {
            ProcessPendingPrimitiveRetry();
            return;
        }

        if (activeExecution == null)
        {
            TryStartNextPrimitive();
        }

        if (activeExecution == null)
        {
            if (pendingPrimitives.Count == 0 && !waitingToRetryPrimitive)
            {
                latestMotionSummary = "Motion Executor:\nState: idle";
                TryNotifyMotionGroupCompleted();
            }

            return;
        }

        CP_PrimitiveExecutionStatus status = TickActiveExecution();
        if (status == CP_PrimitiveExecutionStatus.Running)
        {
            return;
        }

        if (status == CP_PrimitiveExecutionStatus.Succeeded)
        {
            CompleteActiveExecution();
            TryNotifyMotionGroupCompleted();
            return;
        }

        if (status == CP_PrimitiveExecutionStatus.Cancelled)
        {
            InterruptMotionGroup("primitive_cancelled");
            return;
        }

        if (status == CP_PrimitiveExecutionStatus.Failed)
        {
            HandleActiveExecutionFailure();
        }
    }

    private void FixedUpdate()
    {
        if (companionRoot == null || activeExecution == null || activeExecution.primitive == null)
        {
            return;
        }

        if (IsMovementPrimitive(activeExecution.primitive.type))
        {
            return;
        }

        Rigidbody rigidbody = GetCompanionRigidbody();
        if (rigidbody == null)
        {
            ApplyActivePrimitiveTransformFallback();
            return;
        }

        ApplyActivePrimitiveRigidbodyMotion(rigidbody);
    }

    private void OnDisable()
    {
        if (activeExecution != null || pendingPrimitives.Count > 0)
        {
            InterruptMotionGroup("motion_executor_disabled");
        }
    }

    public void ClearPendingPrimitivesKeepCurrent(string reason)
    {
        pendingPrimitives.Clear();
        latestInterruptionReason = reason ?? string.Empty;
        latestMotionSummary = activeExecution == null || activeExecution.primitive == null
            ? "Motion Executor:\nState: queue cleared\nReason: " + reason
            : BuildSummary("soft_interruption", activeExecution.primitive.type, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, 0);
    }

    public void AbortCurrentAndClearQueue(string reason)
    {
        latestInterruptionReason = reason ?? string.Empty;
        StopExecution(string.IsNullOrWhiteSpace(reason) ? "scheduler_hard_interrupt" : reason);
    }

    // Scheduler route: a normal policy must never replace a currently active policy group.
    public bool TryLoadPolicyWhenIdle(CP_PolicyResponse policy)
    {
        if (IsBusy)
        {
            latestMotionSummary = "Motion Executor:\nState: load rejected because execution is busy";
            return false;
        }

        LoadPolicy(policy);
        return true;
    }

    public void LoadPolicy(CP_PolicyResponse policy)
    {
        CancelActiveController("load_policy_replaced");
        ResetRuntimeQueueState(clearHistory: false);

        if (policy == null || policy.policy == null || policy.motion_primitives == null)
        {
            latestMotionSummary = "Motion Executor:\nState: received empty policy";
            return;
        }

        activePolicyId = policy.policy.policy_id;
        activePolicySource = policy.policy.policy_source;
        activePolicyActivityMode = policy.policy.activity_mode;
        activePolicyState = policy.state != null ? policy.state.current_state : string.Empty;
        activePolicyStateEpoch = policy.debug != null ? policy.debug.source_state_epoch : -1;
        activeRequestRole = policy.debug != null ? policy.debug.request_role : string.Empty;
        activeTransitionId = policy.queue != null ? policy.queue.current_transition_id : string.Empty;
        activeSourcePolicyIndex = policy.debug != null ? policy.debug.source_policy_index : -1;

        int primitiveTotalCount = policy.motion_primitives.Count;
        int motionGroupId = primitiveTotalCount > 0 ? ++nextMotionGroupId : -1;

        for (int i = 0; i < primitiveTotalCount; i++)
        {
            CP_MotionPrimitive primitive = policy.motion_primitives[i];
            if (primitive == null)
            {
                continue;
            }

            pendingPrimitives.Enqueue(new CP_QueuedPrimitiveExecution
            {
                primitive = ClonePrimitive(primitive),
                primitiveIndex = i,
                primitiveTotalCount = primitiveTotalCount,
                motionGroupId = motionGroupId,
                policyId = activePolicyId,
                policySource = activePolicySource,
                policyActivityMode = activePolicyActivityMode,
                policyState = activePolicyState,
                stateEpoch = activePolicyStateEpoch,
                requestRole = activeRequestRole,
                sourcePolicyIndex = activeSourcePolicyIndex,
                isTransitionPriority = NormalizePrimitiveType(activePolicySource) == "transition_priority",
                maxAttempts = NormalizePrimitiveType(activePolicySource) == "transition_priority"
                    ? Mathf.Max(1, transitionPrimitiveMaxAttempts)
                    : Mathf.Max(1, normalPrimitiveMaxAttempts),
            });
        }

        latestMotionSummary = BuildSummary("loaded", string.Empty, string.Empty, string.Empty, 0f, pendingPrimitives.Count);
        if (pendingPrimitives.Count > 0)
        {
            ActiveMotionGroupId = motionGroupId;
            motionGroupStarted = true;
            motionGroupCompletionNotified = false;
            OnMotionGroupStarted?.Invoke(ActiveMotionGroupId);
        }
    }

    public void StopExecution(string reason)
    {
        InterruptMotionGroup(reason);
    }

    // Stage 5A pre-test correction:
    // retry must be processed on a later frame even while activeExecution is still present.
    private void ProcessPendingPrimitiveRetry()
    {
        if (!waitingToRetryPrimitive)
        {
            return;
        }

        if (activeExecution == null || activeExecution.primitive == null)
        {
            waitingToRetryPrimitive = false;
            activeControllerFailureReason = "retry_execution_missing";
            latestMotionSummary = "Motion Executor:\nState: retry aborted\nReason: retry_execution_missing";
            ClearActiveExecutionRuntimeState();
            return;
        }

        waitingToRetryPrimitive = false;
        if (TryBeginActiveExecution(activeExecution, out string retryFailureReason))
        {
            return;
        }

        activeExecution.lastFailureReason = retryFailureReason;
        activeControllerFailureReason = retryFailureReason;
        activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
        HandleActiveExecutionFailure();
    }

    private void TryStartNextPrimitive()
    {
        while (pendingPrimitives.Count > 0)
        {
            CP_QueuedPrimitiveExecution nextExecution = pendingPrimitives.Dequeue();
            if (nextExecution == null || nextExecution.primitive == null)
            {
                continue;
            }

            string normalizedType = NormalizePrimitiveType(nextExecution.primitive.type);
            if (string.IsNullOrEmpty(normalizedType))
            {
                continue;
            }

            if (!IsHandledPrimitiveType(normalizedType))
            {
                nextExecution.primitive.type = normalizedType;
                RecordUnhandledPrimitiveAndSkip(nextExecution, "primitive_not_implemented_in_current_stage");
                if (activeExecution != null)
                {
                    return;
                }
                continue;
            }

            nextExecution.primitive.type = normalizedType;
            activeExecution = nextExecution;
            if (TryBeginActiveExecution(activeExecution, out string failureReason))
            {
                return;
            }

            activeExecution.lastFailureReason = failureReason;
            activeControllerFailureReason = failureReason;
            activeExecution.status = CP_PrimitiveExecutionStatus.Failed;

            if (activeExecution.isTransitionPriority)
            {
                HandleActiveExecutionFailure();
                return;
            }

            HandleActiveExecutionFailure();
            if (activeExecution != null)
            {
                return;
            }
        }

        activeExecution = null;
    }

    private bool TryBeginActiveExecution(CP_QueuedPrimitiveExecution execution, out string failureReason)
    {
        failureReason = string.Empty;
        if (execution == null || execution.primitive == null)
        {
            failureReason = "primitive_execution_missing";
            return false;
        }

        execution.attemptCount++;
        currentPrimitiveAttempt = execution.attemptCount;
        currentPrimitiveMaxAttempts = execution.maxAttempts;
        activePrimitiveStyle = execution.primitive.style;
        activePrimitiveTargetLabel = string.IsNullOrWhiteSpace(execution.primitive.target) ? string.Empty : execution.primitive.target;
        activeElapsedSeconds = 0f;
        activeDurationSeconds = Mathf.Clamp(execution.primitive.duration, 0.1f, 10f);
        activeControllerFailureReason = string.Empty;

        string primitiveType = NormalizePrimitiveType(execution.primitive.type);
        if (IsMovementPrimitive(primitiveType))
        {
            if (movementController == null)
            {
                failureReason = "movement_controller_missing";
                return false;
            }

            CP_PrimitiveExecutionContext context = new CP_PrimitiveExecutionContext
            {
                motionGroupId = execution.motionGroupId,
                primitiveIndex = execution.primitiveIndex,
                policyId = execution.policyId,
                policySource = execution.policySource,
                currentState = execution.policyState,
                stateEpoch = execution.stateEpoch,
                requestRole = execution.requestRole,
                isTransitionPriority = execution.isTransitionPriority
            };

            activeControllerName = nameof(CP_MovementController);
            if (!movementController.TryBegin(execution.primitive, context, out failureReason))
            {
                activeControllerFailureReason = failureReason;
                return false;
            }

            execution.status = CP_PrimitiveExecutionStatus.Running;
            latestMotionSummary = BuildSummary("started", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return true;
        }

        if ((primitiveType == "look_at" || primitiveType == "face_toward"))
        {
            if (lookController == null)
            {
                failureReason = "look_controller_not_bound";
                return false;
            }

            bool beginSucceeded = primitiveType == "look_at"
                ? lookController.TryBeginLookAt(execution.primitive, out failureReason)
                : lookController.TryBeginFaceToward(execution.primitive, out failureReason);

            if (!beginSucceeded)
            {
                activeControllerFailureReason = failureReason;
                return false;
            }

            activeControllerName = nameof(CP_LookController);
            activePrimitiveTargetLabel = execution.primitive.target;
            execution.status = CP_PrimitiveExecutionStatus.Running;
            latestMotionSummary = BuildSummary("started", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return true;
        }

        if (primitiveType == "set_hand_pose")
        {
            if (handPoseController == null)
            {
                failureReason = "hand_pose_controller_missing";
                return false;
            }

            CP_PrimitiveExecutionContext context = new CP_PrimitiveExecutionContext
            {
                motionGroupId = execution.motionGroupId,
                primitiveIndex = execution.primitiveIndex,
                policyId = execution.policyId,
                policySource = execution.policySource,
                currentState = execution.policyState,
                stateEpoch = execution.stateEpoch,
                requestRole = execution.requestRole,
                isTransitionPriority = execution.isTransitionPriority
            };

            activeControllerName = nameof(CP_HandPoseController);
            if (!handPoseController.TryBegin(execution.primitive, context, out failureReason))
            {
                activeControllerFailureReason = failureReason;
                return false;
            }

            execution.status = handPoseController.Status;
            latestMotionSummary = BuildSummary("started", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return true;
        }

        if (primitiveType == "set_companion_prop")
        {
            if (propController == null)
            {
                failureReason = "prop_controller_missing";
                return false;
            }

            CP_PrimitiveExecutionContext context = new CP_PrimitiveExecutionContext
            {
                motionGroupId = execution.motionGroupId,
                primitiveIndex = execution.primitiveIndex,
                policyId = execution.policyId,
                policySource = execution.policySource,
                currentState = execution.policyState,
                stateEpoch = execution.stateEpoch,
                requestRole = execution.requestRole,
                isTransitionPriority = execution.isTransitionPriority
            };

            activeControllerName = nameof(CP_PropController);
            if (!propController.TryBegin(execution.primitive, context, out failureReason))
            {
                activeControllerFailureReason = failureReason;
                return false;
            }

            execution.status = propController.status;
            latestMotionSummary = BuildSummary("started", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return true;
        }

        if (primitiveType == "perform_fishing_cast_cycle")
        {
            if (fishingCastCycleController == null)
            {
                failureReason = "fishing_cast_cycle_controller_missing";
                return false;
            }

            CP_PrimitiveExecutionContext context = new CP_PrimitiveExecutionContext
            {
                motionGroupId = execution.motionGroupId,
                primitiveIndex = execution.primitiveIndex,
                policyId = execution.policyId,
                policySource = execution.policySource,
                currentState = execution.policyState,
                stateEpoch = execution.stateEpoch,
                requestRole = execution.requestRole,
                isTransitionPriority = execution.isTransitionPriority
            };

            activeControllerName = nameof(CP_FishingCastCycleController);
            if (!fishingCastCycleController.TryBegin(execution.primitive, context, out failureReason))
            {
                activeControllerFailureReason = failureReason;
                return false;
            }

            activeDurationSeconds = fishingCastCycleController.ExpectedCycleDurationSeconds;
            execution.status = fishingCastCycleController.Status;
            latestMotionSummary = BuildSummary("started", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return true;
        }

        activeControllerName = "legacy_executor";
        execution.status = CP_PrimitiveExecutionStatus.Running;
        latestMotionSummary = BuildSummary("started", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
        return true;
    }

    private CP_PrimitiveExecutionStatus TickActiveExecution()
    {
        if (activeExecution == null || activeExecution.primitive == null)
        {
            return CP_PrimitiveExecutionStatus.Idle;
        }

        string primitiveType = NormalizePrimitiveType(activeExecution.primitive.type);
        if (IsMovementPrimitive(primitiveType))
        {
            if (movementController == null)
            {
                activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
                activeExecution.lastFailureReason = "movement_controller_missing";
                activeControllerFailureReason = activeExecution.lastFailureReason;
                return activeExecution.status;
            }

            CP_PrimitiveExecutionStatus controllerStatus = movementController.Tick(Time.deltaTime);
            activeExecution.status = controllerStatus;
            activeControllerFailureReason = movementController.LastFailureReason;
            latestMotionSummary = BuildSummary("running", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return controllerStatus;
        }

        if (primitiveType == "look_at" || primitiveType == "face_toward")
        {
            if (lookController == null)
            {
                activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
                activeExecution.lastFailureReason = "look_controller_not_bound";
                activeControllerFailureReason = activeExecution.lastFailureReason;
                return activeExecution.status;
            }

            activeExecution.status = lookController.Status;
            activeControllerFailureReason = lookController.LastFailureReason;
            latestMotionSummary = BuildSummary("running", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return activeExecution.status;
        }

        if (primitiveType == "set_hand_pose")
        {
            if (handPoseController == null)
            {
                activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
                activeExecution.lastFailureReason = "hand_pose_controller_missing";
                activeControllerFailureReason = activeExecution.lastFailureReason;
                return activeExecution.status;
            }

            activeExecution.status = handPoseController.Tick(Time.deltaTime);
            activeControllerFailureReason = handPoseController.LastFailureReason;
            latestMotionSummary = BuildSummary("running", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return activeExecution.status;
        }

        if (primitiveType == "set_companion_prop")
        {
            if (propController == null)
            {
                activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
                activeExecution.lastFailureReason = "prop_controller_missing";
                activeControllerFailureReason = activeExecution.lastFailureReason;
                return activeExecution.status;
            }

            activeExecution.status = propController.Tick(Time.deltaTime);
            activeControllerFailureReason = propController.LastFailureReason;
            latestMotionSummary = BuildSummary("running", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return activeExecution.status;
        }

        if (primitiveType == "perform_fishing_cast_cycle")
        {
            if (fishingCastCycleController == null)
            {
                activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
                activeExecution.lastFailureReason = "fishing_cast_cycle_controller_missing";
                activeControllerFailureReason = activeExecution.lastFailureReason;
                return activeExecution.status;
            }

            activeExecution.status = fishingCastCycleController.Tick(Time.deltaTime);
            activeControllerFailureReason = fishingCastCycleController.LastFailureReason;
            activeDurationSeconds = fishingCastCycleController.ExpectedCycleDurationSeconds;
            latestMotionSummary = BuildSummary("running", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            return activeExecution.status;
        }

        activeElapsedSeconds += Time.deltaTime;
        latestMotionSummary = BuildSummary("running", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);

        if (activeElapsedSeconds >= activeDurationSeconds)
        {
            activeExecution.status = CP_PrimitiveExecutionStatus.Succeeded;
        }

        return activeExecution.status;
    }

    private void CompleteActiveExecution()
    {
        if (activeExecution == null || activeExecution.primitive == null)
        {
            return;
        }

        AcknowledgeControllerTerminalStatusIfNeeded();
        latestMotionSummary = BuildSummary("completed", activeExecution.primitive.type, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
        ClearActiveExecutionRuntimeState();
    }

    private void HandleActiveExecutionFailure()
    {
        if (activeExecution == null || activeExecution.primitive == null)
        {
            return;
        }

        string primitiveType = NormalizePrimitiveType(activeExecution.primitive.type);
        string failureReason = string.IsNullOrWhiteSpace(activeExecution.lastFailureReason)
            ? (string.IsNullOrWhiteSpace(activeControllerFailureReason) ? "primitive_failed" : activeControllerFailureReason)
            : activeExecution.lastFailureReason;

        AcknowledgeControllerTerminalStatusIfNeeded();

        if (activeExecution.isTransitionPriority)
        {
            latestMotionSummary = BuildSummary("failed", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            InterruptMotionGroup("transition_primitive_failed:" + failureReason);
            return;
        }

        if (ShouldRetryCurrentPrimitiveFailure() && activeExecution.attemptCount < activeExecution.maxAttempts)
        {
            latestMotionSummary = BuildSummary("retrying", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
            activeExecution.lastFailureReason = failureReason;
            waitingToRetryPrimitive = true;
            return;
        }

        latestMotionSummary = BuildSummary("skipped_after_failure", primitiveType, activePrimitiveTargetLabel, activePrimitiveStyle, activeDurationSeconds, pendingPrimitives.Count);
        ClearActiveExecutionRuntimeState();
    }

    private void TryNotifyMotionGroupCompleted()
    {
        if (!motionGroupStarted || motionGroupCompletionNotified)
        {
            return;
        }

        if (activeExecution != null || pendingPrimitives.Count > 0 || waitingToRetryPrimitive)
        {
            return;
        }

        int completedGroupId = ActiveMotionGroupId;
        string completedPolicyId = activePolicyId;
        motionGroupCompletionNotified = true;
        motionGroupStarted = false;
        ActiveMotionGroupId = -1;
        LastCompletedMotionGroupId = completedGroupId;
        LastCompletedPolicyId = completedPolicyId;
        activePolicyId = string.Empty;
        OnMotionGroupCompleted?.Invoke(completedGroupId);
    }

    private void InterruptMotionGroup(string reason)
    {
        int interruptedGroupId = motionGroupStarted && !motionGroupCompletionNotified ? ActiveMotionGroupId : -1;
        string interruptedPolicyId = activePolicyId;

        latestInterruptionReason = reason ?? string.Empty;
        CancelActiveController(reason);
        ResetRuntimeQueueState(clearHistory: true);

        latestMotionSummary = string.IsNullOrEmpty(reason)
            ? "Motion Executor:\nState: stopped"
            : $"Motion Executor:\nState: stopped\nReason: {reason}";

        if (interruptedGroupId >= 0)
        {
            LastInterruptedMotionGroupId = interruptedGroupId;
            LastInterruptedPolicyId = interruptedPolicyId;
            OnMotionGroupInterrupted?.Invoke(interruptedGroupId, latestInterruptionReason);
        }
    }

    private void CancelActiveController(string reason)
    {
        if (activeExecution != null && activeExecution.primitive != null && IsMovementPrimitive(activeExecution.primitive.type) && movementController != null)
        {
            movementController.Cancel(reason);
        }
        else if (activeExecution != null && activeExecution.primitive != null)
        {
            string primitiveType = NormalizePrimitiveType(activeExecution.primitive.type);
            if ((primitiveType == "look_at" || primitiveType == "face_toward") && lookController != null)
            {
                lookController.CancelExplicitLook(reason);
                lookController.AcknowledgeTerminalStatus();
            }
            else if (primitiveType == "set_hand_pose" && handPoseController != null)
            {
                handPoseController.Cancel(reason);
                handPoseController.AcknowledgeTerminalStatus();
            }
            else if (primitiveType == "set_companion_prop" && propController != null)
            {
                propController.Cancel(reason);
                propController.AcknowledgeTerminalStatus();
            }
            else if (primitiveType == "perform_fishing_cast_cycle" && fishingCastCycleController != null)
            {
                fishingCastCycleController.Cancel(reason);
                fishingCastCycleController.AcknowledgeTerminalStatus();
            }
        }

        if (companionRoot != null)
        {
            Rigidbody rigidbody = GetCompanionRigidbody();
            if (rigidbody != null)
            {
                if (!rigidbody.isKinematic)
                {
                    rigidbody.angularVelocity = Vector3.zero;
                    rigidbody.linearVelocity = Vector3.zero;
                }
            }
        }
    }

    private void ResetRuntimeQueueState(bool clearHistory)
    {
        pendingPrimitives.Clear();
        activeExecution = null;
        waitingToRetryPrimitive = false;
        activeElapsedSeconds = 0f;
        activeDurationSeconds = 0f;
        activePrimitiveStyle = string.Empty;
        activePrimitiveTargetLabel = string.Empty;
        currentPrimitiveAttempt = 0;
        currentPrimitiveMaxAttempts = 0;
        activeControllerName = string.Empty;
        activeControllerFailureReason = string.Empty;
        ActiveMotionGroupId = -1;
        motionGroupStarted = false;
        motionGroupCompletionNotified = false;

        if (clearHistory)
        {
            activePolicyId = string.Empty;
            activePolicySource = string.Empty;
            activePolicyActivityMode = string.Empty;
            activePolicyState = string.Empty;
            activePolicyStateEpoch = -1;
            activeRequestRole = string.Empty;
            activeTransitionId = string.Empty;
            activeSourcePolicyIndex = -1;
        }
    }

    private void ClearActiveExecutionRuntimeState()
    {
        activeExecution = null;
        waitingToRetryPrimitive = false;
        activeElapsedSeconds = 0f;
        activeDurationSeconds = 0f;
        currentPrimitiveAttempt = 0;
        currentPrimitiveMaxAttempts = 0;
        activeControllerName = string.Empty;
        activeControllerFailureReason = string.Empty;
        activePrimitiveStyle = string.Empty;
        activePrimitiveTargetLabel = string.Empty;
    }

    private CP_MotionPrimitive ClonePrimitive(CP_MotionPrimitive primitive)
    {
        if (primitive == null)
        {
            return null;
        }

        CP_MotionPrimitive clone = new CP_MotionPrimitive();
        clone.type = primitive.type;
        clone.target = primitive.target;
        clone.duration = primitive.duration;
        clone.style = primitive.style;
        clone.notes = primitive.notes;
        clone.scope = primitive.scope;
        clone.radius_min = primitive.radius_min;
        clone.radius_max = primitive.radius_max;
        clone.pose = primitive.pose;
        clone.prop = primitive.prop;
        clone.state = primitive.state;
        clone.gesture = primitive.gesture;
        return clone;
    }

    private void ApplyActivePrimitiveRigidbodyMotion(Rigidbody rigidbody)
    {
        if (activeExecution == null || activeExecution.primitive == null)
        {
            return;
        }

        if (NormalizePrimitiveType(activeExecution.primitive.type) == "idle_motion")
        {
            if (!rigidbody.isKinematic)
            {
                rigidbody.angularVelocity = Vector3.zero;
                rigidbody.linearVelocity = Vector3.zero;
            }
        }
    }

    private void ApplyActivePrimitiveTransformFallback()
    {
        // Stage 5B-1 look execution is owned by CP_LookController.
        // The executor keeps only the idle-motion transform-safe fallback path here.
    }

    private bool IsHandledPrimitiveType(string primitiveType)
    {
        switch (primitiveType)
        {
            case "look_at":
            case "face_toward":
            case "move_within_scope":
            case "hold_position":
            case "set_hand_pose":
            case "set_companion_prop":
            case "perform_fishing_cast_cycle":
                return true;
            default:
                return false;
        }
    }

    private bool IsMovementPrimitive(string primitiveType)
    {
        switch (NormalizePrimitiveType(primitiveType))
        {
            case "move_within_scope":
            case "hold_position":
                return true;
            default:
                return false;
        }
    }

    private string NormalizePrimitiveType(string primitiveType)
    {
        return string.IsNullOrWhiteSpace(primitiveType) ? string.Empty : primitiveType.Trim().ToLowerInvariant();
    }

    private string BuildSummary(string phase, string primitiveType, string targetLabel, string style, float durationSeconds, int remainingCount)
    {
        summaryBuilder.Clear();
        summaryBuilder.AppendLine("Policy ID: " + activePolicyId);
        summaryBuilder.AppendLine("Policy Source: " + activePolicySource);
        summaryBuilder.AppendLine("Activity Mode: " + activePolicyActivityMode);
        summaryBuilder.AppendLine("State: " + activePolicyState);
        summaryBuilder.AppendLine("State Epoch: " + activePolicyStateEpoch);
        summaryBuilder.AppendLine("Request Role: " + activeRequestRole);
        summaryBuilder.AppendLine("Source Policy Index: " + activeSourcePolicyIndex);
        summaryBuilder.AppendLine("Phase: " + phase);
        summaryBuilder.AppendLine("Current Primitive: " + primitiveType);
        summaryBuilder.AppendLine("Target: " + (string.IsNullOrEmpty(targetLabel) ? "none" : targetLabel));
        summaryBuilder.AppendLine("Style: " + (string.IsNullOrEmpty(style) ? "none" : style));
        summaryBuilder.AppendLine("Duration: " + durationSeconds.ToString("F2"));
        summaryBuilder.AppendLine("Elapsed: " + activeElapsedSeconds.ToString("F2"));
        summaryBuilder.AppendLine("Attempt: " + currentPrimitiveAttempt + " / " + currentPrimitiveMaxAttempts);
        summaryBuilder.AppendLine("Controller: " + (string.IsNullOrEmpty(activeControllerName) ? "none" : activeControllerName));
        summaryBuilder.AppendLine("Controller Failure: " + (string.IsNullOrEmpty(activeControllerFailureReason) ? "none" : activeControllerFailureReason));
        summaryBuilder.AppendLine("Pending Primitive Count: " + remainingCount);
        return summaryBuilder.ToString();
    }

    private Rigidbody GetCompanionRigidbody()
    {
        return companionRoot != null ? companionRoot.GetComponent<Rigidbody>() : null;
    }

    private bool IsLookPrimitive(string primitiveType)
    {
        string normalizedType = NormalizePrimitiveType(primitiveType);
        return normalizedType == "look_at" || normalizedType == "face_toward";
    }

    private bool ShouldRetryCurrentPrimitiveFailure()
    {
        if (activeExecution == null || activeExecution.primitive == null)
        {
            return false;
        }

        string primitiveType = NormalizePrimitiveType(activeExecution.primitive.type);
        if (primitiveType == "set_hand_pose" || primitiveType == "set_companion_prop" || primitiveType == "perform_fishing_cast_cycle")
        {
            return false;
        }

        return true;
    }

    private void AcknowledgeControllerTerminalStatusIfNeeded()
    {
        if (activeExecution == null || activeExecution.primitive == null)
        {
            return;
        }

        string primitiveType = NormalizePrimitiveType(activeExecution.primitive.type);
        if (IsLookPrimitive(primitiveType))
        {
            if (lookController == null)
            {
                return;
            }

            if (lookController.Status == CP_PrimitiveExecutionStatus.Succeeded ||
                lookController.Status == CP_PrimitiveExecutionStatus.Failed ||
                lookController.Status == CP_PrimitiveExecutionStatus.Cancelled)
            {
                lookController.AcknowledgeTerminalStatus();
            }
            return;
        }

        if (primitiveType == "set_hand_pose" && handPoseController != null)
        {
            handPoseController.AcknowledgeTerminalStatus();
            return;
        }

        if (primitiveType == "set_companion_prop" && propController != null)
        {
            propController.AcknowledgeTerminalStatus();
            return;
        }

        if (primitiveType == "perform_fishing_cast_cycle" && fishingCastCycleController != null)
        {
            fishingCastCycleController.AcknowledgeTerminalStatus();
        }
    }

    private void RecordUnhandledPrimitiveAndSkip(CP_QueuedPrimitiveExecution execution, string reason)
    {
        if (execution == null || execution.primitive == null)
        {
            return;
        }

        activeExecution = execution;
        activeExecution.lastFailureReason = reason;
        activeExecution.status = CP_PrimitiveExecutionStatus.Failed;
        activeControllerName = "stage_gate";
        activeControllerFailureReason = reason;
        activePrimitiveStyle = execution.primitive.style;
        activePrimitiveTargetLabel = string.IsNullOrWhiteSpace(execution.primitive.target) ? string.Empty : execution.primitive.target;
        activeDurationSeconds = Mathf.Clamp(execution.primitive.duration, 0.1f, 10f);
        latestMotionSummary = BuildSummary(
            execution.isTransitionPriority ? "unsupported_transition_primitive" : "unsupported_primitive_skipped",
            execution.primitive.type,
            activePrimitiveTargetLabel,
            activePrimitiveStyle,
            activeDurationSeconds,
            pendingPrimitives.Count
        );

        if (execution.isTransitionPriority)
        {
            InterruptMotionGroup("transition_primitive_failed:" + reason);
            return;
        }

        ClearActiveExecutionRuntimeState();
    }
}
