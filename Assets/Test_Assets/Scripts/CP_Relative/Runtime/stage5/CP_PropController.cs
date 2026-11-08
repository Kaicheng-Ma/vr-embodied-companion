using UnityEngine;

// Stage 5B-2 companion prop controller:
// controls only visibility/state of pre-existing companion-owned visual props.
// It never instantiates, reparents, or touches player-owned runtime objects.
public class CP_PropController : MonoBehaviour
{
    [Header("References")]
    public RoastingStateBlackboard blackboard;
    public CP_ActivityContextRouter activityContextRouter;
    public GameObject companionStickRoot;
    public GameObject companionMarshmallowVisual;
    public Transform companionMarshmallowLookTarget;
    public GameObject companionFishingRodRoot;

    [Header("Runtime Debug")]
    public CP_PrimitiveExecutionStatus status = CP_PrimitiveExecutionStatus.Idle;
    public string currentStickState = "hidden";
    public string currentMarshmallowState = "hidden";
    public string currentFishingRodState = "hidden";
    public string lastCommand = string.Empty;
    public string lastFailureReason = string.Empty;
    public bool runtimeReady;
    public bool fishingRodRuntimeReady;
    public bool visualPhysicsUnsafe;
    public bool fishingRodVisualPhysicsUnsafe;

    public bool IsRunning => status == CP_PrimitiveExecutionStatus.Running;
    public bool IsRuntimeReady => runtimeReady;
    public bool HasCompanionStick => currentStickState == "visible_in_hand" && companionStickRoot != null && companionStickRoot.activeSelf;
    public bool HasCompanionMarshmallow => currentMarshmallowState == "visible_on_stick" && companionMarshmallowVisual != null && companionMarshmallowVisual.activeSelf;
    public bool HasCompanionFishingRod => currentFishingRodState == "visible_in_hand" && companionFishingRodRoot != null && companionFishingRodRoot.activeSelf;
    public string CurrentStickState => currentStickState;
    public string CurrentMarshmallowState => currentMarshmallowState;
    public string CurrentFishingRodState => currentFishingRodState;
    public string LastCommand => lastCommand;
    public string LastFailureReason => lastFailureReason;
    public bool IsFishingRodRuntimeReady => fishingRodRuntimeReady;

    private void Awake()
    {
        RefreshRuntimeReadiness();
        ApplyVisualState("hidden", "hidden", "hidden");
    }

    private void Update()
    {
        RefreshRuntimeReadiness();
        ApplyStateSafetyCleanupIfNeeded();
    }

    private void OnDisable()
    {
        HideAllVisuals();
        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastFailureReason = "companion_prop_disabled";
        runtimeReady = false;
        fishingRodRuntimeReady = false;
    }

    public bool TryBegin(CP_MotionPrimitive primitive, CP_PrimitiveExecutionContext context, out string failureReason)
    {
        failureReason = string.Empty;
        RefreshRuntimeReadiness();

        string propToken = NormalizeToken(primitive != null ? primitive.prop : string.Empty);
        string propStateToken = NormalizeToken(primitive != null ? primitive.state : string.Empty);
        string authorityContext = ResolveAuthorityContext();

        if (string.IsNullOrWhiteSpace(propToken))
        {
            failureReason = "companion_prop_missing";
            SetFailedState(failureReason);
            return false;
        }

        if (!runtimeReady && propToken != "fishing_rod")
        {
            failureReason = string.IsNullOrWhiteSpace(lastFailureReason) ? "prop_controller_unbound" : lastFailureReason;
            SetFailedState(failureReason);
            return false;
        }

        if (propToken != "stick" && propToken != "marshmallow" && propToken != "fishing_rod")
        {
            failureReason = "companion_prop_unknown";
            SetFailedState(failureReason);
            return false;
        }

        if (string.IsNullOrWhiteSpace(propStateToken))
        {
            failureReason = "companion_prop_state_missing";
            SetFailedState(failureReason);
            return false;
        }

        if (!IsAllowedPropStateToken(propToken, propStateToken))
        {
            failureReason = "companion_prop_state_unknown";
            SetFailedState(failureReason);
            return false;
        }

        if (propToken == "fishing_rod" && !fishingRodRuntimeReady)
        {
            failureReason = string.IsNullOrWhiteSpace(lastFailureReason) ? "companion_fishing_rod_root_missing" : lastFailureReason;
            SetFailedState(failureReason);
            return false;
        }

        if (!IsPropCommandAllowedInContext(authorityContext, propToken, propStateToken, out failureReason))
        {
            SetFailedState(failureReason);
            return false;
        }

        if (propToken == "marshmallow" && propStateToken == "visible_on_stick" && !HasCompanionStick)
        {
            failureReason = "companion_stick_required";
            SetFailedState(failureReason);
            return false;
        }

        lastCommand = $"{propToken}:{propStateToken}";
        ApplyRequestedPropState(propToken, propStateToken);
        status = CP_PrimitiveExecutionStatus.Succeeded;
        lastFailureReason = string.Empty;
        return true;
    }

