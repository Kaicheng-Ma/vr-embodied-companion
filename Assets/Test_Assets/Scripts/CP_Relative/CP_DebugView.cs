using System.Text;
using UnityEngine;

public class CP_DebugView : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;
    public CP_RoastingAnchorManager roastingAnchorManager;
    public CP_InteractionArea interactionArea;
    public CP_PerceptionBuilder perceptionBuilder;
    public CP_PolicyReceiver policyReceiver;
    public CP_PythonPolicyClient pythonPolicyClient;
    public CP_MotionExecutor motionExecutor;
    public CP_PolicyScheduler policyScheduler;
    public CP_DefaultAttentionController defaultAttentionController;
    public CP_CompanionAnimatorDriver companionAnimatorDriver;
    public CP_LookController lookController;
    public CP_HandPoseController handPoseController;
    public CP_PropController propController;

    [Header("Display")]
    public bool showDebugView = true;
    public bool showBlackboard = true;
    public bool showSpatial = true;
    public bool showDerivedState = true;
    public bool showPerceptionSnapshot = true;
    public bool showPolicyClient = true;
    public bool showPolicyReceiver = true;
    public bool showMotionExecutor = true;
    public bool showScheduler = true;
    public bool showSnapshotWarnings = true;
    public bool showSnapshotJson = false;

    [Header("Window Settings")]
    public int windowX = 10;
    public int windowY = 10;
    public int windowWidth = 520;
    public int windowHeight = 520;
    public int fontSize = 16;

    private GUIStyle labelStyle;
    private readonly StringBuilder sb = new StringBuilder();
    private Vector2 scrollPosition;

    private void OnGUI()
    {
        if (!showDebugView)
            return;

        EnsureStyle();

        GUI.Box(
            new Rect(windowX, windowY, windowWidth, windowHeight),
            "CP Debug View"
        );

        string debugText = BuildDebugText();
        Rect viewRect = new Rect(windowX + 12, windowY + 30, windowWidth - 24, windowHeight - 40);
        float contentHeight = Mathf.Max(viewRect.height, labelStyle.CalcHeight(new GUIContent(debugText), viewRect.width - 16f) + 16f);
        Rect contentRect = new Rect(0f, 0f, viewRect.width - 16f, contentHeight);

        scrollPosition = GUI.BeginScrollView(viewRect, scrollPosition, contentRect);
        GUI.Label(contentRect, debugText, labelStyle);
        GUI.EndScrollView();
    }

    private void EnsureStyle()
    {
        if (labelStyle != null)
            return;

        labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.fontSize = fontSize;
        labelStyle.normal.textColor = Color.white;
        labelStyle.wordWrap = true;
    }

    private string BuildDebugText()
    {
        // This remains a presentation layer only; it should not mutate gameplay state.
        sb.Clear();

        if (showDerivedState)
        {
            AppendDerivedStateSection();
        }

        if (showBlackboard)
        {
            AppendBlackboardSection();
        }

        if (showSpatial)
        {
            AppendSpatialSection();
        }

        if (showPerceptionSnapshot)
        {
            AppendPerceptionSection();
        }

        if (showPolicyClient)
        {
            AppendPolicyClientSection();
        }

        if (showPolicyReceiver)
        {
            AppendPolicyReceiverSection();
        }

        if (showMotionExecutor)
        {
            AppendMotionExecutorSection();
        }

        if (showScheduler)
        {
            AppendSchedulerSection();
        }

        return sb.ToString();
    }

    private void AppendDerivedStateSection()
    {
        sb.AppendLine("=== Derived State ===");
        sb.AppendLine($"Current State: {GetDerivedState()}");
        sb.AppendLine();
    }

    private string GetDerivedState()
    {
        if (perceptionBuilder != null)
        {
            CP_PerceptionSnapshot snapshot = perceptionBuilder.GetLatestSnapshot();

            if (snapshot != null && !string.IsNullOrEmpty(snapshot.currentStateName))
            {
                return snapshot.currentStateName;
            }
        }

        if (perceptionBuilder != null && perceptionBuilder.activityContextRouter != null)
        {
            string canonicalState = perceptionBuilder.activityContextRouter.CanonicalState;
            if (!string.IsNullOrWhiteSpace(canonicalState))
            {
                return canonicalState;
            }
        }

        if (blackboard == null)
            return CP_StateNames.UnknownNoBlackboard;

        if (!blackboard.campfire_started)
            return CP_StateNames.S1;

        if (blackboard.campfire_started && !blackboard.stick_picked)
            return CP_StateNames.S2;

        if (blackboard.campfire_started && blackboard.stick_picked)
            return CP_StateNames.S3;

        return CP_StateNames.Unknown;
    }

    private void AppendBlackboardSection()
    {
        sb.AppendLine("=== Blackboard ===");

        if (blackboard == null)
        {
            sb.AppendLine("Blackboard: NULL");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"campfire_started: {blackboard.campfire_started}");
        sb.AppendLine($"stick_picked: {blackboard.stick_picked}");
        sb.AppendLine($"marshmallow_skewered: {blackboard.marshmallow_skewered}");
        sb.AppendLine($"marshmallow_in_roasting_zone: {blackboard.marshmallow_in_roasting_zone}");
        sb.AppendLine($"roasting_duration_reached: {blackboard.roasting_duration_reached}");
        sb.AppendLine($"marshmallow_eaten: {blackboard.marshmallow_eaten}");
        sb.AppendLine($"roasting_time: {blackboard.currentRoastingTime:F2} / {blackboard.requiredRoastingDuration:F2}");
        sb.AppendLine($"currentCampfireInstance: {GetObjectName(blackboard.currentCampfireInstance)}");
        sb.AppendLine($"currentMarshmallowInstance: {GetObjectName(blackboard.currentMarshmallowInstance)}");
        sb.AppendLine();
    }

    private void AppendPerceptionSection()
    {
        sb.AppendLine("=== Perception Snapshot ===");

        if (perceptionBuilder == null)
        {
            sb.AppendLine("PerceptionBuilder: NULL");
            sb.AppendLine();
            return;
        }

        CP_PerceptionSnapshot snapshot = perceptionBuilder.GetLatestSnapshot();

        if (snapshot == null)
        {
            sb.AppendLine("LatestSnapshot: NULL");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(perceptionBuilder.GetLatestSnapshotSummary().TrimEnd());

        if (showSnapshotWarnings)
        {
            sb.AppendLine("Warnings:");
            string warningsText = perceptionBuilder.GetLatestWarningsText();
            sb.AppendLine(string.IsNullOrEmpty(warningsText) ? "No warnings." : warningsText.TrimEnd());
        }

        if (showSnapshotJson)
        {
            sb.AppendLine("JSON:");
            sb.AppendLine(perceptionBuilder.GetLatestSnapshotJson());
        }

        sb.AppendLine();
    }

    private void AppendPolicyClientSection()
    {
        sb.AppendLine("=== Python Client ===");

        if (pythonPolicyClient == null)
        {
            sb.AppendLine("PythonPolicyClient: NULL");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"pythonClientEnabled: {pythonPolicyClient.pythonClientEnabled}");
        sb.AppendLine($"serverUrl: {pythonPolicyClient.serverUrl}");
        sb.AppendLine($"requestInterval: {pythonPolicyClient.requestInterval:F2}");
        sb.AppendLine($"sendOnlyWhenReceiverIdle: {pythonPolicyClient.sendOnlyWhenReceiverIdle}");
        sb.AppendLine("latestClientSummary:");
        sb.AppendLine(string.IsNullOrEmpty(pythonPolicyClient.latestClientSummary) ? "None" : pythonPolicyClient.latestClientSummary.TrimEnd());
        sb.AppendLine("latestErrorText:");
        sb.AppendLine(string.IsNullOrEmpty(pythonPolicyClient.latestErrorText) ? "None" : pythonPolicyClient.latestErrorText.TrimEnd());
        sb.AppendLine();
    }

    private void AppendPolicyReceiverSection()
    {
        sb.AppendLine("=== Policy Receiver ===");

        if (policyReceiver == null)
        {
            sb.AppendLine("PolicyReceiver: NULL");
            sb.AppendLine();
            return;
        }

        CP_PolicyResponse response = policyReceiver.latestPolicyResponse;
        if (response == null)
        {
            sb.AppendLine("LatestPolicyResponse: NULL");
            sb.AppendLine("latestPolicySummary:");
            sb.AppendLine(string.IsNullOrEmpty(policyReceiver.latestPolicySummary) ? "None" : policyReceiver.latestPolicySummary.TrimEnd());
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"policySource: {GetPolicySource(response)}");
        sb.AppendLine($"executePolicy: {(response.control != null && response.control.execute_policy)}");
        sb.AppendLine($"policyId: {(response.policy != null ? response.policy.policy_id : string.Empty)}");
        sb.AppendLine($"activityMode: {(response.policy != null ? response.policy.activity_mode : string.Empty)}");
        sb.AppendLine($"serverName: {(response.debug != null ? response.debug.provider_name : string.Empty)}");
        sb.AppendLine($"aiStatus: {(response.debug != null ? response.debug.ai_status : string.Empty)}");
        sb.AppendLine($"aiErrorStatus: {(response.debug != null ? response.debug.ai_error_status : string.Empty)}");
        sb.AppendLine($"aiErrorMessage: {(response.debug != null ? response.debug.ai_error_message : string.Empty)}");
        sb.AppendLine($"provider: {(response.debug != null ? response.debug.provider : string.Empty)}");
        sb.AppendLine($"model: {(response.debug != null ? response.debug.model : string.Empty)}");
        sb.AppendLine($"latencyMs: {(response.debug != null ? response.debug.latency_ms.ToString("F0") : "0")}");
        sb.AppendLine($"fallbackReason: {(response.debug != null ? response.debug.fallback_reason : string.Empty)}");
        sb.AppendLine($"requestRole: {(response.debug != null ? response.debug.request_role : string.Empty)}");
        sb.AppendLine($"sourceStateEpoch: {(response.debug != null ? response.debug.source_state_epoch : -1)}");
        sb.AppendLine($"sourcePolicyIndex: {(response.debug != null ? response.debug.source_policy_index : -1)}");
        sb.AppendLine($"validatorErrors: {JoinOrNone(response.debug != null ? response.debug.validator_error_codes : null)}");
        sb.AppendLine($"validatorWarnings: {JoinOrNone(response.debug != null ? response.debug.validator_warnings : null)}");
        sb.AppendLine($"policyError: {(response.control != null && response.control.policy_error)}");
        sb.AppendLine("latestPolicySummary:");
        sb.AppendLine(string.IsNullOrEmpty(policyReceiver.latestPolicySummary) ? "None" : policyReceiver.latestPolicySummary.TrimEnd());
        sb.AppendLine();
    }

    private void AppendMotionExecutorSection()
    {
        sb.AppendLine("=== Motion Executor ===");

        if (motionExecutor == null)
        {
            sb.AppendLine("MotionExecutor: NULL");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"busy: {motionExecutor.IsBusy}");
        sb.AppendLine($"activeMotionGroupId: {motionExecutor.ActiveMotionGroupId}");
        sb.AppendLine($"lastCompletedMotionGroupId: {motionExecutor.LastCompletedMotionGroupId}");
        sb.AppendLine($"lastInterruptedMotionGroupId: {motionExecutor.LastInterruptedMotionGroupId}");
        sb.AppendLine($"activePolicyId: {motionExecutor.ActivePolicyId}");
        sb.AppendLine($"lastCompletedPolicyId: {motionExecutor.LastCompletedPolicyId}");
        sb.AppendLine($"lastInterruptedPolicyId: {motionExecutor.LastInterruptedPolicyId}");
        sb.AppendLine($"latestInterruptionReason: {motionExecutor.latestInterruptionReason}");
        sb.AppendLine($"activeController: {motionExecutor.activeControllerName}");
        sb.AppendLine($"activeControllerFailure: {motionExecutor.activeControllerFailureReason}");
        sb.AppendLine($"currentPrimitiveType: {motionExecutor.CurrentPrimitiveType}");
        sb.AppendLine($"currentPrimitiveAttempt: {motionExecutor.currentPrimitiveAttempt}");
        sb.AppendLine($"currentPrimitiveMaxAttempts: {motionExecutor.currentPrimitiveMaxAttempts}");

        CP_MovementController movementController = motionExecutor.movementController;
        if (movementController == null)
        {
            sb.AppendLine("movementController: NULL");
        }
        else
        {
            sb.AppendLine("=== Movement Controller ===");
            sb.AppendLine($"movementControllerBound: true");
            sb.AppendLine($"agentEnabled: {movementController.agentEnabled}");
            sb.AppendLine($"agentOnNavMesh: {movementController.agentOnNavMesh}");
            sb.AppendLine($"movementStatus: {movementController.status}");
            sb.AppendLine($"movementPrimitiveType: {movementController.activePrimitiveType}");
            sb.AppendLine($"movementScope: {movementController.activeScope}");
            sb.AppendLine($"movementTarget: {movementController.activeSemanticTarget}");
            sb.AppendLine($"lockedAnchorId: {movementController.lockedAnchorId}");
            sb.AppendLine($"movementDestination: {movementController.destinationWorldPosition}");
            sb.AppendLine($"pathStatus: {movementController.pathStatus}");
            sb.AppendLine($"pathPending: {movementController.pathPending}");
            sb.AppendLine($"remainingDistance: {movementController.remainingDistance:F2}");
            sb.AppendLine($"agentVelocity: {movementController.agentVelocity}");
            sb.AppendLine($"movementElapsedSeconds: {movementController.movementElapsedSeconds:F2}");
            sb.AppendLine($"movementTimeoutSeconds: {movementController.movementTimeoutSeconds:F2}");
            sb.AppendLine($"blockedTimer: {movementController.blockedTimer:F2}");
            sb.AppendLine($"internalRepathAttempted: {movementController.internalRepathAttempted}");
            sb.AppendLine($"lastMovementFailure: {movementController.lastFailureReason}");
        }

        sb.AppendLine("=== Animator Driver ===");
        if (companionAnimatorDriver == null)
        {
            sb.AppendLine("animatorDriver: NULL");
        }
        else
        {
            sb.AppendLine($"animatorDriverBound: {companionAnimatorDriver.animatorDriverBound}");
            sb.AppendLine($"controllerRequestsMovement: {companionAnimatorDriver.controllerRequestsMovement}");
            sb.AppendLine($"agentHasMovementIntent: {companionAnimatorDriver.agentHasMovementIntent}");
            sb.AppendLine($"animatorIsMoving: {companionAnimatorDriver.latestAppliedMoving}");
        }

        sb.AppendLine("=== Look Controller ===");
        if (lookController == null)
        {
            sb.AppendLine("lookController: NULL");
        }
        else
        {
            sb.AppendLine($"lookStatus: {lookController.Status}");
            sb.AppendLine($"explicitLookActive: {lookController.IsExplicitLookActive}");
            sb.AppendLine($"defaultLookRequested: {lookController.defaultLookRequested}");
            sb.AppendLine($"defaultLookControlling: {lookController.IsDefaultLookControlling}");
            sb.AppendLine($"currentLookMode: {lookController.currentLookMode}");
            sb.AppendLine($"currentTargetToken: {lookController.currentTargetToken}");
            sb.AppendLine($"targetResolved: {lookController.targetResolved}");
            sb.AppendLine($"resolvedTargetWorldPosition: {lookController.resolvedTargetWorldPosition}");
            sb.AppendLine($"currentHeadYawDegrees: {lookController.currentHeadYawDegrees:F2}");
            sb.AppendLine($"currentHeadPitchDegrees: {lookController.currentHeadPitchDegrees:F2}");
            sb.AppendLine($"bodyAssistActive: {lookController.bodyAssistActive}");
            sb.AppendLine($"rootYawOwner: {lookController.currentRootYawOwner}");
            sb.AppendLine($"lastLookFailure: {lookController.LastFailureReason}");
        }

        sb.AppendLine("=== Hand Pose Controller ===");
        if (handPoseController == null)
        {
            sb.AppendLine("handPoseController: NULL");
        }
        else
        {
            sb.AppendLine($"handRuntimeReady: {handPoseController.IsRuntimeReady}");
            sb.AppendLine($"handStatus: {handPoseController.status}");
            sb.AppendLine($"currentHandPose: {handPoseController.CurrentPose}");
            sb.AppendLine($"targetHandPose: {handPoseController.TargetPose}");
            sb.AppendLine($"handBlendProgress: {handPoseController.blendProgress:F2}");
            sb.AppendLine($"handTargetResolved: {handPoseController.poseTargetResolved}");
            sb.AppendLine($"rightHandWorldPosition: {handPoseController.currentHandWorldPosition}");
            sb.AppendLine($"handDistanceFromRoot: {handPoseController.distanceFromCompanionRoot:F2}");
            sb.AppendLine($"handHeightOffset: {handPoseController.heightOffsetFromBodyReference:F2}");
            sb.AppendLine($"lastHandFailure: {handPoseController.LastFailureReason}");
        }

        sb.AppendLine("=== Prop Controller ===");
        if (propController == null)
        {
            sb.AppendLine("propController: NULL");
        }
        else
        {
            sb.AppendLine($"propRuntimeReady: {propController.IsRuntimeReady}");
            sb.AppendLine($"propStatus: {propController.status}");
            sb.AppendLine($"hasCompanionStick: {propController.HasCompanionStick}");
            sb.AppendLine($"hasCompanionMarshmallow: {propController.HasCompanionMarshmallow}");
            sb.AppendLine($"currentStickState: {propController.CurrentStickState}");
            sb.AppendLine($"currentMarshmallowState: {propController.CurrentMarshmallowState}");
            sb.AppendLine($"lastPropCommand: {propController.LastCommand}");
            sb.AppendLine($"marshmallowLookTargetAvailable: {propController.TryGetCompanionMarshmallowLookTarget(out _)}");
            sb.AppendLine($"visualPhysicsUnsafe: {propController.visualPhysicsUnsafe}");
            sb.AppendLine($"lastPropFailure: {propController.LastFailureReason}");
        }
        sb.AppendLine("latestMotionSummary:");
        sb.AppendLine(string.IsNullOrEmpty(motionExecutor.latestMotionSummary) ? "None" : motionExecutor.latestMotionSummary.TrimEnd());
        sb.AppendLine();
    }

    private void AppendSchedulerSection()
    {
        sb.AppendLine("=== Runtime Scheduler ===");
        if (policyScheduler == null)
        {
            sb.AppendLine("PolicyScheduler: NULL");
        }
        else
        {
            sb.AppendLine($"schedulerState: {policyScheduler.schedulerState}");
            sb.AppendLine($"shutdownLatched: {policyScheduler.shutdownLatched}");
            sb.AppendLine($"policyErrorActive: {policyScheduler.policyErrorActive}");
            sb.AppendLine($"currentState: {policyScheduler.currentState}");
            sb.AppendLine($"stateEpoch: {policyScheduler.currentStateEpoch}");
            sb.AppendLine($"stateTransition: {policyScheduler.stateTransitionLabel}");
            sb.AppendLine($"currentExecutionContext: {policyScheduler.currentExecutionContextKey}");
            sb.AppendLine($"previousExecutionContext: {policyScheduler.previousExecutionContextKey}");
            sb.AppendLine($"executionContextReason: {policyScheduler.lastExecutionContextChangeReason}");
            sb.AppendLine($"lastRequestRole: {policyScheduler.lastRequestRole}");
            sb.AppendLine($"lastResponseRole: {policyScheduler.lastResponseRole}");
            sb.AppendLine($"requestInFlight: {(pythonPolicyClient != null && pythonPolicyClient.IsRequestInFlight)}");
            sb.AppendLine($"activeJobId: {policyScheduler.currentAiGenerationJobId}");
            sb.AppendLine($"activeJobEpoch: {policyScheduler.activeJobStateEpoch}");
            sb.AppendLine($"activeJobPolicyIndex: {policyScheduler.activeJobPolicyIndex}");
            sb.AppendLine($"currentPolicyIndex: {policyScheduler.currentPolicyIndex}");
            sb.AppendLine($"pipelineStarted: {policyScheduler.pipelineStarted}");
            sb.AppendLine($"pipelineRestartRequired: {policyScheduler.pipelineRestartRequired}");
            sb.AppendLine($"aiPolicyReady: {policyScheduler.AiPolicyReady}");
            sb.AppendLine($"motionGroupFinished: {policyScheduler.MotionGroupFinished}");
            sb.AppendLine($"barrierReady: {policyScheduler.barrierReady}");
            sb.AppendLine($"barrierCommitInProgress: {policyScheduler.barrierCommitInProgress}");
            sb.AppendLine($"policyErrorAutoRetry: {policyScheduler.automaticallyRetryPolicyError}");
            sb.AppendLine($"policyErrorRetryDelaySeconds: {policyScheduler.policyErrorRetryDelaySeconds:F2}");
            sb.AppendLine($"policyErrorRecoveryPending: {policyScheduler.policyErrorRecoveryPending}");
            sb.AppendLine($"policyErrorRetryAtTime: {policyScheduler.policyErrorRetryAtTime:F2}");
            sb.AppendLine($"policyErrorRetryRemainingSeconds: {policyScheduler.policyErrorRetryRemainingSeconds:F2}");
            sb.AppendLine($"activeMotionGroupId: {policyScheduler.activeMotionGroupId}");
            sb.AppendLine($"lastCompletedMotionGroupId: {policyScheduler.lastCompletedMotionGroupId}");
            sb.AppendLine($"lastInterruptedMotionGroupId: {policyScheduler.lastInterruptedMotionGroupId}");
            sb.AppendLine($"lastInterruptedMotionGroupReason: {policyScheduler.lastInterruptedMotionGroupReason}");
            sb.AppendLine($"requestPending: {policyScheduler.requestPending}");
            sb.AppendLine($"retryRole: {policyScheduler.pendingRequestRole}");
            sb.AppendLine($"retryReason: {policyScheduler.pendingRequestReason}");
            sb.AppendLine($"pendingSlotOccupied: {policyScheduler.pendingSlotOccupied}");
            sb.AppendLine($"pendingPayloadPresent: {policyScheduler.PendingPayloadPresent}");
            sb.AppendLine($"pendingStatus: {policyScheduler.pendingNextPolicyStatus}");
            sb.AppendLine($"pendingId: {policyScheduler.pendingPolicyId}");
            sb.AppendLine($"pendingEpoch: {policyScheduler.pendingPolicySourceStateEpoch}");
            sb.AppendLine($"pendingSourceIndex: {policyScheduler.pendingPolicySourceIndex}");
            sb.AppendLine($"pendingSource: {policyScheduler.pendingPolicySource}");
            sb.AppendLine($"pendingPriority: {policyScheduler.pendingPolicyPriority}");
            sb.AppendLine($"pendingAge: {policyScheduler.pendingPolicyAgeSeconds:F2}");
            sb.AppendLine($"pendingLifetime: {policyScheduler.pendingPolicyEffectiveLifetimeSeconds:F2}");
            sb.AppendLine($"pendingDiscardReason: {policyScheduler.pendingPolicyDiscardReason}");
            sb.AppendLine($"interruptionMode: {policyScheduler.stateChangeInterruptionMode}");
            sb.AppendLine($"interruptionType: {policyScheduler.lastInterruptionType}");
            sb.AppendLine($"forcedHardReason: {policyScheduler.forcedHardInterruptReason}");
            sb.AppendLine($"schedulerWarning: {policyScheduler.lastSchedulerWarning}");
            sb.AppendLine($"latestReceivedPolicyId: {(policyReceiver != null && policyReceiver.latestReceivedPolicyResponse != null && policyReceiver.latestReceivedPolicyResponse.policy != null ? policyReceiver.latestReceivedPolicyResponse.policy.policy_id : string.Empty)}");
            sb.AppendLine($"latestSanitizedPolicyId: {(policyReceiver != null && policyReceiver.latestPolicyResponse != null && policyReceiver.latestPolicyResponse.policy != null ? policyReceiver.latestPolicyResponse.policy.policy_id : string.Empty)}");
        }
        if (defaultAttentionController != null)
        {
            sb.AppendLine($"defaultAttentionActive: {defaultAttentionController.defaultAttentionActive}");
            sb.AppendLine($"defaultAttentionRequested: {defaultAttentionController.defaultAttentionRequested}");
            sb.AppendLine($"defaultAttentionControlling: {defaultAttentionController.defaultAttentionControlling}");
            sb.AppendLine($"defaultAttentionTarget: {defaultAttentionController.defaultAttentionTarget}");
            sb.AppendLine($"defaultAttentionReason: {defaultAttentionController.defaultAttentionReason}");
        }
        sb.AppendLine();
    }

    private void AppendSpatialSection()
    {
        sb.AppendLine("=== Spatial ===");

        if (interactionArea == null)
        {
            sb.AppendLine("InteractionArea: NULL");
        }
        else
        {
            sb.AppendLine($"companionInsideInteractionArea: {interactionArea.companionInsideArea}");
            sb.AppendLine($"distanceToInteractionArea: {interactionArea.companionDistanceToAreaCenter:F2}");
        }

        if (roastingAnchorManager == null)
        {
            sb.AppendLine("RoastingAnchorManager: NULL");
        }
        else
        {
            sb.AppendLine($"playerOccupiedAnchorId: {roastingAnchorManager.playerOccupiedAnchorId}");
            sb.AppendLine($"nearestFreeAnchorId: {roastingAnchorManager.nearestFreeAnchorId}");
            sb.AppendLine($"companionAtValidRoastingAnchor: {roastingAnchorManager.companionAtValidRoastingAnchor}");
            sb.AppendLine($"distanceToNearestFreeAnchor: {roastingAnchorManager.companionDistanceToNearestFreeAnchor:F2}");
        }

        sb.AppendLine();
    }

    private string GetObjectName(GameObject obj)
    {
        return obj == null ? "NULL" : obj.name;
    }

    private string JoinOrNone(System.Collections.Generic.List<string> values)
    {
        if (values == null || values.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", values);
    }

    private string GetPolicySource(CP_PolicyResponse response)
    {
        if (response == null || response.policy == null)
        {
            return string.Empty;
        }

        return response.policy.policy_source;
    }
}
