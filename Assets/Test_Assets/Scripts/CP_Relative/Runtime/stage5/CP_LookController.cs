using UnityEngine;

// Stage 5B-1 look controller for explicit look primitives and low-priority default attention.
// This component owns only procedural head rotation plus limited root-yaw assistance when movement is idle.
public class CP_LookController : MonoBehaviour
{
    private enum CP_LookControlMode
    {
        None,
        ExplicitLookAt,
        ExplicitFaceToward,
        DefaultAttention
    }

    [Header("References")]
    public Transform companionRoot;
    public Transform headTransform;
    public Transform playerHead;
    public RoastingStateBlackboard blackboard;
    public FishingStateBlackboard fishingBlackboard;
    public CP_FishingAnchorManager fishingAnchorManager;
    public CP_MovementController movementController;
    public CP_PropController propController;

    [Header("Optional References")]
    public Transform fireCenterFallback;
    public Transform headForwardMarker;

    [Header("Head Settings")]
    public float headYawLimitDegrees = 90f;
    public float headPitchDownLimitDegrees = 30f;
    public float headPitchUpLimitDegrees = 25f;
    public float headTurnSpeedDegrees = 200f;
    public float headReturnSpeedDegrees = 150f;
    public float headAngularToleranceDegrees = 8f;
    public bool invertYaw;
    public bool invertPitch;
    public float neutralForwardDistance = 1.0f;

    [Header("Body Follow")]
    public float comfortableHeadYawDegrees = 30f;
    public float bodyFollowStartYawDegrees = 55f;
    public float bodyFollowDelaySeconds = 0.75f;
    public float bodyFollowTurnSpeedDegrees = 100f;
    public float rootYawToleranceDegrees = 8f;
    public float bodyFollowHysteresisDegrees = 5f;

    [Header("Timing")]
    public float minimumLookHardTimeout = 1.25f;
    public float lookHardTimeoutGrace = 1.0f;
    public float minimumFaceHardTimeout = 1.25f;
    public float faceHardTimeoutGrace = 1.0f;

    [Header("Runtime Debug")]
    public CP_PrimitiveExecutionStatus status = CP_PrimitiveExecutionStatus.Idle;
    public string lastFailureReason = string.Empty;
    public bool explicitLookActive;
    public bool defaultLookRequested;
    public bool defaultLookControlling;
    public string currentLookMode = "None";
    public string currentTargetToken = string.Empty;
    public bool targetResolved;
    public Vector3 resolvedTargetWorldPosition;
    public float currentHeadYawDegrees;
    public float currentHeadPitchDegrees;
    public bool bodyAssistActive;
    public string currentRootYawOwner = "none";

    private Quaternion headBaseLocalRotation = Quaternion.identity;
    private Quaternion currentProceduralLookOffset = Quaternion.identity;
    private Quaternion desiredProceduralLookOffset = Quaternion.identity;
    private Quaternion pendingRootRotation = Quaternion.identity;
    private string explicitTargetToken = string.Empty;
    private string defaultAttentionTargetToken = "player_head";
    private float explicitRequestedDuration;
    private float explicitElapsedSeconds;
    private float explicitHardTimeoutSeconds;
    private float bodyFollowTimer;
    private bool bodyFollowLatched;
    private bool rootRotationPending;
    private bool bindingsCached;
    private CP_LookControlMode explicitMode = CP_LookControlMode.None;

    public CP_PrimitiveExecutionStatus Status => status;
    public string LastFailureReason => lastFailureReason;
    public bool IsExplicitLookActive => explicitLookActive;
    public bool IsDefaultLookControlling => defaultLookControlling;

    private void Awake()
    {
        CacheBindingsIfNeeded();
    }

    private void Update()
    {
        CacheBindingsIfNeeded();
        UpdateRuntimeState(Time.deltaTime);
    }

    private void LateUpdate()
    {
        if (headTransform == null)
        {
            return;
        }

        ApplyHeadRotation(Time.deltaTime);
    }