    public CP_PrimitiveExecutionStatus Tick(float deltaTime)
    {
        RefreshRuntimeReadiness();
        if (status == CP_PrimitiveExecutionStatus.Running && !runtimeReady)
        {
            SetFailedState(string.IsNullOrWhiteSpace(lastFailureReason) ? "prop_controller_unbound" : lastFailureReason);
        }

        return status;
    }

    public void Cancel(string reason)
    {
        status = CP_PrimitiveExecutionStatus.Cancelled;
        lastFailureReason = string.IsNullOrWhiteSpace(reason) ? "companion_prop_cancelled" : reason;
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

    public bool TryGetCompanionMarshmallowLookTarget(out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;
        if (!runtimeReady ||
            !HasCompanionMarshmallow ||
            companionMarshmallowLookTarget == null ||
            !companionMarshmallowLookTarget.gameObject.activeInHierarchy)
        {
            return false;
        }

        worldPosition = companionMarshmallowLookTarget.position;
        return true;
    }

    private void RefreshRuntimeReadiness()
    {
        fishingRodRuntimeReady = false;
        fishingRodVisualPhysicsUnsafe = false;

        if (blackboard == null)
        {
            SetRuntimeUnavailable("prop_controller_unbound");
            return;
        }

        if (companionStickRoot == null)
        {
            SetRuntimeUnavailable("companion_stick_visual_missing");
            return;
        }

        if (companionMarshmallowVisual == null)
        {
            SetRuntimeUnavailable("companion_marshmallow_visual_missing");
            return;
        }

        if (companionMarshmallowLookTarget == null)
        {
            SetRuntimeUnavailable("companion_marshmallow_visual_missing");
            return;
        }

        visualPhysicsUnsafe = HierarchyHasVisualPhysics(companionStickRoot) || HierarchyHasVisualPhysics(companionMarshmallowVisual);
        if (visualPhysicsUnsafe)
        {
            SetRuntimeUnavailable("companion_prop_visual_has_physics");
            return;
        }

        runtimeReady = true;
        if (companionFishingRodRoot != null)
        {
            fishingRodVisualPhysicsUnsafe = HierarchyHasVisualPhysics(companionFishingRodRoot);
            if (!fishingRodVisualPhysicsUnsafe)
            {
                fishingRodRuntimeReady = true;
            }
            else
            {
                lastFailureReason = "companion_fishing_rod_visual_has_physics";
            }
        }
    }

    private void SetRuntimeUnavailable(string failureReason)
    {
        runtimeReady = false;
        lastFailureReason = failureReason;
    }

    private void ApplyStateSafetyCleanupIfNeeded()
    {
        string authorityContext = ResolveAuthorityContext();
        switch (authorityContext)
        {
            case "common":
                if (currentStickState != "hidden" || currentMarshmallowState != "hidden" || currentFishingRodState != "hidden")
                {
                    ApplyVisualState("hidden", "hidden", "hidden");
                }
                break;

            case CP_StateNames.F2:
            case CP_StateNames.F3:
                if (currentStickState != "hidden" || currentMarshmallowState != "hidden")
                {
                    ApplyVisualState("hidden", "hidden", currentFishingRodState);
                }
                break;

            case CP_StateNames.S2:
                if (currentMarshmallowState != "hidden")
                {
                    ApplyVisualState(currentStickState, "hidden", "hidden");
                }
                else if (currentFishingRodState != "hidden")
                {
                    ApplyVisualState(currentStickState, currentMarshmallowState, "hidden");
                }
                break;

            case CP_StateNames.S3:
                if (currentFishingRodState != "hidden")
                {
                    ApplyVisualState(currentStickState, currentMarshmallowState, "hidden");
                }
                break;
        }
    }

    private void ApplyRequestedPropState(string propToken, string propStateToken)
    {
        if (propToken == "stick")
        {
            if (propStateToken == "hidden")
            {
                ApplyVisualState("hidden", "hidden", currentFishingRodState);
                return;
            }

            ApplyVisualState("visible_in_hand", currentMarshmallowState, currentFishingRodState);
            if (currentMarshmallowState == "visible_on_stick")
            {
                ApplyVisualState("visible_in_hand", "visible_on_stick", currentFishingRodState);
            }
            return;
        }

        if (propToken == "marshmallow")
        {
            ApplyVisualState(currentStickState, propStateToken, currentFishingRodState);
            return;
        }

        if (propToken == "fishing_rod")
        {
            ApplyVisualState("hidden", "hidden", propStateToken);
        }
    }

    private void ApplyVisualState(string stickState, string marshmallowState, string fishingRodState)
    {
        string normalizedStickState = NormalizeToken(stickState);
        string normalizedMarshmallowState = NormalizeToken(marshmallowState);
        string normalizedFishingRodState = NormalizeToken(fishingRodState);

        if (normalizedStickState == "hidden")
        {
            normalizedMarshmallowState = "hidden";
        }

        currentStickState = normalizedStickState == "visible_in_hand" ? "visible_in_hand" : "hidden";
        currentMarshmallowState = normalizedMarshmallowState == "visible_on_stick" ? "visible_on_stick" : "hidden";
        currentFishingRodState = normalizedFishingRodState == "visible_in_hand" ? "visible_in_hand" : "hidden";

        if (companionStickRoot != null && companionStickRoot.activeSelf != (currentStickState == "visible_in_hand"))
        {
            companionStickRoot.SetActive(currentStickState == "visible_in_hand");
        }

        if (companionMarshmallowVisual != null && companionMarshmallowVisual.activeSelf != (currentMarshmallowState == "visible_on_stick"))
        {
            companionMarshmallowVisual.SetActive(currentMarshmallowState == "visible_on_stick");
        }

        if (companionFishingRodRoot != null && companionFishingRodRoot.activeSelf != (currentFishingRodState == "visible_in_hand"))
        {
            companionFishingRodRoot.SetActive(currentFishingRodState == "visible_in_hand");
        }
    }

    private void HideAllVisuals()
    {
        if (companionMarshmallowVisual != null && companionMarshmallowVisual.activeSelf)
        {
            companionMarshmallowVisual.SetActive(false);
        }

        if (companionStickRoot != null && companionStickRoot.activeSelf)
        {
            companionStickRoot.SetActive(false);
        }

        if (companionFishingRodRoot != null && companionFishingRodRoot.activeSelf)
        {
            companionFishingRodRoot.SetActive(false);
        }

        currentStickState = "hidden";
        currentMarshmallowState = "hidden";
        currentFishingRodState = "hidden";
    }

    private bool IsAllowedPropStateToken(string propToken, string propStateToken)
    {
        if (propToken == "stick")
        {
            return propStateToken == "visible_in_hand" || propStateToken == "hidden";
        }

        if (propToken == "marshmallow")
        {
            return propStateToken == "visible_on_stick" || propStateToken == "hidden";
        }

        if (propToken == "fishing_rod")
        {
            return propStateToken == "visible_in_hand" || propStateToken == "hidden";
        }

        return false;
    }

    private bool IsPropCommandAllowedInContext(string authorityContext, string propToken, string propStateToken, out string failureReason)
    {
        failureReason = string.Empty;
        switch (authorityContext)
        {
            case "common":
                failureReason = "companion_prop_not_allowed_in_state";
                return false;

            case CP_StateNames.F2:
            case CP_StateNames.F3:
                if (propToken == "fishing_rod")
                {
                    return true;
                }
                failureReason = "companion_prop_not_allowed_in_state";
                return false;

            case CP_StateNames.S2:
                if (propToken != "stick")
                {
                    failureReason = "companion_marshmallow_not_allowed_in_state";
                    return false;
                }
                return true;

            case CP_StateNames.S3:
                if (propToken == "stick")
                {
                    return true;
                }
                if (propToken == "marshmallow")
                {
                    return true;
                }
                break;
        }

        failureReason = "companion_prop_not_allowed_in_state";
        return false;
    }

    private bool HierarchyHasVisualPhysics(GameObject root)
    {
        if (root == null)
        {
            return false;
        }

        Rigidbody[] rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rigidbodies.Length; i++)
        {
            if (rigidbodies[i] != null)
            {
                return true;
            }
        }

        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null && colliders[i].enabled)
            {
                return true;
            }
        }

        return false;
    }

    private void SetFailedState(string failureReason)
    {
        status = CP_PrimitiveExecutionStatus.Failed;
        lastFailureReason = string.IsNullOrWhiteSpace(failureReason) ? "companion_prop_unknown" : failureReason;
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

    private string NormalizeToken(string token)
    {
        return string.IsNullOrWhiteSpace(token) ? string.Empty : token.Trim().ToLowerInvariant();
    }

    private string ResolveAuthorityContext()
    {
        if (activityContextRouter != null)
        {
            string canonicalState = ResolveAuthoritativeState();
            if (canonicalState == CP_StateNames.F2 || canonicalState == CP_StateNames.F3)
            {
                return canonicalState;
            }

            string activity = NormalizeToken(activityContextRouter.ActiveActivity);
            if (activity == "common")
            {
                return activity;
            }

            if (activity == "campfire")
            {
                return ResolveAuthoritativeState();
            }
        }

        return ResolveAuthoritativeState();
    }
}
