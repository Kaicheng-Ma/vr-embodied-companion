using System;
using UnityEngine;
using UnityEngine.AI;

public enum CP_PrimitiveExecutionStatus
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

[Serializable]
public sealed class CP_PrimitiveExecutionContext
{
    public int motionGroupId;
    public int primitiveIndex;
    public string policyId = string.Empty;
    public string policySource = string.Empty;
    public string currentState = string.Empty;
    public int stateEpoch;
    public string requestRole = string.Empty;
    public bool isTransitionPriority;
}

[Serializable]
public sealed class CP_NamedPointBinding
{
    public string id = string.Empty;
    public Transform target;
}

// Stage 5A movement controller:
// owns semantic target resolution, NavMesh path validation, arrival/timeout/blocked monitoring,
// and movement-only execution status. It does not own scheduling or Motion Group completion.
public class CP_MovementController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform companionRoot;
    [SerializeField] private NavMeshAgent navMeshAgent;
    [SerializeField] private Transform playerRoot;
    [SerializeField] private Transform playerHead;
    [SerializeField] private CP_InteractionArea interactionArea;
    [SerializeField] private CP_FishingInteractionArea fishingInteractionArea;
    [SerializeField] private CP_FishingAnchorManager fishingAnchorManager;
    [SerializeField] private CP_RoastingAnchorManager roastingAnchorManager;
    [SerializeField] private RoastingStateBlackboard blackboard;
    [SerializeField] private Transform fireCenterFallback;
    [SerializeField] private CP_NamedPointBinding[] namedPoints = Array.Empty<CP_NamedPointBinding>();

    [Header("Sampling / Validation")]
    [SerializeField] private float navMeshSampleRadius = 1.5f;
    [SerializeField] private float interactionAreaInteriorMargin = 0.15f;
    [SerializeField] private float interactionAreaArrivalSafetyBuffer = 0.05f;
    [SerializeField] private float anchorSampleRadius = 0.75f;
    [SerializeField] private float arrivalTolerance = 0.25f;
    [SerializeField] private float bodyRotationSpeedDegrees = 240f;
    [SerializeField] private float blockedDistanceEpsilon = 0.02f;
    [SerializeField] private float blockedTimeThreshold = 1.2f;
    [SerializeField] private float timeoutGraceSeconds = 0.75f;
    [SerializeField] private float movementTimeoutMultiplier = 1.75f;
    [SerializeField] private float minimumMovementTimeoutSeconds = 4.0f;
    [SerializeField] private float maximumMovementTimeoutSeconds = 20.0f;
    [SerializeField] private float initialSnapRadius = 1.5f;
    [SerializeField] private bool allowInitialNavMeshWarp = true;
    [SerializeField] private float playerMinimumDistance = 0.75f;
    [SerializeField] private float campfireDangerRadius = 0.8f;
    [SerializeField] private float minimumAroundPlayerRadius = 0.8f;
    [SerializeField] private float maximumAroundPlayerRadius = 4.0f;
    [SerializeField] private float minimumDestinationDisplacement = 0.35f;
    [SerializeField] private bool logMovement = true;

    [Header("Around Player Live Tracking")]
    [SerializeField] private bool enableAroundPlayerLiveTracking = true;
    [SerializeField] private float aroundPlayerRepathIntervalSeconds = 0.25f;
    [SerializeField] private float aroundPlayerPlayerMoveThreshold = 0.25f;
    [SerializeField] private float aroundPlayerDestinationChangeThreshold = 0.20f;
    [SerializeField] private float aroundPlayerArrivalDwellSeconds = 0.20f;

    [Header("Debug Gizmos")]
    [SerializeField] private bool showMovementGizmos = true;
    [SerializeField] private bool showMovementPathGizmo = true;
    [SerializeField] private bool showAroundPlayerRadiusGizmos = true;
    [SerializeField] private float destinationGizmoRadius = 0.15f;
    [SerializeField] private int radiusGizmoSegments = 40;

    [Header("Runtime Debug")]
    public CP_PrimitiveExecutionStatus status = CP_PrimitiveExecutionStatus.Idle;
    public string lastFailureReason = string.Empty;
    public string activePrimitiveType = string.Empty;
    public string activeScope = string.Empty;
    public string activeSemanticTarget = string.Empty;
    public string lockedAnchorId = string.Empty;
    public Vector3 destinationWorldPosition;
    public string pathStatus = string.Empty;
    public bool pathPending;
    public float remainingDistance;
    public Vector3 agentVelocity;
    public float movementElapsedSeconds;
    public float movementTimeoutSeconds;
    public float calculatedPathLength;
    public float estimatedTravelTimeSeconds;
    public float blockedTimer;
    public bool internalRepathAttempted;
    public string lastCancellationReason = string.Empty;
    public bool agentEnabled;
    public bool agentOnNavMesh;
    public float effectiveInteractionAreaInteriorMargin;
    public float effectiveInteractionAreaSafeRadius;
    public bool aroundPlayerLiveTrackingActive;
    public int aroundPlayerRepathCount;
    public string lastAroundPlayerRepathReason = string.Empty;
    public Vector3 lastTrackedPlayerPosition;
    public Vector3 lockedAroundPlayerOffset;
    public float aroundPlayerDistanceToPlayer;
    public float destinationMovedSinceLastRepath;
    public bool aroundPlayerInsideAllowedBand;
    public float aroundPlayerArrivalDwellProgress;

    private CP_MotionPrimitive activePrimitive;
    private CP_PrimitiveExecutionContext activeContext;
    private Vector3 holdWorldPosition;
    private float holdDurationSeconds;
    private Vector3 lastProgressPosition;
    private bool usesAgentPath;
    private float activeAroundPlayerRadiusMin;
    private float activeAroundPlayerRadiusMax;
    private float aroundPlayerRepathTimer;
    private float aroundPlayerArrivalDwellTimer;
    private bool hasLockedAroundPlayerOffset;
    private Vector3 lastAroundPlayerDestination;
    private bool hasDestination;

    public CP_PrimitiveExecutionStatus Status => status;
    public string LastFailureReason => lastFailureReason;
    public bool IsRunning => status == CP_PrimitiveExecutionStatus.Running;

    private void Awake()
    {
        ApplyAgentOwnershipSettings();
        RefreshAgentDebug();
    }

    private void Update()
    {
        RefreshAgentDebug();
    }

    public bool TryBegin(CP_MotionPrimitive primitive, CP_PrimitiveExecutionContext context, out string failureReason)
    {
        failureReason = string.Empty;

        if (primitive == null)
        {
            failureReason = "movement_primitive_missing";
            SetFailedState(failureReason);
            return false;
        }

        if (IsRunning)
        {
            failureReason = "movement_already_running";
            SetFailedState(failureReason);
            return false;
        }

        if (!ValidateSetupAndAgent(out failureReason))
        {
            SetFailedState(failureReason);
            return false;
        }

        activePrimitive = ClonePrimitive(primitive);
        activeContext = context ?? new CP_PrimitiveExecutionContext();
        activePrimitiveType = Normalize(activePrimitive.type);
        activeScope = Normalize(activePrimitive.scope);
        activeSemanticTarget = Normalize(activePrimitive.target);
        lockedAnchorId = string.Empty;
        lastFailureReason = string.Empty;
        lastCancellationReason = string.Empty;
        internalRepathAttempted = false;
        blockedTimer = 0f;
        movementElapsedSeconds = 0f;
        movementTimeoutSeconds = 0f;
        calculatedPathLength = 0f;
        estimatedTravelTimeSeconds = 0f;
        lastProgressPosition = companionRoot.position;
        destinationWorldPosition = companionRoot.position;
        hasDestination = false;
        holdWorldPosition = companionRoot.position;
        holdDurationSeconds = Mathf.Max(0.1f, activePrimitive.duration);
        usesAgentPath = false;
        pathStatus = "idle";
        ResetAroundPlayerTrackingState();

        if (activePrimitiveType == "hold_position")
        {
            BeginHoldPosition();
            status = CP_PrimitiveExecutionStatus.Running;
            return true;
        }

        if (activePrimitiveType != "move_within_scope")
        {
            failureReason = "movement_primitive_type_unsupported";
            SetFailedState(failureReason);
            return false;
        }

        if (!TryResolveMovementDestination(activePrimitive, activeContext, out Vector3 resolvedDestination, out failureReason))
        {
            SetFailedState(failureReason);
            return false;
        }

        NavMeshPath calculatedPath = new NavMeshPath();
        NavMeshQueryFilter queryFilter = BuildNavMeshQueryFilter();
        if (!NavMesh.CalculatePath(companionRoot.position, resolvedDestination, queryFilter, calculatedPath))
        {
            failureReason = "path_calculation_failed";
            SetFailedState(failureReason);
            return false;
        }

        if (calculatedPath.status != NavMeshPathStatus.PathComplete)
        {
            failureReason = "path_not_complete";
            SetFailedState(failureReason);
            return false;
        }

        float safeMultiplier = Mathf.Max(1.0f, movementTimeoutMultiplier);
        float safeMinimumTimeout = Mathf.Max(0.1f, minimumMovementTimeoutSeconds);
        float safeMaximumTimeout = Mathf.Max(safeMinimumTimeout, maximumMovementTimeoutSeconds);
        float safeGrace = Mathf.Max(0.0f, timeoutGraceSeconds);
        float safeAgentSpeed = Mathf.Max(0.1f, navMeshAgent.speed);
        float pathLength = CalculatePathLength(calculatedPath);
        float estimatedTravelTime = pathLength / safeAgentSpeed;
        float basePrimitiveDuration = Mathf.Max(0.1f, activePrimitive.duration);
        float estimatedBudget = estimatedTravelTime * safeMultiplier;

        calculatedPathLength = pathLength;
        estimatedTravelTimeSeconds = estimatedTravelTime;
        movementTimeoutSeconds = Mathf.Clamp(
            Mathf.Max(basePrimitiveDuration, estimatedBudget) + safeGrace,
            safeMinimumTimeout,
            safeMaximumTimeout);

        destinationWorldPosition = resolvedDestination;
        hasDestination = true;
        pathStatus = calculatedPath.status.ToString();
        SafeResetPath();
        navMeshAgent.isStopped = false;
        if (!navMeshAgent.SetDestination(destinationWorldPosition))
        {
            failureReason = "set_destination_failed";
            SetFailedState(failureReason);
            return false;
        }

        usesAgentPath = true;
        status = CP_PrimitiveExecutionStatus.Running;
        if (activeScope == "around_player")
        {
            BeginAroundPlayerLiveTracking(destinationWorldPosition);
        }

        return true;
    }

    public CP_PrimitiveExecutionStatus Tick(float deltaTime)
    {
        RefreshAgentDebug();

        if (!IsRunning)
        {
            return status;
        }

        movementElapsedSeconds += Mathf.Max(0f, deltaTime);

        if (activePrimitiveType == "hold_position")
        {
            MaintainHoldPosition();
            if (movementElapsedSeconds >= holdDurationSeconds)
            {
                ResetAroundPlayerTrackingState();
                hasDestination = false;
                status = CP_PrimitiveExecutionStatus.Succeeded;
            }

            return status;
        }

        if (!ValidateSetupAndAgent(out string failureReason))
        {
            SetFailedState(failureReason);
            return status;
        }

        UpdateMovementDebug();
        UpdateAroundPlayerLiveTracking(deltaTime);
        if (!IsRunning)
        {
            return status;
        }

        RotateBodyTowardMovement(deltaTime);

        if (ShouldCancelLockedAnchorBecausePlayerOccupied())
        {
            SetFailedState("target_became_player_occupied");
            return status;
        }

        if (IsArrivalSatisfied())
        {
            SafeResetPath();
            navMeshAgent.isStopped = true;
            ResetAroundPlayerTrackingState();
            hasDestination = false;
            status = CP_PrimitiveExecutionStatus.Succeeded;
            return status;
        }

        if (movementElapsedSeconds >= movementTimeoutSeconds)
        {
            SetFailedState("movement_timeout");
            return status;
        }

        UpdateBlockedState(deltaTime);
        return status;
    }

    public void Cancel(string reason)
    {
        SafeResetPath();
        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            navMeshAgent.isStopped = true;
        }

        usesAgentPath = false;
        calculatedPathLength = 0f;
        estimatedTravelTimeSeconds = 0f;
        ResetAroundPlayerTrackingState();
        hasDestination = false;
        lastCancellationReason = string.IsNullOrWhiteSpace(reason) ? "movement_cancelled" : reason;
        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastFailureReason = lastCancellationReason;
    }

    private void OnDisable()
    {
        ResetAroundPlayerTrackingState();
        hasDestination = false;
    }

    private void BeginHoldPosition()
    {
        SafeResetPath();
        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            navMeshAgent.isStopped = true;
        }

        usesAgentPath = false;
        ResetAroundPlayerTrackingState();
        holdWorldPosition = companionRoot.position;
        destinationWorldPosition = holdWorldPosition;
        hasDestination = true;
        movementTimeoutSeconds = holdDurationSeconds;
        calculatedPathLength = 0f;
        estimatedTravelTimeSeconds = 0f;
        pathStatus = "hold_position";
    }

    private void MaintainHoldPosition()
    {
        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            navMeshAgent.isStopped = true;
            SafeResetPath();
        }

        pathStatus = "hold_position";
        remainingDistance = 0f;
        agentVelocity = Vector3.zero;
    }

    private bool TryResolveMovementDestination(
        CP_MotionPrimitive primitive,
        CP_PrimitiveExecutionContext context,
        out Vector3 resolvedDestination,
        out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        string scope = Normalize(primitive.scope);
        switch (scope)
        {
            case "around_player":
                return TryResolveAroundPlayerDestination(primitive, out resolvedDestination, out failureReason);
            case "interaction_area":
                if (IsFishingInteractionAreaContext(context))
                {
                    return TryResolveFishingInteractionAreaDestination(true, out resolvedDestination, out failureReason);
                }
                return TryResolveInteractionAreaDestination(true, out resolvedDestination, out failureReason);
            case "named_point":
                return TryResolveNamedPointDestination(primitive.target, out resolvedDestination, out failureReason);
            case "transition_only":
                return TryResolveTransitionDestination(primitive, context, out resolvedDestination, out failureReason);
            default:
                failureReason = "movement_scope_unsupported";
                return false;
        }
    }

    private bool TryResolveAroundPlayerDestination(CP_MotionPrimitive primitive, out Vector3 resolvedDestination, out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        if (playerRoot == null)
        {
            failureReason = "player_root_missing";
            return false;
        }

        float radiusMin = GetClampedAroundPlayerRadiusMin(primitive);
        float radiusMax = GetClampedAroundPlayerRadiusMax(primitive, radiusMin);
        Vector3 playerPosition = playerRoot.position;
        Vector3 toCompanion = companionRoot.position - playerPosition;
        toCompanion.y = 0f;
        if (toCompanion.sqrMagnitude < 0.001f)
        {
            toCompanion = playerRoot.forward.sqrMagnitude > 0.001f ? playerRoot.forward : Vector3.forward;
            toCompanion.y = 0f;
        }

        Vector3 baseDirection = toCompanion.normalized;
        float[] angleOffsets = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 145f, -145f };
        float[] radii = { radiusMin, (radiusMin + radiusMax) * 0.5f, radiusMax };

        for (int radiusIndex = 0; radiusIndex < radii.Length; radiusIndex++)
        {
            for (int angleIndex = 0; angleIndex < angleOffsets.Length; angleIndex++)
            {
                Vector3 candidateDirection = Quaternion.AngleAxis(angleOffsets[angleIndex], Vector3.up) * baseDirection;
                Vector3 candidate = playerPosition + candidateDirection.normalized * radii[radiusIndex];
                if (!IsCandidateSpatiallySafe(candidate))
                {
                    continue;
                }

                if (TryAcceptAroundPlayerCandidate(candidate, radiusMin, radiusMax, out resolvedDestination))
                {
                    return true;
                }
            }
        }

        failureReason = "around_player_destination_unresolved";
        return false;
    }

    private bool TryResolveInteractionAreaDestination(bool requireMeaningfulDisplacement, out Vector3 resolvedDestination, out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        if (interactionArea == null)
        {
            failureReason = "interaction_area_missing";
            return false;
        }

        float effectiveInteriorMargin = GetEffectiveInteractionAreaInteriorMargin();
        effectiveInteractionAreaInteriorMargin = effectiveInteriorMargin;
        effectiveInteractionAreaSafeRadius = GetInteractionAreaSafeRadius(effectiveInteriorMargin);
        if (effectiveInteractionAreaSafeRadius <= 0.01f)
        {
            failureReason = "interaction_area_safe_interior_unavailable";
            return false;
        }

        if (!interactionArea.TryGetNearestInteriorPoint(companionRoot.position, effectiveInteriorMargin, out Vector3 nearestInteriorCandidate))
        {
            failureReason = "interaction_area_point_unresolved";
            return false;
        }

        string lastCandidateFailure = "interaction_area_navmesh_projection_failed";
        if (TryAcceptInteractionAreaCandidate(nearestInteriorCandidate, effectiveInteriorMargin, requireMeaningfulDisplacement, out resolvedDestination, out lastCandidateFailure))
        {
            return true;
        }

        Vector3 center = interactionArea.CenterWorldPosition;
        float safeRadius = effectiveInteractionAreaSafeRadius;
        float ringRadius = safeRadius * 0.7f;
        Vector3[] directions =
        {
            Vector3.forward,
            Vector3.right,
            Vector3.back,
            Vector3.left,
            (Vector3.forward + Vector3.right).normalized,
            (Vector3.back + Vector3.right).normalized,
            (Vector3.back + Vector3.left).normalized,
            (Vector3.forward + Vector3.left).normalized,
        };

        int startIndex = ResolveCandidateStartIndex(requireMeaningfulDisplacement, directions.Length);

        for (int offset = 0; offset < directions.Length; offset++)
        {
            int candidateIndex = requireMeaningfulDisplacement
                ? (startIndex + offset) % directions.Length
                : offset;
            Vector3 candidate = center + directions[candidateIndex] * ringRadius;
            candidate.y = center.y;
            if (TryAcceptInteractionAreaCandidate(candidate, effectiveInteriorMargin, requireMeaningfulDisplacement, out resolvedDestination, out lastCandidateFailure))
            {
                return true;
            }
        }

        if (TryAcceptInteractionAreaCandidate(center, effectiveInteriorMargin, requireMeaningfulDisplacement, out resolvedDestination, out lastCandidateFailure))
        {
            return true;
        }

        failureReason = lastCandidateFailure == "interaction_area_point_unresolved"
            ? lastCandidateFailure
            : "no_valid_interaction_area_navmesh_point";
        return false;
    }

    // Fishing Stage C2 reuses the shared movement safety path while keeping
    // geometry ownership inside CP_FishingInteractionArea and NavMesh ownership here.
    private bool TryResolveFishingInteractionAreaDestination(bool requireMeaningfulDisplacement, out Vector3 resolvedDestination, out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        if (fishingInteractionArea == null)
        {
            failureReason = "fishing_interaction_area_missing";
            return false;
        }

        float effectiveInteriorMargin = GetEffectiveInteractionAreaInteriorMargin();
        if (Mathf.Max(0f, fishingInteractionArea.Radius - effectiveInteriorMargin) <= 0.01f)
        {
            failureReason = "fishing_interaction_area_safe_interior_unavailable";
            return false;
        }

        if (!fishingInteractionArea.TryGetNearestInteriorPoint(companionRoot.position, effectiveInteriorMargin, out Vector3 nearestInteriorCandidate))
        {
            failureReason = "fishing_interaction_area_point_unresolved";
            return false;
        }

        string lastCandidateFailure = "fishing_interaction_area_navmesh_projection_failed";
        if (TryAcceptFishingInteractionAreaCandidate(nearestInteriorCandidate, effectiveInteriorMargin, requireMeaningfulDisplacement, out resolvedDestination, out lastCandidateFailure))
        {
            return true;
        }

        Vector3 center = fishingInteractionArea.GetAreaCenterWorldPosition();
        float safeRadius = Mathf.Max(0f, fishingInteractionArea.Radius - Mathf.Max(0f, effectiveInteriorMargin));
        float ringRadius = safeRadius * 0.7f;
        Vector3[] directions =
        {
            Vector3.forward,
            Vector3.right,
            Vector3.back,
            Vector3.left,
            (Vector3.forward + Vector3.right).normalized,
            (Vector3.back + Vector3.right).normalized,
            (Vector3.back + Vector3.left).normalized,
            (Vector3.forward + Vector3.left).normalized,
        };

        int startIndex = ResolveCandidateStartIndex(requireMeaningfulDisplacement, directions.Length);
        for (int offset = 0; offset < directions.Length; offset++)
        {
            int candidateIndex = requireMeaningfulDisplacement
                ? (startIndex + offset) % directions.Length
                : offset;
            Vector3 candidate = center + directions[candidateIndex] * ringRadius;
            candidate.y = center.y;
            if (TryAcceptFishingInteractionAreaCandidate(candidate, effectiveInteriorMargin, requireMeaningfulDisplacement, out resolvedDestination, out lastCandidateFailure))
            {
                return true;
            }
        }

        if (TryAcceptFishingInteractionAreaCandidate(center, effectiveInteriorMargin, requireMeaningfulDisplacement, out resolvedDestination, out lastCandidateFailure))
        {
            return true;
        }

        failureReason = lastCandidateFailure == "fishing_interaction_area_point_unresolved"
            ? lastCandidateFailure
            : "no_valid_fishing_interaction_area_navmesh_point";
        return false;
    }

    private int ResolveCandidateStartIndex(bool requireMeaningfulDisplacement, int candidateCount)
    {
        // Normal meaningful repositioning rotates deterministic candidate order
        // by Motion Group. Entry movement keeps the stable zero-based order.
        if (!requireMeaningfulDisplacement || activeContext == null || candidateCount <= 0)
        {
            return 0;
        }

        int rawIndex = activeContext.motionGroupId;
        return ((rawIndex % candidateCount) + candidateCount) % candidateCount;
    }

    private bool TryResolveNamedPointDestination(string pointId, out Vector3 resolvedDestination, out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        Transform pointTransform = FindNamedPoint(pointId);
        if (pointTransform == null)
        {
            failureReason = "named_point_missing";
            return false;
        }

        if (!TryProjectOntoNavMesh(pointTransform.position, navMeshSampleRadius, out resolvedDestination))
        {
            failureReason = "named_point_navmesh_projection_failed";
            return false;
        }

        if (!IsPathComplete(resolvedDestination))
        {
            failureReason = "named_point_path_invalid";
            return false;
        }

        return true;
    }

    private bool TryResolveTransitionDestination(
        CP_MotionPrimitive primitive,
        CP_PrimitiveExecutionContext context,
        out Vector3 resolvedDestination,
        out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        string target = Normalize(primitive.target);
        string state = Normalize(context != null ? context.currentState : string.Empty);

        if (state == Normalize(CP_StateNames.S2) && target == "nearest_valid_interaction_area_point")
        {
            return TryResolveInteractionAreaDestination(false, out resolvedDestination, out failureReason);
        }

        if (target == "nearest_valid_fishing_interaction_area_point")
        {
            return TryResolveFishingInteractionAreaDestination(false, out resolvedDestination, out failureReason);
        }

        if (target == "assigned_fishing_anchor")
        {
            return TryResolveAssignedFishingAnchorDestination(out resolvedDestination, out failureReason);
        }

        if (state == Normalize(CP_StateNames.S3) && target == "nearest_free_roasting_anchor")
        {
            if (roastingAnchorManager == null)
            {
                failureReason = "roasting_anchor_manager_missing";
                return false;
            }

            string anchorId = roastingAnchorManager.nearestFreeAnchorId;
            if (string.IsNullOrWhiteSpace(anchorId))
            {
                failureReason = "nearest_free_anchor_missing";
                return false;
            }

            if (!roastingAnchorManager.TryGetAnchorTransform(anchorId, out Transform anchorTransform) || anchorTransform == null)
            {
                failureReason = "roasting_anchor_transform_missing";
                return false;
            }

            if (!TryProjectOntoNavMesh(anchorTransform.position, anchorSampleRadius, out resolvedDestination))
            {
                failureReason = "roasting_anchor_navmesh_projection_failed";
                return false;
            }

            float allowedProjectionDistance = Mathf.Max(arrivalTolerance, roastingAnchorManager.companionArriveDistance);
            float projectedAnchorDistanceXZ = DistanceXZ(resolvedDestination, anchorTransform.position);
            if (projectedAnchorDistanceXZ > allowedProjectionDistance)
            {
                failureReason = "roasting_anchor_projection_outside_arrive_distance";
                return false;
            }

            if (!IsPathComplete(resolvedDestination))
            {
                failureReason = "roasting_anchor_path_invalid";
                return false;
            }

            lockedAnchorId = anchorId;
            return true;
        }

        failureReason = "transition_target_unsupported";
        return false;
    }

    private bool TryResolveAssignedFishingAnchorDestination(out Vector3 resolvedDestination, out string failureReason)
    {
        resolvedDestination = companionRoot.position;
        failureReason = string.Empty;

        if (fishingAnchorManager == null)
        {
            failureReason = "fishing_anchor_manager_missing";
            return false;
        }

        if (!fishingAnchorManager.HasFishingAssignment)
        {
            failureReason = "assigned_fishing_anchor_missing";
            return false;
        }

        if (!fishingAnchorManager.TryGetAssignedAnchorTransform(out Transform assignedAnchorTransform) || assignedAnchorTransform == null)
        {
            failureReason = "assigned_fishing_anchor_transform_missing";
            return false;
        }

        if (!TryProjectOntoNavMesh(assignedAnchorTransform.position, anchorSampleRadius, out resolvedDestination))
        {
            failureReason = "assigned_fishing_anchor_navmesh_projection_failed";
            return false;
        }

        float allowedProjectionDistance = Mathf.Max(arrivalTolerance, fishingAnchorManager.AnchorArrivalTolerance);
        float projectedAnchorDistanceXZ = DistanceXZ(resolvedDestination, assignedAnchorTransform.position);
        if (projectedAnchorDistanceXZ > allowedProjectionDistance)
        {
            failureReason = "assigned_fishing_anchor_projection_outside_arrive_distance";
            return false;
        }

        if (!IsPathComplete(resolvedDestination))
        {
            failureReason = "assigned_fishing_anchor_path_invalid";
            return false;
        }

        lockedAnchorId = fishingAnchorManager.AssignedFishingAnchorId;
        return true;
    }

    private bool ValidateSetupAndAgent(out string failureReason)
    {
        failureReason = string.Empty;
        if (companionRoot == null)
        {
            failureReason = "companion_root_missing";
            return false;
        }

        if (navMeshAgent == null)
        {
            failureReason = "navmesh_agent_missing";
            return false;
        }

        if (!navMeshAgent.enabled)
        {
            failureReason = "navmesh_agent_disabled";
            return false;
        }

        ApplyAgentOwnershipSettings();
        if (!EnsureAgentOnNavMesh(out failureReason))
        {
            return false;
        }

        Rigidbody body = companionRoot.GetComponent<Rigidbody>();
        if (body != null && (!body.isKinematic || body.useGravity))
        {
            if (logMovement)
            {
                Debug.LogWarning("CP_MovementController expects CompanionRoot Rigidbody to be kinematic with gravity disabled for NavMesh control.", this);
            }
        }

        NPCAnimationTest keyboardMovement = companionRoot.GetComponent<NPCAnimationTest>();
        if (keyboardMovement != null && keyboardMovement.enabled && keyboardMovement.movementEnabled)
        {
            failureReason = "temporary_keyboard_movement_active";
            return false;
        }

        return true;
    }

    private void ApplyAgentOwnershipSettings()
    {
        if (navMeshAgent == null)
        {
            return;
        }

        navMeshAgent.updatePosition = true;
        navMeshAgent.updateRotation = false;
        navMeshAgent.updateUpAxis = true;
        navMeshAgent.autoTraverseOffMeshLink = false;
        navMeshAgent.autoRepath = true;
    }

    private bool EnsureAgentOnNavMesh(out string failureReason)
    {
        failureReason = string.Empty;
        RefreshAgentDebug();
        if (agentOnNavMesh)
        {
            return true;
        }

        if (!allowInitialNavMeshWarp || IsRunning)
        {
            failureReason = "agent_not_on_navmesh";
            return false;
        }

        if (NavMesh.SamplePosition(companionRoot.position, out NavMeshHit hit, initialSnapRadius, navMeshAgent.areaMask))
        {
            navMeshAgent.Warp(hit.position);
            RefreshAgentDebug();
            if (agentOnNavMesh)
            {
                return true;
            }
        }

        failureReason = "agent_not_on_navmesh";
        return false;
    }

    private void UpdateMovementDebug()
    {
        pathPending = navMeshAgent.pathPending;
        remainingDistance = navMeshAgent.remainingDistance;
        agentVelocity = navMeshAgent.velocity;
        pathStatus = navMeshAgent.hasPath ? navMeshAgent.pathStatus.ToString() : "NoPath";
    }

    private void UpdateBlockedState(float deltaTime)
    {
        Vector3 currentPosition = companionRoot.position;
        float movedDistance = Vector3.Distance(currentPosition, lastProgressPosition);
        bool stuck = movedDistance < blockedDistanceEpsilon &&
                     navMeshAgent.velocity.magnitude < 0.05f &&
                     (!navMeshAgent.pathPending && navMeshAgent.remainingDistance > Mathf.Max(arrivalTolerance, navMeshAgent.stoppingDistance));

        if (!stuck)
        {
            blockedTimer = 0f;
            lastProgressPosition = currentPosition;
            return;
        }

        blockedTimer += Mathf.Max(0f, deltaTime);
        if (blockedTimer < blockedTimeThreshold)
        {
            return;
        }

        if (!internalRepathAttempted)
        {
            internalRepathAttempted = true;
            blockedTimer = 0f;
            SafeResetPath();
            navMeshAgent.isStopped = false;
            if (!navMeshAgent.SetDestination(destinationWorldPosition))
            {
                SetFailedState("repath_set_destination_failed");
            }
            return;
        }

        SetFailedState("movement_blocked");
    }

    private bool IsArrivalSatisfied()
    {
        if (!usesAgentPath)
        {
            return false;
        }

        if (activeScope == "around_player")
        {
            return IsAroundPlayerArrivalSatisfied();
        }

        // Normal interaction-area movement must wait for the NavMesh path to resolve
        // before the already-inside-area case can be treated as arrived.
        if (activeScope == "interaction_area")
        {
            bool insideArea = IsFishingInteractionAreaContext(activeContext)
                ? fishingInteractionArea != null && fishingInteractionArea.ContainsXZ(companionRoot.position)
                : interactionArea != null && interactionArea.ContainsXZ(companionRoot.position);
            return insideArea &&
                   !navMeshAgent.pathPending &&
                   navMeshAgent.remainingDistance <= Mathf.Max(arrivalTolerance, navMeshAgent.stoppingDistance);
        }

        if (activeScope == "transition_only" && activeSemanticTarget == "nearest_valid_interaction_area_point")
        {
            return interactionArea != null &&
                   interactionArea.ContainsXZ(companionRoot.position) &&
                   navMeshAgent.remainingDistance <= Mathf.Max(arrivalTolerance, navMeshAgent.stoppingDistance);
        }

        if (activeScope == "transition_only" && activeSemanticTarget == "nearest_valid_fishing_interaction_area_point")
        {
            return fishingInteractionArea != null &&
                   fishingInteractionArea.ContainsXZ(companionRoot.position) &&
                   navMeshAgent.remainingDistance <= Mathf.Max(arrivalTolerance, navMeshAgent.stoppingDistance);
        }

        if (activeScope == "transition_only" && activeSemanticTarget == "assigned_fishing_anchor")
        {
            if (string.IsNullOrWhiteSpace(lockedAnchorId) || fishingAnchorManager == null)
            {
                return false;
            }

            Transform assignedAnchorTransform = fishingAnchorManager.GetAnchorByName(lockedAnchorId);
            if (assignedAnchorTransform == null)
            {
                return false;
            }

            return DistanceXZ(companionRoot.position, assignedAnchorTransform.position) <= Mathf.Max(arrivalTolerance, fishingAnchorManager.AnchorArrivalTolerance);
        }

        if (activeScope == "transition_only" && activeSemanticTarget == "nearest_free_roasting_anchor")
        {
            if (string.IsNullOrWhiteSpace(lockedAnchorId) || roastingAnchorManager == null)
            {
                return false;
            }

            if (roastingAnchorManager.IsPlayerOccupiedAnchor(lockedAnchorId))
            {
                return false;
            }

            if (!roastingAnchorManager.TryGetAnchorTransform(lockedAnchorId, out Transform anchorTransform) || anchorTransform == null)
            {
                return false;
            }

            return DistanceXZ(companionRoot.position, anchorTransform.position) <= Mathf.Max(arrivalTolerance, roastingAnchorManager.companionArriveDistance);
        }

        return !navMeshAgent.pathPending &&
               navMeshAgent.remainingDistance <= Mathf.Max(arrivalTolerance, navMeshAgent.stoppingDistance);
    }

    private bool ShouldCancelLockedAnchorBecausePlayerOccupied()
    {
        return activeScope == "transition_only" &&
               activeSemanticTarget == "nearest_free_roasting_anchor" &&
               !string.IsNullOrWhiteSpace(lockedAnchorId) &&
               roastingAnchorManager != null &&
               roastingAnchorManager.IsPlayerOccupiedAnchor(lockedAnchorId);
    }

    private void RotateBodyTowardMovement(float deltaTime)
    {
        Vector3 desiredDirection = navMeshAgent.desiredVelocity;
        desiredDirection.y = 0f;
        if (desiredDirection.sqrMagnitude < 0.0001f)
        {
            Vector3 toDestination = destinationWorldPosition - companionRoot.position;
            toDestination.y = 0f;
            desiredDirection = toDestination;
        }

        if (desiredDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion desiredRotation = Quaternion.LookRotation(desiredDirection.normalized, Vector3.up);
        companionRoot.rotation = Quaternion.RotateTowards(
            companionRoot.rotation,
            desiredRotation,
            bodyRotationSpeedDegrees * Mathf.Max(0f, deltaTime)
        );
    }

    private bool IsCandidateSpatiallySafe(Vector3 candidate)
    {
        if (playerRoot != null && DistanceXZ(candidate, playerRoot.position) < playerMinimumDistance)
        {
            return false;
        }

        Vector3 fireCenter = ResolveFireCenter();
        if (fireCenter != Vector3.zero && DistanceXZ(candidate, fireCenter) < campfireDangerRadius)
        {
            return false;
        }

        return true;
    }

    private Vector3 ResolveFireCenter()
    {
        if (blackboard != null)
        {
            if (blackboard.currentCampfireInstance != null)
            {
                Transform fireCenter = FindChildByName(blackboard.currentCampfireInstance.transform, "FireCenterPoint");
                if (fireCenter != null)
                {
                    return fireCenter.position;
                }
            }

            if (blackboard.campfireSpawnPoint != null)
            {
                return blackboard.campfireSpawnPoint.position;
            }
        }

        return fireCenterFallback != null ? fireCenterFallback.position : Vector3.zero;
    }

    private Transform FindNamedPoint(string pointId)
    {
        string normalizedId = Normalize(pointId);
        for (int i = 0; i < namedPoints.Length; i++)
        {
            CP_NamedPointBinding binding = namedPoints[i];
            if (binding == null || binding.target == null)
            {
                continue;
            }

            if (Normalize(binding.id) == normalizedId)
            {
                return binding.target;
            }
        }

        return null;
    }

    private bool TryProjectOntoNavMesh(Vector3 candidate, float sampleRadius, out Vector3 projected)
    {
        projected = candidate;
        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, Mathf.Max(0.05f, sampleRadius), navMeshAgent.areaMask))
        {
            projected = hit.position;
            return true;
        }

        return false;
    }

    private bool TryAcceptAroundPlayerCandidate(
        Vector3 semanticCandidate,
        float radiusMin,
        float radiusMax,
        out Vector3 projectedDestination)
    {
        projectedDestination = companionRoot.position;

        if (!TryProjectOntoNavMesh(semanticCandidate, navMeshSampleRadius, out projectedDestination))
        {
            return false;
        }

        if (!IsCandidateSpatiallySafe(projectedDestination))
        {
            return false;
        }

        if (!IsAroundPlayerDestinationInsideBand(projectedDestination, playerRoot != null ? playerRoot.position : Vector3.zero, radiusMin, radiusMax))
        {
            return false;
        }

        if (DistanceXZ(projectedDestination, companionRoot.position) < minimumDestinationDisplacement)
        {
            return false;
        }

        return IsPathComplete(projectedDestination);
    }

    private void BeginAroundPlayerLiveTracking(Vector3 acceptedDestination)
    {
        ResetAroundPlayerTrackingState();

        if (!enableAroundPlayerLiveTracking || playerRoot == null || activePrimitive == null)
        {
            return;
        }

        activeAroundPlayerRadiusMin = GetClampedAroundPlayerRadiusMin(activePrimitive);
        activeAroundPlayerRadiusMax = GetClampedAroundPlayerRadiusMax(activePrimitive, activeAroundPlayerRadiusMin);
        lastTrackedPlayerPosition = playerRoot.position;
        lastAroundPlayerDestination = acceptedDestination;
        lockedAroundPlayerOffset = acceptedDestination - lastTrackedPlayerPosition;
        lockedAroundPlayerOffset.y = 0f;
        hasLockedAroundPlayerOffset = lockedAroundPlayerOffset.sqrMagnitude > 0.0001f;
        aroundPlayerLiveTrackingActive = hasLockedAroundPlayerOffset;
        aroundPlayerDistanceToPlayer = DistanceXZ(companionRoot.position, lastTrackedPlayerPosition);
        aroundPlayerInsideAllowedBand = IsAroundPlayerDistanceInsideBand(aroundPlayerDistanceToPlayer, activeAroundPlayerRadiusMin, activeAroundPlayerRadiusMax);
        lastAroundPlayerRepathReason = aroundPlayerLiveTrackingActive ? "tracking_initialized" : "tracking_offset_missing";
    }

    private void UpdateAroundPlayerLiveTracking(float deltaTime)
    {
        if (!ShouldRunAroundPlayerLiveTracking())
        {
            return;
        }

        Vector3 currentPlayerPosition = playerRoot.position;
        aroundPlayerDistanceToPlayer = DistanceXZ(companionRoot.position, currentPlayerPosition);
        aroundPlayerInsideAllowedBand = IsAroundPlayerDistanceInsideBand(
            aroundPlayerDistanceToPlayer,
            activeAroundPlayerRadiusMin,
            activeAroundPlayerRadiusMax);

        aroundPlayerRepathTimer += Mathf.Max(0f, deltaTime);
        float safeRepathInterval = Mathf.Max(0.05f, aroundPlayerRepathIntervalSeconds);
        if (aroundPlayerRepathTimer < safeRepathInterval)
        {
            return;
        }

        aroundPlayerRepathTimer = 0f;
        Vector3 translatedSemanticCandidate = currentPlayerPosition + lockedAroundPlayerOffset;
        translatedSemanticCandidate.y = currentPlayerPosition.y;
        float playerMovedDistance = DistanceXZ(currentPlayerPosition, lastTrackedPlayerPosition);
        bool playerMovedEnough = playerMovedDistance >= Mathf.Max(0f, aroundPlayerPlayerMoveThreshold);
        bool currentDestinationOutsideBand = !IsAroundPlayerDestinationInsideBand(
            destinationWorldPosition,
            currentPlayerPosition,
            activeAroundPlayerRadiusMin,
            activeAroundPlayerRadiusMax);
        destinationMovedSinceLastRepath = DistanceXZ(translatedSemanticCandidate, destinationWorldPosition);
        bool destinationMovedEnough = destinationMovedSinceLastRepath >= Mathf.Max(0f, aroundPlayerDestinationChangeThreshold);

        if (!playerMovedEnough && !currentDestinationOutsideBand && !destinationMovedEnough)
        {
            lastAroundPlayerRepathReason = "tracking_no_repath_needed";
            return;
        }

        string repathReason = currentDestinationOutsideBand
            ? "current_destination_outside_radius_band"
            : (playerMovedEnough ? "player_moved" : "locked_offset_destination_changed");

        bool usedFallbackSearch = false;
        if (!TryAcceptAroundPlayerLiveCandidate(translatedSemanticCandidate, out Vector3 nextDestination, out NavMeshPath nextPath))
        {
            if (!TryResolveAroundPlayerDestination(activePrimitive, out nextDestination, out string fallbackFailureReason) ||
                !TryCalculateCompletePath(nextDestination, out nextPath))
            {
                lastAroundPlayerRepathReason = "tracking_sample_unresolved:" + fallbackFailureReason;
                return;
            }

            usedFallbackSearch = true;
            lockedAroundPlayerOffset = nextDestination - currentPlayerPosition;
            lockedAroundPlayerOffset.y = 0f;
            hasLockedAroundPlayerOffset = lockedAroundPlayerOffset.sqrMagnitude > 0.0001f;
            repathReason = "fallback_candidate_refresh:" + repathReason;
        }

        float destinationDelta = DistanceXZ(nextDestination, destinationWorldPosition);
        destinationMovedSinceLastRepath = destinationDelta;
        if (destinationDelta < Mathf.Max(0f, aroundPlayerDestinationChangeThreshold))
        {
            lastTrackedPlayerPosition = currentPlayerPosition;
            lastAroundPlayerRepathReason = usedFallbackSearch ? "fallback_candidate_below_destination_threshold" : "translated_candidate_below_destination_threshold";
            return;
        }

        if (!navMeshAgent.SetDestination(nextDestination))
        {
            SetFailedState("around_player_live_repath_set_destination_failed");
            return;
        }

        destinationWorldPosition = nextDestination;
        hasDestination = true;
        lastTrackedPlayerPosition = currentPlayerPosition;
        lastAroundPlayerDestination = nextDestination;
        aroundPlayerRepathCount++;
        aroundPlayerArrivalDwellTimer = 0f;
        aroundPlayerArrivalDwellProgress = 0f;
        blockedTimer = 0f;
        internalRepathAttempted = false;
        lastProgressPosition = companionRoot.position;
        pathStatus = nextPath.status.ToString();
        UpdateTimeoutBudgetForLiveRepath(nextPath);
        lastAroundPlayerRepathReason = repathReason;
    }

    private bool ShouldRunAroundPlayerLiveTracking()
    {
        return status == CP_PrimitiveExecutionStatus.Running &&
               activePrimitiveType == "move_within_scope" &&
               activeScope == "around_player" &&
               enableAroundPlayerLiveTracking &&
               aroundPlayerLiveTrackingActive &&
               hasLockedAroundPlayerOffset &&
               playerRoot != null &&
               navMeshAgent != null &&
               navMeshAgent.enabled &&
               navMeshAgent.isOnNavMesh;
    }

    private bool TryAcceptAroundPlayerLiveCandidate(Vector3 semanticCandidate, out Vector3 projectedDestination, out NavMeshPath path)
    {
        projectedDestination = companionRoot.position;
        path = null;

        if (!TryProjectOntoNavMesh(semanticCandidate, navMeshSampleRadius, out projectedDestination))
        {
            return false;
        }

        if (!IsCandidateSpatiallySafe(projectedDestination))
        {
            return false;
        }

        if (!IsAroundPlayerDestinationInsideBand(projectedDestination, playerRoot.position, activeAroundPlayerRadiusMin, activeAroundPlayerRadiusMax))
        {
            return false;
        }

        if (DistanceXZ(projectedDestination, companionRoot.position) < minimumDestinationDisplacement)
        {
            return false;
        }

        return TryCalculateCompletePath(projectedDestination, out path);
    }

    private bool TryCalculateCompletePath(Vector3 destination, out NavMeshPath path)
    {
        path = new NavMeshPath();
        NavMeshQueryFilter queryFilter = BuildNavMeshQueryFilter();
        return NavMesh.CalculatePath(companionRoot.position, destination, queryFilter, path) &&
               path.status == NavMeshPathStatus.PathComplete;
    }

    private void UpdateTimeoutBudgetForLiveRepath(NavMeshPath path)
    {
        float safeMultiplier = Mathf.Max(1.0f, movementTimeoutMultiplier);
        float safeMinimumTimeout = Mathf.Max(0.1f, minimumMovementTimeoutSeconds);
        float safeMaximumTimeout = Mathf.Max(safeMinimumTimeout, maximumMovementTimeoutSeconds);
        float safeGrace = Mathf.Max(0.0f, timeoutGraceSeconds);
        float safeAgentSpeed = navMeshAgent != null ? Mathf.Max(0.1f, navMeshAgent.speed) : 0.1f;
        float pathLength = CalculatePathLength(path);
        float estimatedRemainingTravelTime = pathLength / safeAgentSpeed;
        float candidateTotalTimeout = movementElapsedSeconds + estimatedRemainingTravelTime * safeMultiplier + safeGrace;
        float clampedCandidateTimeout = Mathf.Clamp(candidateTotalTimeout, safeMinimumTimeout, safeMaximumTimeout);

        calculatedPathLength = pathLength;
        estimatedTravelTimeSeconds = estimatedRemainingTravelTime;
        movementTimeoutSeconds = Mathf.Max(movementTimeoutSeconds, clampedCandidateTimeout);
    }

    private bool IsAroundPlayerArrivalSatisfied()
    {
        if (playerRoot == null || navMeshAgent == null)
        {
            return false;
        }

        aroundPlayerDistanceToPlayer = DistanceXZ(companionRoot.position, playerRoot.position);
        aroundPlayerInsideAllowedBand = IsAroundPlayerDistanceInsideBand(
            aroundPlayerDistanceToPlayer,
            activeAroundPlayerRadiusMin,
            activeAroundPlayerRadiusMax);
        bool nearDestination = !navMeshAgent.pathPending &&
                               navMeshAgent.remainingDistance <= GetAroundPlayerArrivalAllowance();
        // Runtime completion is based on the accepted destination plus outer safety.
        // The strict radius_min/radius_max band remains owned by destination selection and live repath validation.
        bool arrivalDistanceAcceptable = IsAroundPlayerArrivalDistanceAcceptable(aroundPlayerDistanceToPlayer);

        if (arrivalDistanceAcceptable && nearDestination)
        {
            float safeDwellSeconds = Mathf.Max(0f, aroundPlayerArrivalDwellSeconds);
            if (safeDwellSeconds <= 0f)
            {
                aroundPlayerArrivalDwellProgress = 1f;
                return true;
            }

            aroundPlayerArrivalDwellTimer += Time.deltaTime;
            aroundPlayerArrivalDwellProgress = Mathf.Clamp01(aroundPlayerArrivalDwellTimer / safeDwellSeconds);
            return aroundPlayerArrivalDwellTimer >= safeDwellSeconds;
        }

        aroundPlayerArrivalDwellTimer = 0f;
        aroundPlayerArrivalDwellProgress = 0f;
        return false;
    }

    private bool IsAroundPlayerArrivalDistanceAcceptable(float distanceToPlayer)
    {
        if (float.IsNaN(distanceToPlayer) || float.IsInfinity(distanceToPlayer))
        {
            return false;
        }

        float safeRadiusMax = Mathf.Max(activeAroundPlayerRadiusMin, activeAroundPlayerRadiusMax);
        return distanceToPlayer <= safeRadiusMax + GetAroundPlayerArrivalAllowance();
    }

    private float GetAroundPlayerArrivalAllowance()
    {
        float stoppingAllowance = navMeshAgent != null
            ? Mathf.Max(0f, navMeshAgent.stoppingDistance)
            : 0f;

        return Mathf.Max(Mathf.Max(0f, arrivalTolerance), stoppingAllowance);
    }

    private bool IsAroundPlayerDestinationInsideBand(Vector3 destination, Vector3 playerPosition, float radiusMin, float radiusMax)
    {
        return IsAroundPlayerDistanceInsideBand(DistanceXZ(destination, playerPosition), radiusMin, radiusMax);
    }

    private bool IsAroundPlayerDistanceInsideBand(float distance, float radiusMin, float radiusMax)
    {
        float safeRadiusMin = Mathf.Max(0f, radiusMin);
        float safeRadiusMax = Mathf.Max(safeRadiusMin, radiusMax);
        return distance >= safeRadiusMin && distance <= safeRadiusMax;
    }

    private void ResetAroundPlayerTrackingState()
    {
        aroundPlayerLiveTrackingActive = false;
        aroundPlayerRepathCount = 0;
        lastAroundPlayerRepathReason = string.Empty;
        lastTrackedPlayerPosition = Vector3.zero;
        lockedAroundPlayerOffset = Vector3.zero;
        aroundPlayerDistanceToPlayer = 0f;
        destinationMovedSinceLastRepath = 0f;
        aroundPlayerInsideAllowedBand = false;
        aroundPlayerArrivalDwellProgress = 0f;
        activeAroundPlayerRadiusMin = 0f;
        activeAroundPlayerRadiusMax = 0f;
        aroundPlayerRepathTimer = 0f;
        aroundPlayerArrivalDwellTimer = 0f;
        hasLockedAroundPlayerOffset = false;
        lastAroundPlayerDestination = Vector3.zero;
    }

    private bool TryAcceptInteractionAreaCandidate(
        Vector3 semanticCandidate,
        float effectiveInteriorMargin,
        bool requireMeaningfulDisplacement,
        out Vector3 projectedDestination,
        out string failureReason)
    {
        projectedDestination = companionRoot.position;
        failureReason = "interaction_area_navmesh_projection_failed";

        if (!TryProjectOntoNavMesh(semanticCandidate, navMeshSampleRadius, out projectedDestination))
        {
            return false;
        }

        if (interactionArea == null)
        {
            failureReason = "interaction_area_missing";
            return false;
        }

        if (!IsInsideInteractionAreaSafeInterior(projectedDestination, effectiveInteriorMargin))
        {
            failureReason = "interaction_area_projection_outside_safe_interior";
            return false;
        }

        if (requireMeaningfulDisplacement && DistanceXZ(projectedDestination, companionRoot.position) < minimumDestinationDisplacement)
        {
            failureReason = "interaction_area_destination_too_close";
            return false;
        }

        if (!IsPathComplete(projectedDestination))
        {
            failureReason = "interaction_area_path_invalid";
            return false;
        }

        return true;
    }

    private bool TryAcceptFishingInteractionAreaCandidate(
        Vector3 semanticCandidate,
        float effectiveInteriorMargin,
        bool requireMeaningfulDisplacement,
        out Vector3 projectedDestination,
        out string failureReason)
    {
        projectedDestination = companionRoot.position;
        failureReason = string.Empty;

        if (fishingInteractionArea == null)
        {
            failureReason = "fishing_interaction_area_missing";
            return false;
        }

        if (!TryProjectOntoNavMesh(semanticCandidate, navMeshSampleRadius, out projectedDestination))
        {
            failureReason = "fishing_interaction_area_navmesh_projection_failed";
            return false;
        }

        float safeRadius = Mathf.Max(0f, fishingInteractionArea.Radius - Mathf.Max(0f, effectiveInteriorMargin));
        float safeDistance = Mathf.Max(0f, safeRadius - interactionAreaArrivalSafetyBuffer);
        float projectedDistanceXZ = DistanceXZ(projectedDestination, fishingInteractionArea.GetAreaCenterWorldPosition());
        if (projectedDistanceXZ > safeDistance)
        {
            failureReason = "fishing_interaction_area_projection_outside_safe_interior";
            return false;
        }

        if (requireMeaningfulDisplacement && DistanceXZ(projectedDestination, companionRoot.position) < minimumDestinationDisplacement)
        {
            failureReason = "fishing_interaction_area_destination_too_close";
            return false;
        }

        if (!IsPathComplete(projectedDestination))
        {
            failureReason = "fishing_interaction_area_path_invalid";
            return false;
        }

        return true;
    }

    private bool IsFishingInteractionAreaContext(CP_PrimitiveExecutionContext context)
    {
        string state = Normalize(context != null ? context.currentState : string.Empty);

        return state == Normalize(CP_StateNames.F2) ||
            state == Normalize(CP_StateNames.F3);
    }

    private float GetEffectiveInteractionAreaInteriorMargin()
    {
        float configuredMargin = Mathf.Max(0f, interactionAreaInteriorMargin);
        float stoppingAllowance = navMeshAgent != null
            ? Mathf.Max(0f, navMeshAgent.stoppingDistance)
            : 0f;
        float arrivalAllowance = Mathf.Max(Mathf.Max(0f, arrivalTolerance), stoppingAllowance);
        float safetyBuffer = Mathf.Max(0f, interactionAreaArrivalSafetyBuffer);

        return Mathf.Max(configuredMargin, arrivalAllowance + safetyBuffer);
    }

    private float GetInteractionAreaSafeRadius(float effectiveInteriorMargin)
    {
        if (interactionArea == null)
        {
            return 0f;
        }

        return Mathf.Max(0f, interactionArea.Radius - Mathf.Max(0f, effectiveInteriorMargin));
    }

    private bool IsInsideInteractionAreaSafeInterior(Vector3 worldPosition, float effectiveInteriorMargin)
    {
        if (interactionArea == null)
        {
            return false;
        }

        float safeRadius = GetInteractionAreaSafeRadius(effectiveInteriorMargin);
        float distance = interactionArea.useXZDistanceOnly
            ? interactionArea.DistanceXZ(worldPosition, interactionArea.CenterWorldPosition)
            : Vector3.Distance(worldPosition, interactionArea.CenterWorldPosition);

        return distance <= safeRadius;
    }

    private float GetClampedAroundPlayerRadiusMin(CP_MotionPrimitive primitive)
    {
        float safeMinimum = Mathf.Max(0.1f, minimumAroundPlayerRadius);
        float safeMaximum = Mathf.Max(safeMinimum, maximumAroundPlayerRadius);
        float requestedRadiusMin = primitive != null && primitive.radius_min > 0f ? primitive.radius_min : 1.2f;
        return Mathf.Clamp(requestedRadiusMin, safeMinimum, safeMaximum);
    }

    private float GetClampedAroundPlayerRadiusMax(CP_MotionPrimitive primitive, float clampedRadiusMin)
    {
        float safeMinimum = Mathf.Max(0.1f, minimumAroundPlayerRadius);
        float safeMaximum = Mathf.Max(safeMinimum, maximumAroundPlayerRadius);
        float requestedRadiusMax = primitive != null && primitive.radius_max > 0f
            ? primitive.radius_max
            : Mathf.Max(clampedRadiusMin + 0.3f, 1.8f);

        return Mathf.Clamp(requestedRadiusMax, clampedRadiusMin, safeMaximum);
    }

    private bool IsPathComplete(Vector3 destination)
    {
        NavMeshPath path = new NavMeshPath();
        NavMeshQueryFilter queryFilter = BuildNavMeshQueryFilter();
        return NavMesh.CalculatePath(companionRoot.position, destination, queryFilter, path) &&
               path.status == NavMeshPathStatus.PathComplete;
    }

    private NavMeshQueryFilter BuildNavMeshQueryFilter()
    {
        return new NavMeshQueryFilter
        {
            agentTypeID = navMeshAgent != null ? navMeshAgent.agentTypeID : 0,
            areaMask = navMeshAgent != null ? navMeshAgent.areaMask : NavMesh.AllAreas
        };
    }

    private float CalculatePathLength(NavMeshPath path)
    {
        if (path == null || path.corners == null || path.corners.Length < 2)
        {
            return 0f;
        }

        float totalLength = 0f;
        for (int index = 1; index < path.corners.Length; index++)
        {
            totalLength += Vector3.Distance(path.corners[index - 1], path.corners[index]);
        }

        return totalLength;
    }

    private void SafeResetPath()
    {
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.ResetPath();
        }
    }

    private void SetFailedState(string failureReason)
    {
        SafeResetPath();
        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            navMeshAgent.isStopped = true;
        }

        usesAgentPath = false;
        calculatedPathLength = 0f;
        estimatedTravelTimeSeconds = 0f;
        ResetAroundPlayerTrackingState();
        hasDestination = false;
        status = CP_PrimitiveExecutionStatus.Failed;
        lastFailureReason = string.IsNullOrWhiteSpace(failureReason) ? "movement_failed" : failureReason;
    }

    private void RefreshAgentDebug()
    {
        agentEnabled = navMeshAgent != null && navMeshAgent.enabled;
        agentOnNavMesh = navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh;
    }

    private void OnDrawGizmos()
    {
        if (!showMovementGizmos || !hasDestination)
        {
            return;
        }

        float safeDestinationRadius = Mathf.Max(0.02f, destinationGizmoRadius);
        Gizmos.color = new Color(0.1f, 0.85f, 1.0f, 0.95f);
        Gizmos.DrawWireSphere(destinationWorldPosition, safeDestinationRadius);
        Gizmos.DrawLine(destinationWorldPosition + Vector3.left * safeDestinationRadius, destinationWorldPosition + Vector3.right * safeDestinationRadius);
        Gizmos.DrawLine(destinationWorldPosition + Vector3.forward * safeDestinationRadius, destinationWorldPosition + Vector3.back * safeDestinationRadius);
        Gizmos.DrawLine(destinationWorldPosition + Vector3.down * safeDestinationRadius, destinationWorldPosition + Vector3.up * safeDestinationRadius);

        if (companionRoot != null)
        {
            Gizmos.color = new Color(0.1f, 0.65f, 1.0f, 0.75f);
            Gizmos.DrawLine(companionRoot.position, destinationWorldPosition);
        }

        if (showMovementPathGizmo &&
            navMeshAgent != null &&
            navMeshAgent.enabled &&
            navMeshAgent.hasPath &&
            navMeshAgent.path != null &&
            navMeshAgent.path.corners != null)
        {
            Vector3[] corners = navMeshAgent.path.corners;
            Gizmos.color = new Color(0.0f, 1.0f, 0.45f, 0.9f);
            for (int index = 1; index < corners.Length; index++)
            {
                Gizmos.DrawLine(corners[index - 1], corners[index]);
            }
        }

        if (showAroundPlayerRadiusGizmos &&
            activeScope == "around_player" &&
            aroundPlayerLiveTrackingActive &&
            playerRoot != null)
        {
            Gizmos.color = new Color(1.0f, 0.72f, 0.1f, 0.75f);
            DrawXZCircleGizmo(playerRoot.position, activeAroundPlayerRadiusMin);
            Gizmos.color = new Color(1.0f, 0.35f, 0.1f, 0.75f);
            DrawXZCircleGizmo(playerRoot.position, activeAroundPlayerRadiusMax);
        }
    }

    private void DrawXZCircleGizmo(Vector3 center, float radius)
    {
        float safeRadius = Mathf.Max(0f, radius);
        if (safeRadius <= 0f)
        {
            return;
        }

        int safeSegments = Mathf.Max(8, radiusGizmoSegments);
        Vector3 previousPoint = center + Vector3.forward * safeRadius;
        for (int segment = 1; segment <= safeSegments; segment++)
        {
            float angle = (segment / (float)safeSegments) * Mathf.PI * 2f;
            Vector3 nextPoint = center + new Vector3(Mathf.Sin(angle) * safeRadius, 0f, Mathf.Cos(angle) * safeRadius);
            Gizmos.DrawLine(previousPoint, nextPoint);
            previousPoint = nextPoint;
        }
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

    private Transform FindChildByName(Transform root, string targetName)
    {
        if (root == null)
        {
            return null;
        }

        if (root.name == targetName)
        {
            return root;
        }

        foreach (Transform child in root)
        {
            Transform result = FindChildByName(child, targetName);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