    private void FixedUpdate()
    {
        if (!rootRotationPending || companionRoot == null)
        {
            return;
        }

        if (IsMovementYawOwned())
        {
            ClearPendingRootRotation();
            currentRootYawOwner = "movement_controller";
            return;
        }

        Rigidbody body = companionRoot.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.MoveRotation(pendingRootRotation);
            if (!body.isKinematic)
            {
                body.angularVelocity = Vector3.zero;
            }
        }
        else
        {
            companionRoot.rotation = pendingRootRotation;
        }

        rootRotationPending = false;
    }

    private void OnDisable()
    {
        CancelExplicitLook("look_controller_disabled");
        defaultLookRequested = false;
        defaultLookControlling = false;
        currentLookMode = "None";
        currentTargetToken = string.Empty;
        targetResolved = false;
        bodyAssistActive = false;
        currentRootYawOwner = "none";
        currentHeadYawDegrees = 0f;
        currentHeadPitchDegrees = 0f;
        desiredProceduralLookOffset = Quaternion.identity;
        currentProceduralLookOffset = Quaternion.identity;
        ClearPendingRootRotation();

        if (headTransform != null)
        {
            headTransform.localRotation = headBaseLocalRotation;
        }
    }

    public bool TryBeginLookAt(CP_MotionPrimitive primitive, out string failureReason)
    {
        failureReason = string.Empty;
        if (!ValidateExplicitExecutionBindings(out failureReason))
        {
            SetFailureState(failureReason);
            return false;
        }

        string targetToken = NormalizeTargetToken(primitive != null ? primitive.target : string.Empty);
        if (!IsSupportedLookTarget(targetToken))
        {
            failureReason = "unsupported_look_target";
            SetFailureState(failureReason);
            return false;
        }

        if (!TryResolveTarget(targetToken, out Vector3 targetPosition, out failureReason))
        {
            SetFailureState(failureReason);
            return false;
        }

        BeginExplicitLook(CP_LookControlMode.ExplicitLookAt, targetToken, primitive != null ? primitive.duration : 0f, targetPosition);
        return true;
    }

    public bool TryBeginFaceToward(CP_MotionPrimitive primitive, out string failureReason)
    {
        failureReason = string.Empty;
        if (!ValidateExplicitExecutionBindings(out failureReason))
        {
            SetFailureState(failureReason);
            return false;
        }

        if (IsMovementYawOwned())
        {
            failureReason = "body_rotation_owned_by_movement";
            SetFailureState(failureReason);
            return false;
        }

        string targetToken = NormalizeTargetToken(primitive != null ? primitive.target : string.Empty);
        if (!IsSupportedLookTarget(targetToken))
        {
            failureReason = "unsupported_look_target";
            SetFailureState(failureReason);
            return false;
        }

        if (!TryResolveTarget(targetToken, out Vector3 targetPosition, out failureReason))
        {
            failureReason = failureReason == "campfire_center_unresolved" || failureReason == "player_head_unresolved"
                ? "face_target_unresolved"
                : failureReason;
            SetFailureState(failureReason);
            return false;
        }

        BeginExplicitLook(CP_LookControlMode.ExplicitFaceToward, targetToken, primitive != null ? primitive.duration : 0f, targetPosition);
        return true;
    }

    public void CancelExplicitLook(string reason)
    {
        if (!explicitLookActive && explicitMode == CP_LookControlMode.None)
        {
            return;
        }

        explicitLookActive = false;
        explicitMode = CP_LookControlMode.None;
        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastFailureReason = string.IsNullOrWhiteSpace(reason) ? "look_cancelled" : reason;
        explicitTargetToken = string.Empty;
        explicitElapsedSeconds = 0f;
        explicitRequestedDuration = 0f;
        explicitHardTimeoutSeconds = 0f;
        bodyFollowTimer = 0f;
        bodyFollowLatched = false;
        bodyAssistActive = false;
        ClearPendingRootRotation();
        currentTargetToken = string.Empty;
        targetResolved = false;
        currentLookMode = defaultLookRequested ? CP_LookControlMode.DefaultAttention.ToString() : "None";
        currentRootYawOwner = IsMovementYawOwned() ? "movement_controller" : "none";
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

    public void SetDefaultAttentionRequested(bool active)
    {
        defaultLookRequested = active;
    }

    public void SetDefaultAttentionTarget(string targetToken)
    {
        string normalized = NormalizeTargetToken(targetToken);
        defaultAttentionTargetToken = string.IsNullOrWhiteSpace(normalized) ? "player_head" : normalized;
    }

    private void CacheBindingsIfNeeded()
    {
        if (bindingsCached)
        {
            return;
        }

        if (companionRoot == null)
        {
            companionRoot = transform;
        }

        if (headTransform != null)
        {
            headBaseLocalRotation = headTransform.localRotation;
        }

        pendingRootRotation = companionRoot != null ? companionRoot.rotation : Quaternion.identity;
        bindingsCached = true;
    }

    private void BeginExplicitLook(CP_LookControlMode mode, string targetToken, float requestedDuration, Vector3 initialTargetPosition)
    {
        ClearPendingRootRotation();
        explicitMode = mode;
        explicitLookActive = true;
        status = CP_PrimitiveExecutionStatus.Running;
        lastFailureReason = string.Empty;
        explicitTargetToken = targetToken;
        explicitRequestedDuration = Mathf.Max(0.1f, requestedDuration);
        explicitElapsedSeconds = 0f;
        explicitHardTimeoutSeconds = mode == CP_LookControlMode.ExplicitFaceToward
            ? Mathf.Max(minimumFaceHardTimeout, explicitRequestedDuration + faceHardTimeoutGrace)
            : Mathf.Max(minimumLookHardTimeout, explicitRequestedDuration + lookHardTimeoutGrace);
        bodyFollowTimer = 0f;
        bodyFollowLatched = false;
        bodyAssistActive = false;
        targetResolved = true;
        resolvedTargetWorldPosition = initialTargetPosition;
        currentTargetToken = targetToken;
        currentLookMode = mode.ToString();
    }

    private void UpdateRuntimeState(float deltaTime)
    {
        currentRootYawOwner = IsMovementYawOwned() ? "movement_controller" : "none";

        if (explicitLookActive)
        {
            UpdateExplicitLook(deltaTime);
            defaultLookControlling = false;
            return;
        }

        if (defaultLookRequested && ValidateVisualBindings(out _))
        {
            UpdateDefaultAttention(deltaTime);
            return;
        }

        defaultLookControlling = false;
        currentLookMode = "None";
        currentTargetToken = string.Empty;
        targetResolved = false;
        bodyAssistActive = false;
        currentRootYawOwner = IsMovementYawOwned() ? "movement_controller" : "none";
        desiredProceduralLookOffset = Quaternion.identity;
        currentHeadYawDegrees = MoveTowardZero(currentHeadYawDegrees, headReturnSpeedDegrees * deltaTime);
        currentHeadPitchDegrees = MoveTowardZero(currentHeadPitchDegrees, headReturnSpeedDegrees * deltaTime);

        if (status == CP_PrimitiveExecutionStatus.Idle || status == CP_PrimitiveExecutionStatus.Running)
        {
            status = CP_PrimitiveExecutionStatus.Idle;
        }
    }

    private void UpdateExplicitLook(float deltaTime)
    {
        explicitElapsedSeconds += Mathf.Max(0f, deltaTime);
        currentLookMode = explicitMode.ToString();
        currentTargetToken = explicitTargetToken;

        string failureReason;
        if (!TryResolveTarget(explicitTargetToken, out Vector3 targetPosition, out failureReason))
        {
            FailExplicitLook(failureReason);
            return;
        }

        resolvedTargetWorldPosition = targetPosition;
        targetResolved = true;

        LookAngles angles = CalculateClampedAngles(targetPosition);
        float angularError = CalculateAngularError(angles);
        currentHeadYawDegrees = Mathf.MoveTowards(currentHeadYawDegrees, angles.clampedYawDegrees, headTurnSpeedDegrees * deltaTime);
        currentHeadPitchDegrees = Mathf.MoveTowards(currentHeadPitchDegrees, angles.clampedPitchDegrees, headTurnSpeedDegrees * deltaTime);

        if (explicitMode == CP_LookControlMode.ExplicitLookAt)
        {
            UpdateLookAtBodyAssist(deltaTime, angles);

            bool withinTolerance = angularError <= Mathf.Max(0.5f, headAngularToleranceDegrees);
            if (withinTolerance && explicitElapsedSeconds >= explicitRequestedDuration)
            {
                CompleteExplicitLook();
                return;
            }

            if (explicitElapsedSeconds >= explicitHardTimeoutSeconds)
            {
                string timeoutReason = (IsMovementYawOwned() || angles.requiredBodyAssistToReachComfortableRange)
                    ? "look_target_outside_head_range"
                    : "look_hard_timeout";
                FailExplicitLook(timeoutReason);
            }
            return;
        }

        if (IsMovementYawOwned())
        {
            ClearPendingRootRotation();
            FailExplicitLook("body_rotation_owned_by_movement");
            return;
        }

        bodyAssistActive = false;
        bodyFollowTimer = 0f;
        bodyFollowLatched = false;

        bool faceAligned = Mathf.Abs(angles.unclampedYawDegrees) <= Mathf.Max(0.5f, rootYawToleranceDegrees);
        RotateRootYawToward(targetPosition, bodyFollowTurnSpeedDegrees, Time.deltaTime, Time.fixedDeltaTime);
        currentRootYawOwner = "explicit_face_toward";

        if (faceAligned && explicitElapsedSeconds >= explicitRequestedDuration)
        {
            CompleteExplicitLook();
            return;
        }

        if (explicitElapsedSeconds >= explicitHardTimeoutSeconds)
        {
            FailExplicitLook("face_toward_hard_timeout");
        }
    }

    private void UpdateDefaultAttention(float deltaTime)
    {
        currentLookMode = CP_LookControlMode.DefaultAttention.ToString();
        currentTargetToken = defaultAttentionTargetToken;

        if (!TryResolveTarget(defaultAttentionTargetToken, out Vector3 targetPosition, out _))
        {
            defaultLookControlling = false;
            targetResolved = false;
            bodyAssistActive = false;
            desiredProceduralLookOffset = Quaternion.identity;
            currentHeadYawDegrees = MoveTowardZero(currentHeadYawDegrees, headReturnSpeedDegrees * deltaTime);
            currentHeadPitchDegrees = MoveTowardZero(currentHeadPitchDegrees, headReturnSpeedDegrees * deltaTime);
            return;
        }

        targetResolved = true;
        resolvedTargetWorldPosition = targetPosition;
        defaultLookControlling = true;
        bodyAssistActive = false;

        LookAngles angles = CalculateClampedAngles(targetPosition);
        currentHeadYawDegrees = Mathf.MoveTowards(currentHeadYawDegrees, angles.clampedYawDegrees, headTurnSpeedDegrees * deltaTime);
        currentHeadPitchDegrees = Mathf.MoveTowards(currentHeadPitchDegrees, angles.clampedPitchDegrees, headTurnSpeedDegrees * deltaTime);
        currentRootYawOwner = IsMovementYawOwned() ? "movement_controller" : "none";
    }

    private void UpdateLookAtBodyAssist(float deltaTime, LookAngles angles)
    {
        if (IsMovementYawOwned())
        {
            bodyAssistActive = false;
            bodyFollowTimer = 0f;
            bodyFollowLatched = false;
            ClearPendingRootRotation();
            currentRootYawOwner = "movement_controller";
            return;
        }

        float absYaw = Mathf.Abs(angles.unclampedYawDegrees);
        float startThreshold = Mathf.Max(comfortableHeadYawDegrees, bodyFollowStartYawDegrees);
        float stopThreshold = Mathf.Max(comfortableHeadYawDegrees, bodyFollowStartYawDegrees - Mathf.Max(0f, bodyFollowHysteresisDegrees));

        if (!bodyFollowLatched)
        {
            if (absYaw >= startThreshold)
            {
                bodyFollowTimer += deltaTime;
                if (bodyFollowTimer >= Mathf.Max(0f, bodyFollowDelaySeconds))
                {
                    bodyFollowLatched = true;
                }
            }
            else
            {
                bodyFollowTimer = 0f;
            }
        }
        else if (absYaw <= stopThreshold)
        {
            bodyFollowLatched = false;
            bodyFollowTimer = 0f;
        }

        bodyAssistActive = bodyFollowLatched;
        if (!bodyAssistActive)
        {
            ClearPendingRootRotation();
            return;
        }

        RotateRootYawToward(resolvedTargetWorldPosition, bodyFollowTurnSpeedDegrees, Time.deltaTime, Time.fixedDeltaTime);
        currentRootYawOwner = "explicit_look_body_assist";
    }

    private void RotateRootYawToward(Vector3 targetPosition, float turnSpeedDegrees, float deltaTime, float fixedDeltaTime)
    {
        if (companionRoot == null)
        {
            return;
        }

        Vector3 flatDirection = targetPosition - companionRoot.position;
        flatDirection.y = 0f;
        if (flatDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion currentRotation = companionRoot.rotation;
        Quaternion desiredRotation = Quaternion.LookRotation(flatDirection.normalized, Vector3.up);
        float step = Mathf.Max(0f, turnSpeedDegrees) * (GetCompanionRigidbody() != null ? fixedDeltaTime : deltaTime);
        Quaternion nextRotation = Quaternion.RotateTowards(currentRotation, desiredRotation, step);
        pendingRootRotation = nextRotation;
        rootRotationPending = true;

        if (GetCompanionRigidbody() == null)
        {
            companionRoot.rotation = nextRotation;
            rootRotationPending = false;
        }
    }

    private LookAngles CalculateClampedAngles(Vector3 targetPosition)
    {
        Vector3 referencePosition = headTransform != null ? headTransform.position : companionRoot.position;
        Vector3 worldDirection = targetPosition - referencePosition;
        if (worldDirection.sqrMagnitude < 0.0001f)
        {
            return default;
        }

        Vector3 localDirection = companionRoot.InverseTransformDirection(worldDirection.normalized);
        float yawDegrees = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
        float pitchDegrees = -Mathf.Atan2(localDirection.y, new Vector2(localDirection.x, localDirection.z).magnitude) * Mathf.Rad2Deg;

        if (invertYaw)
        {
            yawDegrees = -yawDegrees;
        }

        if (invertPitch)
        {
            pitchDegrees = -pitchDegrees;
        }

        float clampedYaw = Mathf.Clamp(yawDegrees, -Mathf.Abs(headYawLimitDegrees), Mathf.Abs(headYawLimitDegrees));
        float clampedPitch = Mathf.Clamp(pitchDegrees, -Mathf.Abs(headPitchUpLimitDegrees), Mathf.Abs(headPitchDownLimitDegrees));

        return new LookAngles
        {
            unclampedYawDegrees = yawDegrees,
            unclampedPitchDegrees = pitchDegrees,
            clampedYawDegrees = clampedYaw,
            clampedPitchDegrees = clampedPitch,
            requiredBodyAssistToReachComfortableRange = Mathf.Abs(yawDegrees) > Mathf.Abs(headYawLimitDegrees) + 0.5f
        };
    }

    private float CalculateAngularError(LookAngles angles)
    {
        float yawError = Mathf.Abs(angles.unclampedYawDegrees - currentHeadYawDegrees);
        float pitchError = Mathf.Abs(angles.unclampedPitchDegrees - currentHeadPitchDegrees);

        float unreachableYaw = Mathf.Max(0f, Mathf.Abs(angles.unclampedYawDegrees) - Mathf.Abs(headYawLimitDegrees));
        float unreachablePitchUp = Mathf.Max(0f, -angles.unclampedPitchDegrees - Mathf.Abs(headPitchUpLimitDegrees));
        float unreachablePitchDown = Mathf.Max(0f, angles.unclampedPitchDegrees - Mathf.Abs(headPitchDownLimitDegrees));
        float unreachablePitch = Mathf.Max(unreachablePitchUp, unreachablePitchDown);

        return Mathf.Max(yawError, pitchError, unreachableYaw, unreachablePitch);
    }

    private void ApplyHeadRotation(float deltaTime)
    {
        if (headTransform == null)
        {
            return;
        }

        desiredProceduralLookOffset = Quaternion.Euler(currentHeadPitchDegrees, currentHeadYawDegrees, 0f);
        currentProceduralLookOffset = Quaternion.RotateTowards(
            currentProceduralLookOffset,
            desiredProceduralLookOffset,
            Mathf.Max(1f, headTurnSpeedDegrees) * Mathf.Max(0f, deltaTime)
        );

        headTransform.localRotation = headBaseLocalRotation * currentProceduralLookOffset;
    }

    private void CompleteExplicitLook()
    {
        explicitLookActive = false;
        status = CP_PrimitiveExecutionStatus.Succeeded;
        explicitMode = CP_LookControlMode.None;
        bodyAssistActive = false;
        bodyFollowTimer = 0f;
        bodyFollowLatched = false;
        ClearPendingRootRotation();
        explicitTargetToken = string.Empty;
        currentTargetToken = string.Empty;
        currentLookMode = defaultLookRequested ? CP_LookControlMode.DefaultAttention.ToString() : "None";
        targetResolved = false;
    }

    private void FailExplicitLook(string failureReason)
    {
        explicitLookActive = false;
        explicitMode = CP_LookControlMode.None;
        status = CP_PrimitiveExecutionStatus.Failed;
        lastFailureReason = string.IsNullOrWhiteSpace(failureReason) ? "look_failed" : failureReason;
        bodyAssistActive = false;
        bodyFollowTimer = 0f;
        bodyFollowLatched = false;
        ClearPendingRootRotation();
        explicitTargetToken = string.Empty;
        currentTargetToken = string.Empty;
        currentLookMode = defaultLookRequested ? CP_LookControlMode.DefaultAttention.ToString() : "None";
        currentRootYawOwner = IsMovementYawOwned() ? "movement_controller" : "none";
        targetResolved = false;
    }

    private void SetFailureState(string failureReason)
    {
        status = CP_PrimitiveExecutionStatus.Failed;
        lastFailureReason = string.IsNullOrWhiteSpace(failureReason) ? "look_failed" : failureReason;
        explicitLookActive = false;
        explicitMode = CP_LookControlMode.None;
        ClearPendingRootRotation();
        currentLookMode = "None";
        currentTargetToken = string.Empty;
        targetResolved = false;
    }

    private bool ValidateVisualBindings(out string failureReason)
    {
        failureReason = string.Empty;
        if (companionRoot == null || headTransform == null)
        {
            failureReason = "look_controller_not_bound";
            return false;
        }

        return true;
    }

    private bool ValidateExplicitExecutionBindings(out string failureReason)
    {
        if (!ValidateVisualBindings(out failureReason))
        {
            return false;
        }

        if (movementController == null)
        {
            failureReason = "movement_controller_not_bound_for_look";
            return false;
        }

        return true;
    }

    private bool TryResolveTarget(string targetToken, out Vector3 targetPosition, out string failureReason)
    {
        targetPosition = Vector3.zero;
        failureReason = string.Empty;

        string normalizedTarget = NormalizeTargetToken(targetToken);
        switch (normalizedTarget)
        {
            case "player_head":
                if (playerHead == null)
                {
                    failureReason = "player_head_unresolved";
                    return false;
                }

                targetPosition = playerHead.position;
                return true;

            case "campfire_center":
                return TryResolveCampfireCenter(out targetPosition, out failureReason);

            case "neutral_forward":
                if (companionRoot == null)
                {
                    failureReason = "look_controller_not_bound";
                    return false;
                }

                targetPosition = companionRoot.position + companionRoot.forward * Mathf.Max(0.25f, neutralForwardDistance);
                return true;

            case "companion_marshmallow":
                if (propController == null || !propController.IsRuntimeReady)
                {
                    failureReason = "companion_marshmallow_unavailable";
                    return false;
                }

                if (!propController.TryGetCompanionMarshmallowLookTarget(out targetPosition))
                {
                    failureReason = "companion_marshmallow_target_unresolved";
                    return false;
                }

                return true;

            case "assigned_fishing_cast_target":
            {
                if (fishingAnchorManager == null)
                {
                    failureReason = "assigned_fishing_cast_target_unresolved";
                    return false;
                }
                Transform assignedCastTarget;
                if (!fishingAnchorManager.TryGetAssignedCastTargetTransform(out assignedCastTarget) || assignedCastTarget == null)
                {
                    failureReason = "assigned_fishing_cast_target_unresolved";
                    return false;
                }
                targetPosition = assignedCastTarget.position;
                return true;
            }

            case "player_fishing_rod":
                if (fishingBlackboard == null || fishingBlackboard.fishingRodObject == null)
                {
                    failureReason = "player_fishing_rod_unresolved";
                    return false;
                }
                targetPosition = fishingBlackboard.fishingRodObject.transform.position;
                return true;

            case "player_bobber":
                if (fishingBlackboard == null || fishingBlackboard.currentBobberInstance == null)
                {
                    failureReason = "player_bobber_unresolved";
                    return false;
                }
                targetPosition = fishingBlackboard.currentBobberInstance.transform.position;
                return true;

            case "water_focus":
                return TryResolveWaterFocus(out targetPosition, out failureReason);

            default:
                failureReason = "unsupported_look_target";
                return false;
        }
    }

    private bool TryResolveCampfireCenter(out Vector3 targetPosition, out string failureReason)
    {
        targetPosition = Vector3.zero;
        failureReason = string.Empty;

        if (blackboard != null && blackboard.currentCampfireInstance != null)
        {
            Transform fireCenterPoint = FindNestedChildByName(blackboard.currentCampfireInstance.transform, "FireCenterPoint");
            if (fireCenterPoint != null)
            {
                targetPosition = fireCenterPoint.position;
                return true;
            }
        }

        if (blackboard != null && blackboard.campfireSpawnPoint != null)
        {
            targetPosition = blackboard.campfireSpawnPoint.position;
            return true;
        }

        if (fireCenterFallback != null)
        {
            targetPosition = fireCenterFallback.position;
            return true;
        }

        failureReason = "campfire_center_unresolved";
        return false;
    }

    private bool IsSupportedLookTarget(string targetToken)
    {
        switch (NormalizeTargetToken(targetToken))
        {
            case "player_head":
            case "campfire_center":
            case "neutral_forward":
            case "companion_marshmallow":
            case "water_focus":
            case "assigned_fishing_cast_target":
            case "player_fishing_rod":
            case "player_bobber":
                return true;
            default:
                return false;
        }
    }

    private bool TryResolveWaterFocus(out Vector3 targetPosition, out string failureReason)
    {
        targetPosition = Vector3.zero;
        failureReason = string.Empty;

        Transform assignedCastTarget;
        if (fishingAnchorManager != null && fishingAnchorManager.TryGetAssignedCastTargetTransform(out assignedCastTarget) && assignedCastTarget != null)
        {
            targetPosition = assignedCastTarget.position;
            return true;
        }

        if (fishingBlackboard != null && fishingBlackboard.playerCastTarget != null)
        {
            targetPosition = fishingBlackboard.playerCastTarget.position;
            return true;
        }

        if (fishingAnchorManager != null && fishingAnchorManager.CastDirectionReference != null)
        {
            targetPosition = fishingAnchorManager.CastDirectionReference.position;
            return true;
        }

        failureReason = "water_focus_unresolved";
        return false;
    }

    private bool IsMovementYawOwned()
    {
        return movementController != null && movementController.IsRunning;
    }

    private string NormalizeTargetToken(string targetToken)
    {
        return string.IsNullOrWhiteSpace(targetToken) ? string.Empty : targetToken.Trim().ToLowerInvariant();
    }

    private float MoveTowardZero(float value, float maxDelta)
    {
        return Mathf.MoveTowards(value, 0f, Mathf.Max(0f, maxDelta));
    }

    private Rigidbody GetCompanionRigidbody()
    {
        return companionRoot != null ? companionRoot.GetComponent<Rigidbody>() : null;
    }

    private void ClearPendingRootRotation()
    {
        rootRotationPending = false;
        pendingRootRotation = companionRoot != null ? companionRoot.rotation : Quaternion.identity;
    }

    private Transform FindNestedChildByName(Transform root, string childName)
    {
        if (root == null)
        {
            return null;
        }

        if (root.name == childName)
        {
            return root;
        }

        foreach (Transform child in root)
        {
            Transform result = FindNestedChildByName(child, childName);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private struct LookAngles
    {
        public float unclampedYawDegrees;
        public float unclampedPitchDegrees;
        public float clampedYawDegrees;
        public float clampedPitchDegrees;
        public bool requiredBodyAssistToReachComfortableRange;
    }
}
