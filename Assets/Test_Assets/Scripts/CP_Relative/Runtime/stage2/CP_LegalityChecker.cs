using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Runtime legality layer:
// sanitizes formal policy JSON into the currently executable Unity subset and
// exposes focused revalidation for the active primitive during scheduler/state checks.
public class CP_LegalityChecker : MonoBehaviour
{
    // Public Fishing policies use semantic cycle duration only; runtime timeout
    // grace remains owned by CP_FishingCastCycleController.
    public const float FishingCastCycleSemanticDurationSeconds = 20.0f;
    private const float FishingCastCycleDurationToleranceSeconds = 0.01f;

    [Header("References")]
    public CP_PerceptionBuilder perceptionBuilder;
    public CP_PolicyReceiver policyReceiver;
    public CP_PropController propController;

    [Header("Legality Limits")]
    public float minimumDurationSeconds = 0.1f;
    public float maximumDurationSeconds = 10.0f;

    [Header("Logging")]
    public bool logLegalityWarnings = true;

    [Header("Latest Legality Check")]
    [TextArea(4, 12)]
    public string latestLegalitySummary = string.Empty;
    [TextArea(4, 16)]
    public string latestLegalityWarningsText = string.Empty;

    private readonly StringBuilder summaryBuilder = new StringBuilder();
    private readonly StringBuilder warningBuilder = new StringBuilder();
    private string lastLoggedWarningsText = string.Empty;

    public bool ValidateCurrentExecution(string authoritativeState, CP_MotionPrimitive currentPrimitive, out string reason)
    {
        reason = string.Empty;
        string normalizedState = NormalizeStateName(authoritativeState);
        if (string.IsNullOrWhiteSpace(normalizedState))
        {
            reason = "authoritative_state_missing";
            return false;
        }

        if (currentPrimitive == null)
        {
            reason = "current_primitive_missing";
            return false;
        }

        string primitiveType = NormalizePrimitiveType(currentPrimitive.type);
        if (string.IsNullOrWhiteSpace(primitiveType))
        {
            reason = "current_primitive_type_missing";
            return false;
        }

        if (!IsKnownPrimitiveType(primitiveType))
        {
            reason = "current_primitive_type_unknown";
            return false;
        }

        if (!IsPrimitiveImplementedInCurrentStage(primitiveType))
        {
            reason = "primitive_not_implemented_in_current_stage";
            return false;
        }

        if (!IsPrimitiveDurationValid(currentPrimitive))
        {
            reason = "current_primitive_duration_invalid";
            return false;
        }

        if (primitiveType != "set_companion_prop" &&
            !string.IsNullOrWhiteSpace(currentPrimitive.state) &&
            NormalizeStateName(currentPrimitive.state) != normalizedState)
        {
            reason = "current_primitive_state_mismatch";
            return false;
        }

        if (RequiresTargetResolution(primitiveType))
        {
            if (!IsSupportedRuntimeLookTarget(currentPrimitive.target))
            {
                reason = "unsupported_look_target";
                return false;
            }

            if (!TryResolveTargetPosition(currentPrimitive.target, out _, out _, out string targetWarning))
            {
                reason = string.IsNullOrWhiteSpace(targetWarning) ? "current_primitive_target_unresolved" : targetWarning;
                return false;
            }
        }

        if (!IsPrimitiveRuntimeStructureValid(currentPrimitive, out reason))
        {
            return false;
        }

        if (primitiveType == "perform_fishing_cast_cycle")
        {
            return IsAllowedFishingCastCyclePrimitive(currentPrimitive, out reason);
        }

        if (primitiveType == "set_companion_prop" &&
            NormalizeOptionalToken(currentPrimitive.prop) == "marshmallow" &&
            NormalizeOptionalToken(currentPrimitive.state) == "visible_on_stick" &&
            !IsCompanionStickCurrentlyVisible())
        {
            reason = "companion_stick_required";
            return false;
        }

        if (primitiveType == "move_within_scope" &&
            NormalizeScope(currentPrimitive.scope) == "transition_only" &&
            NormalizeTarget(currentPrimitive.target) == "nearest_valid_fishing_interaction_area_point" &&
            IsFishingF2TransitionExecutionContext())
        {
            return true;
        }

        if (IsFishingF2TransitionExecutionContext() &&
            IsAllowedFishingF2TransitionPrimitive(currentPrimitive, out string fishingF2TransitionReason))
        {
            return true;
        }

        if (primitiveType == "move_within_scope" &&
            NormalizeScope(currentPrimitive.scope) == "transition_only" &&
            NormalizeTarget(currentPrimitive.target) == "assigned_fishing_anchor" &&
            IsFishingF3TransitionExecutionContext())
        {
            return true;
        }

        if (IsFishingF3TransitionExecutionContext() &&
            IsAllowedFishingF3TransitionPrimitive(currentPrimitive, out string fishingTransitionReason))
        {
            return true;
        }

        // S2/S3 state-entry base hand poses are transition-owned. Revalidation
        // admits only the exact entry package primitives after structure checks.
        if (normalizedState == CP_StateNames.S2 &&
            IsAllowedCampfireS2TransitionPrimitive(currentPrimitive, out string s2TransitionReason))
        {
            return true;
        }

        if (normalizedState == CP_StateNames.S3 &&
            IsAllowedCampfireS3TransitionPrimitive(currentPrimitive, out string s3TransitionReason))
        {
            return true;
        }

        return IsPrimitiveAllowedForPolicy(normalizedState, "fallback", currentPrimitive, out reason);
    }

    public CP_PolicyResponse SanitizePolicy(CP_PolicyResponse source, out List<string> warnings)
    {
        warnings = new List<string>();

        if (source == null)
        {
            warnings.Add("CP_LegalityChecker: source policy is null.");
            CP_PolicyResponse errorPolicy = CreatePolicyErrorResponse("policy_null", "Source policy is null.");
            latestLegalityWarningsText = BuildWarningsText(warnings);
            latestLegalitySummary = BuildSummary(errorPolicy, 0, 0, warnings.Count);
            return errorPolicy;
        }

        EnsurePolicySections(source);
        CP_PolicyResponse sanitized = ClonePolicyResponse(source);
        sanitized.motion_primitives.Clear();

        string policySource = NormalizePolicySource(source.policy != null ? source.policy.policy_source : string.Empty);
        string currentState = NormalizeStateName(source.state != null ? source.state.current_state : string.Empty);

        if (source.control != null && (!source.control.execute_policy || !IsExecutablePolicySource(policySource)))
        {
            warnings.Add(
                !source.control.execute_policy
                    ? "CP_LegalityChecker: execute_policy is false, so motion primitives will remain empty."
                    : $"CP_LegalityChecker: policy source '{policySource}' is not executable, so motion primitives will remain empty."
            );
            latestLegalityWarningsText = BuildWarningsText(warnings);
            latestLegalitySummary = BuildSummary(sanitized, source.motion_primitives.Count, 0, warnings.Count);
            return sanitized;
        }

        if (string.IsNullOrWhiteSpace(currentState))
        {
            warnings.Add("CP_LegalityChecker: current_state is missing.");
            CP_PolicyResponse errorPolicy = CreatePolicyErrorResponse("current_state_missing", "Policy current_state is missing.");
            latestLegalityWarningsText = BuildWarningsText(warnings);
            latestLegalitySummary = BuildSummary(errorPolicy, source.motion_primitives.Count, 0, warnings.Count);
            return errorPolicy;
        }

        bool transitionPolicyInvalid = false;
        string transitionFailureReason = string.Empty;
        int acceptedCount = 0;
        List<CP_MotionPrimitive> primitives = source.motion_primitives ?? new List<CP_MotionPrimitive>();
        bool stickVisibleForSequence = IsCompanionStickCurrentlyVisible();

        for (int i = 0; i < primitives.Count; i++)
        {
            CP_MotionPrimitive primitive = primitives[i];
            if (primitive == null)
            {
                string warning = $"CP_LegalityChecker: motion primitive #{i} is null and was skipped.";
                warnings.Add(warning);
                if (policySource == "transition_priority")
                {
                    transitionPolicyInvalid = true;
                    transitionFailureReason = "transition_primitive_null";
                    break;
                }
                continue;
            }

            if (!TrySanitizePrimitive(primitive, currentState, policySource, stickVisibleForSequence, out CP_MotionPrimitive sanitizedPrimitive, out string failureReason))
            {
                warnings.Add($"CP_LegalityChecker: source primitive #{i} was skipped ({BuildPrimitiveDebugLabel(primitive)}). Reason: {failureReason}");
                if (policySource == "transition_priority")
                {
                    transitionPolicyInvalid = true;
                    transitionFailureReason = failureReason;
                    break;
                }
                continue;
            }

            sanitized.motion_primitives.Add(sanitizedPrimitive);
            acceptedCount++;
            if (NormalizePrimitiveType(sanitizedPrimitive.type) == "set_companion_prop")
            {
                if (NormalizeOptionalToken(sanitizedPrimitive.prop) == "stick")
                {
                    stickVisibleForSequence = NormalizeOptionalToken(sanitizedPrimitive.state) == "visible_in_hand";
                }
            }
        }

        if (transitionPolicyInvalid)
        {
            warnings.Add($"CP_LegalityChecker: transition policy rejected because {transitionFailureReason}.");
            CP_PolicyResponse errorPolicy = CreatePolicyErrorResponse("transition_policy_invalid", transitionFailureReason);
            errorPolicy.state.current_state = source.state.current_state;
            errorPolicy.state.previous_state = source.state.previous_state;
            errorPolicy.state.state_changed = source.state.state_changed;
            errorPolicy.state.state_change_id = source.state.state_change_id;
            errorPolicy.state.state_reason = source.state.state_reason;
            errorPolicy.queue = CloneQueueSection(source.queue);
            errorPolicy.debug.warnings.AddRange(warnings);
            latestLegalityWarningsText = BuildWarningsText(warnings);
            latestLegalitySummary = BuildSummary(errorPolicy, primitives.Count, 0, warnings.Count);
            return errorPolicy;
        }

        latestLegalityWarningsText = BuildWarningsText(warnings);
        latestLegalitySummary = BuildSummary(sanitized, primitives.Count, acceptedCount, warnings.Count);
        return sanitized;
    }

    public bool TryResolveTargetPosition(string targetId, out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = string.Empty;
        warning = string.Empty;

        switch (NormalizeTarget(targetId))
        {
            case "campfire_center":
                return TryResolveCampfireCenter(out worldPosition, out resolvedTargetName, out warning);
            case "player_head":
                return TryResolvePlayerHead(out worldPosition, out resolvedTargetName, out warning);
            case "interaction_area":
                return TryResolveInteractionArea(out worldPosition, out resolvedTargetName, out warning);
            case "neutral_forward":
                return TryResolveNeutralForward(out worldPosition, out resolvedTargetName, out warning);
            case "companion_marshmallow":
                return TryResolveCompanionMarshmallow(out worldPosition, out resolvedTargetName, out warning);
            case "assigned_fishing_cast_target":
                return TryResolveAssignedFishingCastTarget(out worldPosition, out resolvedTargetName, out warning);
            case "water_focus":
                return TryResolveWaterFocus(out worldPosition, out resolvedTargetName, out warning);
            case "player_fishing_rod":
                return TryResolvePlayerFishingRod(out worldPosition, out resolvedTargetName, out warning);
            case "player_bobber":
                return TryResolvePlayerBobber(out worldPosition, out resolvedTargetName, out warning);
            default:
                warning = $"CP_LegalityChecker: target '{targetId}' is unknown or not supported in runtime target resolution.";
                return false;
        }
    }

    private bool TrySanitizePrimitive(
        CP_MotionPrimitive primitive,
        string currentState,
        string policySource,
        bool stickVisibleForSequence,
        out CP_MotionPrimitive sanitizedPrimitive,
        out string failureReason)
    {
        sanitizedPrimitive = null;
        failureReason = string.Empty;

        string primitiveType = NormalizePrimitiveType(primitive.type);
        if (!IsKnownPrimitiveType(primitiveType))
        {
            failureReason = "primitive_type_unknown";
            return false;
        }

        if (!IsPrimitiveImplementedInCurrentStage(primitiveType))
        {
            failureReason = "primitive_not_implemented_in_current_stage";
            return false;
        }

        if (!IsPrimitiveDurationValid(primitive))
        {
            failureReason = "primitive_duration_invalid";
            return false;
        }

        sanitizedPrimitive = ClonePrimitive(primitive);
        sanitizedPrimitive.type = primitiveType;
        sanitizedPrimitive.scope = NormalizeOptionalToken(primitive.scope);
        sanitizedPrimitive.target = NormalizeOptionalToken(primitive.target);
        sanitizedPrimitive.pose = NormalizeOptionalToken(primitive.pose);
        sanitizedPrimitive.prop = NormalizeOptionalToken(primitive.prop);
        sanitizedPrimitive.state = primitiveType == "set_companion_prop"
            ? NormalizeOptionalToken(primitive.state)
            : NormalizeStateName(primitive.state);
        sanitizedPrimitive.gesture = NormalizeOptionalToken(primitive.gesture);

        if (RequiresTargetResolution(primitiveType))
        {
            if (!IsSupportedRuntimeLookTarget(sanitizedPrimitive.target))
            {
                failureReason = "unsupported_look_target";
                return false;
            }

            if (!TryResolveTargetPosition(sanitizedPrimitive.target, out _, out string targetLabel, out string targetWarning))
            {
                failureReason = string.IsNullOrWhiteSpace(targetWarning) ? "primitive_target_unresolved" : targetWarning;
                return false;
            }

            sanitizedPrimitive.target = targetLabel;
        }

        if (!IsPrimitiveRuntimeStructureValid(sanitizedPrimitive, out failureReason))
        {
            return false;
        }

        return IsPrimitiveAllowedForPolicy(currentState, policySource, sanitizedPrimitive, out failureReason, stickVisibleForSequence);
    }

    private bool IsPrimitiveAllowedForPolicy(string currentState, string policySource, CP_MotionPrimitive primitive, out string reason, bool stickVisibleForSequence = false)
    {
        reason = string.Empty;
        string primitiveType = NormalizePrimitiveType(primitive.type);
        string scope = NormalizeScope(primitive.scope);
        string target = NormalizeTarget(primitive.target);
        bool fishingF2TransitionAdmissionContext = IsFishingF2TransitionAdmissionContext();
        bool fishingF3TransitionAdmissionContext = IsFishingF3TransitionAdmissionContext();

        if (string.IsNullOrWhiteSpace(currentState))
        {
            reason = "primitive_state_missing";
            return false;
        }

        if (primitiveType != "set_companion_prop" &&
            !string.IsNullOrWhiteSpace(primitive.state) &&
            NormalizeStateName(primitive.state) != currentState)
        {
            reason = "primitive_state_mismatch";
            return false;
        }

        if (policySource == "transition_priority")
        {
            if (fishingF2TransitionAdmissionContext)
            {
                if (IsAllowedFishingF2TransitionPrimitive(primitive, out string fishingReason))
                {
                    return true;
                }

                return Fail(out reason, string.IsNullOrWhiteSpace(fishingReason) ? "f2_fishing_transition_target_invalid" : fishingReason);
            }

            if (fishingF3TransitionAdmissionContext)
            {
                if (IsAllowedFishingF3TransitionPrimitive(primitive, out string fishingReason))
                {
                    return true;
                }

                return Fail(out reason, string.IsNullOrWhiteSpace(fishingReason) ? "f3_fishing_transition_target_invalid" : fishingReason);
            }

            if (currentState == CP_StateNames.S2)
            {
                return IsAllowedCampfireS2TransitionPrimitive(primitive, out reason);
            }

            if (currentState == CP_StateNames.S3)
            {
                return IsAllowedCampfireS3TransitionPrimitive(primitive, out reason);
            }

            if (fishingF2TransitionAdmissionContext)
            {
                return scope == "transition_only" && target == "nearest_valid_fishing_interaction_area_point"
                    ? true
                    : Fail(out reason, "f2_fishing_transition_target_invalid");
            }

            return Fail(out reason, "transition_priority_not_supported_in_state");
        }

        if (primitiveType == "move_within_scope")
        {
            switch (currentState)
            {
                case CP_StateNames.S1:
                    if (scope != "around_player" && scope != "named_point")
                    {
                        return Fail(out reason, "s1_move_scope_invalid");
                    }
                    if (scope == "named_point" && string.IsNullOrWhiteSpace(target))
                    {
                        return Fail(out reason, "named_point_target_missing");
                    }
                    if (scope == "around_player" && !string.IsNullOrWhiteSpace(target) && target != "player_head")
                    {
                        return Fail(out reason, "around_player_target_invalid");
                    }
                    return true;

                case CP_StateNames.S2:
                    if (scope != "interaction_area")
                    {
                        return Fail(out reason, "s2_normal_move_scope_invalid");
                    }
                    if (!string.IsNullOrWhiteSpace(target) && target != "interaction_area")
                    {
                        return Fail(out reason, "s2_normal_move_target_invalid");
                    }
                    return true;

                case CP_StateNames.F2:
                    if (scope != "interaction_area")
                    {
                        return Fail(out reason, "f2_normal_move_scope_invalid");
                    }
                    if (!string.IsNullOrWhiteSpace(target) && target != "interaction_area")
                    {
                        return Fail(out reason, "f2_normal_move_target_invalid");
                    }
                    return true;

                case CP_StateNames.F3:
                case CP_StateNames.S3:
                    return Fail(out reason, currentState == CP_StateNames.F3 ? "f3_normal_policy_forbids_move_within_scope" : "s3_normal_policy_forbids_move_within_scope");
            }
        }

        if (primitiveType == "set_hand_pose")
        {
            if (IsNormalPolicyHandPoseTransitionOwned(currentState))
            {
                return Fail(out reason, "normal_hand_pose_owned_by_transition");
            }
            return IsAllowedHandPoseForState(currentState, primitive.pose, out reason);
        }

        if (primitiveType == "perform_fishing_cast_cycle")
        {
            return IsAllowedFishingCastCyclePrimitive(primitive, out reason);
        }

        if (primitiveType == "set_companion_prop")
        {
            return IsAllowedCompanionPropForState(currentState, primitive.prop, primitive.state, out reason, stickVisibleForSequence);
        }

        if (scope == "transition_only")
        {
            return Fail(out reason, "transition_only_scope_forbidden_for_normal_policy");
        }

        return true;
    }

    private bool IsNormalPolicyHandPoseTransitionOwned(string currentState)
    {
        switch (NormalizeStateName(currentState))
        {
            case CP_StateNames.S2:
            case CP_StateNames.F2:
            case CP_StateNames.F3:
            case CP_StateNames.S3:
                return true;
            default:
                return false;
        }
    }

    private bool IsAllowedCampfireS2TransitionPrimitive(CP_MotionPrimitive primitive, out string reason)
    {
        reason = string.Empty;
        if (primitive == null)
        {
            reason = "transition_primitive_null";
            return false;
        }

        string primitiveType = NormalizePrimitiveType(primitive.type);
        string scope = NormalizeScope(primitive.scope);
        string target = NormalizeTarget(primitive.target);
        string pose = NormalizeOptionalToken(primitive.pose);

        switch (primitiveType)
        {
            case "move_within_scope":
                return scope == "transition_only" && target == "nearest_valid_interaction_area_point"
                    ? true
                    : Fail(out reason, "s2_transition_target_invalid");
            case "set_hand_pose":
                return pose == "ready"
                    ? true
                    : Fail(out reason, "s2_transition_pose_invalid");
            default:
                return Fail(out reason, "transition_priority_requires_move_within_scope_or_entry_pose");
        }
    }

    private bool IsAllowedCampfireS3TransitionPrimitive(CP_MotionPrimitive primitive, out string reason)
    {
        reason = string.Empty;
        if (primitive == null)
        {
            reason = "transition_primitive_null";
            return false;
        }

        string primitiveType = NormalizePrimitiveType(primitive.type);
        string scope = NormalizeScope(primitive.scope);
        string target = NormalizeTarget(primitive.target);
        string pose = NormalizeOptionalToken(primitive.pose);

        switch (primitiveType)
        {
            case "move_within_scope":
                return scope == "transition_only" && target == "nearest_free_roasting_anchor"
                    ? true
                    : Fail(out reason, "s3_transition_target_invalid");
            case "set_hand_pose":
                return pose == "roast_pose"
                    ? true
                    : Fail(out reason, "s3_transition_pose_invalid");
            default:
                return Fail(out reason, "transition_priority_requires_move_within_scope_or_entry_pose");
        }
    }

    private bool IsFishingF2TransitionAdmissionContext()
    {
        CP_PerceptionPacket packet = perceptionBuilder != null ? perceptionBuilder.latestPerceptionPacket : null;
        if (packet == null || packet.activity == null || packet.entry_checks == null)
        {
            return false;
        }

        return NormalizeOptionalToken(packet.activity.id) == "fishing"
               && NormalizeOptionalToken(packet.activity.phase) == "ready"
               && !(packet.entry_checks.companion_inside_fishing_interaction_area &&
                    packet.entry_checks.fishing_rod_ready &&
                    NormalizeOptionalToken(packet.companion.current_hand_pose) == "fishing_ready");
    }

    private bool IsFishingF2TransitionExecutionContext()
    {
        CP_PerceptionPacket packet = perceptionBuilder != null ? perceptionBuilder.latestPerceptionPacket : null;
        if (packet == null || packet.activity == null)
        {
            return false;
        }

        return NormalizeOptionalToken(packet.activity.id) == "fishing"
               && NormalizeOptionalToken(packet.activity.phase) == "ready";
    }

    private bool IsFishingF3TransitionAdmissionContext()
    {
        CP_PerceptionPacket packet = perceptionBuilder != null ? perceptionBuilder.latestPerceptionPacket : null;
        if (packet == null || packet.activity == null || packet.companion == null || packet.entry_checks == null)
        {
            return false;
        }

        return NormalizeOptionalToken(packet.activity.id) == "fishing"
               && NormalizeOptionalToken(packet.activity.phase) == "engagement"
               && !string.IsNullOrWhiteSpace(packet.companion.assigned_fishing_anchor_id)
               && !(packet.entry_checks.companion_at_valid_fishing_anchor &&
                    packet.entry_checks.fishing_rod_ready &&
                    NormalizeOptionalToken(packet.companion.current_hand_pose) == "fishing_ready");
    }

    private bool IsFishingF3TransitionExecutionContext()
    {
        CP_PerceptionPacket packet = perceptionBuilder != null ? perceptionBuilder.latestPerceptionPacket : null;
        if (packet == null || packet.activity == null)
        {
            return false;
        }

        return NormalizeOptionalToken(packet.activity.id) == "fishing"
               && NormalizeOptionalToken(packet.activity.phase) == "engagement";
    }

    private bool IsPrimitiveRuntimeStructureValid(CP_MotionPrimitive primitive, out string reason)
    {
        reason = string.Empty;
        if (primitive == null)
        {
            return Fail(out reason, "primitive_missing");
        }

        string primitiveType = NormalizePrimitiveType(primitive.type);
        string scope = NormalizeScope(primitive.scope);
        string target = NormalizeTarget(primitive.target);
        string pose = NormalizeOptionalToken(primitive.pose);
        string prop = NormalizeOptionalToken(primitive.prop);
        string primitiveStateName = NormalizeStateName(primitive.state);
        string propStateToken = NormalizeOptionalToken(primitive.state);
        string gesture = NormalizeOptionalToken(primitive.gesture);

        if (!IsRadiusValid(primitive.radius_min, primitive.radius_max, out reason))
        {
            return false;
        }

        switch (primitiveType)
        {
            case "look_at":
            case "face_toward":
                if (string.IsNullOrWhiteSpace(target))
                {
                    return Fail(out reason, "primitive_target_missing");
                }
                if (!string.IsNullOrWhiteSpace(scope) ||
                    !string.IsNullOrWhiteSpace(pose) ||
                    !string.IsNullOrWhiteSpace(prop) ||
                    !string.IsNullOrWhiteSpace(primitiveStateName) ||
                    !string.IsNullOrWhiteSpace(gesture))
                {
                    return Fail(out reason, "look_or_face_contains_unexpected_fields");
                }
                return true;

            case "move_within_scope":
                if (string.IsNullOrWhiteSpace(scope))
                {
                    return Fail(out reason, "move_scope_missing");
                }
                if (!IsAllowedMoveScope(scope))
                {
                    return Fail(out reason, "move_scope_unknown");
                }
                if ((scope == "named_point" || scope == "transition_only") && string.IsNullOrWhiteSpace(target))
                {
                    return Fail(out reason, "move_target_missing");
                }
                if (!string.IsNullOrWhiteSpace(pose) ||
                    !string.IsNullOrWhiteSpace(prop) ||
                    !string.IsNullOrWhiteSpace(gesture))
                {
                    return Fail(out reason, "move_contains_unexpected_fields");
                }
                return true;

            case "hold_position":
            case "idle_motion":
                if (!string.IsNullOrWhiteSpace(scope) ||
                    !string.IsNullOrWhiteSpace(pose) ||
                    !string.IsNullOrWhiteSpace(prop) ||
                    !string.IsNullOrWhiteSpace(gesture))
                {
                    return Fail(out reason, "hold_or_idle_contains_unexpected_fields");
                }
                return true;

            case "set_hand_pose":
                if (string.IsNullOrWhiteSpace(pose))
                {
                    return Fail(out reason, "hand_pose_missing");
                }
                if (pose != "relaxed" && pose != "neutral" && pose != "ready" && pose != "roast_pose" && pose != "fishing_ready")
                {
                    return Fail(out reason, "hand_pose_unknown");
                }
                if (!string.IsNullOrWhiteSpace(scope) ||
                    !string.IsNullOrWhiteSpace(target) ||
                    !string.IsNullOrWhiteSpace(prop) ||
                    !string.IsNullOrWhiteSpace(propStateToken) ||
                    !string.IsNullOrWhiteSpace(gesture))
                {
                    return Fail(out reason, "hand_pose_contains_unexpected_fields");
                }
                return true;

            case "set_companion_prop":
                if (string.IsNullOrWhiteSpace(prop))
                {
                    return Fail(out reason, "companion_prop_missing");
                }
                if (prop != "stick" && prop != "marshmallow" && prop != "fishing_rod")
                {
                    return Fail(out reason, "companion_prop_unknown");
                }
                if (string.IsNullOrWhiteSpace(propStateToken))
                {
                    return Fail(out reason, "companion_prop_state_missing");
                }
                if (!IsKnownCompanionPropState(prop, propStateToken))
                {
                    return Fail(out reason, "companion_prop_state_unknown");
                }
                if (!string.IsNullOrWhiteSpace(scope) ||
                    !string.IsNullOrWhiteSpace(target) ||
                    !string.IsNullOrWhiteSpace(pose) ||
                    !string.IsNullOrWhiteSpace(gesture))
                {
                    return Fail(out reason, "companion_prop_contains_unexpected_fields");
                }
                return true;

            case "play_gesture":
                return !string.IsNullOrWhiteSpace(gesture) ? true : Fail(out reason, "gesture_missing");
            case "perform_fishing_cast_cycle":
                if (!string.IsNullOrWhiteSpace(scope) ||
                    !string.IsNullOrWhiteSpace(target) ||
                    !string.IsNullOrWhiteSpace(pose) ||
                    !string.IsNullOrWhiteSpace(prop) ||
                    !string.IsNullOrWhiteSpace(propStateToken) ||
                    !string.IsNullOrWhiteSpace(gesture))
                {
                    return Fail(out reason, "fishing_cast_cycle_contains_unexpected_fields");
                }
                return true;
        }

        return Fail(out reason, "primitive_structure_unknown");
    }

    private bool IsAllowedMoveScope(string scope)
    {
        switch (scope)
        {
            case "around_player":
            case "interaction_area":
            case "named_point":
            case "transition_only":
                return true;
            default:
                return false;
        }
    }

    private bool IsRadiusValid(float radiusMin, float radiusMax, out string reason)
    {
        reason = string.Empty;
        if (radiusMin < 0f)
        {
            return Fail(out reason, "radius_min_negative");
        }
        if (radiusMax < 0f)
        {
            return Fail(out reason, "radius_max_negative");
        }
        if (radiusMax > 0f && radiusMin > radiusMax)
        {
            return Fail(out reason, "radius_min_greater_than_radius_max");
        }
        return true;
    }

    private bool RequiresTargetResolution(string primitiveType)
    {
        return primitiveType == "look_at" || primitiveType == "face_toward";
    }

    private bool IsSupportedRuntimeLookTarget(string targetToken)
    {
        switch (NormalizeTarget(targetToken))
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

    private bool IsKnownPrimitiveType(string primitiveType)
    {
        switch (primitiveType)
        {
            case "look_at":
            case "face_toward":
            case "move_within_scope":
            case "hold_position":
            case "idle_motion":
            case "set_hand_pose":
            case "set_companion_prop":
            case "play_gesture":
            case "perform_fishing_cast_cycle":
                return true;
            default:
                return false;
        }
    }

    private bool IsPrimitiveImplementedInCurrentStage(string primitiveType)
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

    private bool IsExecutablePolicySource(string policySource)
    {
        switch (NormalizePolicySource(policySource))
        {
            case "ai":
            case "fallback":
            case "transition_priority":
                return true;
            default:
                return false;
        }
    }

    private bool IsDurationValid(float durationSeconds)
    {
        return durationSeconds >= minimumDurationSeconds && durationSeconds <= maximumDurationSeconds;
    }

    private bool IsPrimitiveDurationValid(CP_MotionPrimitive primitive)
    {
        if (primitive == null)
        {
            return false;
        }

        // F3 Fishing cast cycle is intentionally longer than ordinary policy
        // primitives, but only the canonical semantic duration is admitted here.
        if (NormalizePrimitiveType(primitive.type) == "perform_fishing_cast_cycle")
        {
            return Mathf.Abs(primitive.duration - FishingCastCycleSemanticDurationSeconds) <= FishingCastCycleDurationToleranceSeconds;
        }

        return IsDurationValid(primitive.duration);
    }

    private bool IsAllowedHandPoseForState(string currentState, string poseToken, out string reason)
    {
        reason = string.Empty;
        string pose = NormalizeOptionalToken(poseToken);
        switch (NormalizeStateName(currentState))
        {
            case CP_StateNames.S1:
                return pose == "neutral" || pose == "relaxed"
                    ? true
                    : Fail(out reason, "hand_pose_not_allowed_in_state");
            case CP_StateNames.F2:
                return pose == "fishing_ready"
                    ? true
                    : Fail(out reason, "hand_pose_not_allowed_in_state");
            case CP_StateNames.F3:
                return pose == "fishing_ready"
                    ? true
                    : Fail(out reason, "hand_pose_not_allowed_in_state");
            case CP_StateNames.S2:
                return pose == "neutral" || pose == "relaxed" || pose == "ready"
                    ? true
                    : Fail(out reason, "hand_pose_not_allowed_in_state");
            case CP_StateNames.S3:
                return pose == "neutral" || pose == "relaxed" || pose == "ready" || pose == "roast_pose"
                    ? true
                    : Fail(out reason, "hand_pose_not_allowed_in_state");
            default:
                if (IsFishingTransitionContext())
                {
                    return pose == "fishing_ready"
                        ? true
                        : Fail(out reason, "hand_pose_not_allowed_in_state");
                }
                return Fail(out reason, "hand_pose_not_allowed_in_state");
        }
    }

    private bool IsAllowedCompanionPropForState(string currentState, string propToken, string propStateToken, out string reason, bool stickVisibleForSequence = false)
    {
        reason = string.Empty;
        string prop = NormalizeOptionalToken(propToken);
        string propState = NormalizeOptionalToken(propStateToken);
        if (!IsKnownCompanionPropState(prop, propState))
        {
            return Fail(out reason, "companion_prop_state_unknown");
        }

        switch (NormalizeStateName(currentState))
        {
            case CP_StateNames.S1:
                return Fail(out reason, "companion_prop_not_allowed_in_state");

            case CP_StateNames.S2:
                if (prop == "stick")
                {
                    return true;
                }
                return Fail(out reason, "companion_marshmallow_not_allowed_in_state");

            case CP_StateNames.F2:
                if (prop == "fishing_rod")
                {
                    return propState == "visible_in_hand" || propState == "hidden"
                        ? true
                        : Fail(out reason, "companion_prop_state_unknown");
                }
                return Fail(out reason, "companion_prop_not_allowed_in_state");

            case CP_StateNames.F3:
                if (prop == "fishing_rod")
                {
                    return propState == "visible_in_hand" || propState == "hidden"
                        ? true
                        : Fail(out reason, "companion_prop_state_unknown");
                }
                return Fail(out reason, "companion_prop_not_allowed_in_state");

            case CP_StateNames.S3:
                if (prop == "stick")
                {
                    return true;
                }
                if (prop == "marshmallow")
                {
                    if (propState == "visible_on_stick" &&
                        !stickVisibleForSequence &&
                        !IsCompanionStickCurrentlyVisible())
                    {
                        return Fail(out reason, "companion_stick_required");
                    }
                    return true;
                }
                break;
        }

        if (IsFishingTransitionContext())
        {
            if (prop == "fishing_rod")
            {
                return propState == "visible_in_hand" || propState == "hidden"
                    ? true
                    : Fail(out reason, "companion_prop_state_unknown");
            }
        }

        return Fail(out reason, "companion_prop_not_allowed_in_state");
    }

    private bool IsKnownCompanionPropState(string propToken, string propStateToken)
    {
        string prop = NormalizeOptionalToken(propToken);
        string state = NormalizeOptionalToken(propStateToken);
        if (prop == "stick")
        {
            return state == "visible_in_hand" || state == "hidden";
        }
        if (prop == "marshmallow")
        {
            return state == "visible_on_stick" || state == "hidden";
        }
        if (prop == "fishing_rod")
        {
            return state == "visible_in_hand" || state == "hidden";
        }
        return false;
    }

    private bool IsFishingTransitionContext()
    {
        return IsFishingF2TransitionAdmissionContext() ||
               IsFishingF3TransitionAdmissionContext() ||
               IsFishingF2TransitionExecutionContext() ||
               IsFishingF3TransitionExecutionContext();
    }

    private bool IsAllowedFishingCastCyclePrimitive(CP_MotionPrimitive primitive, out string reason)
    {
        reason = string.Empty;
        if (primitive == null)
        {
            reason = "fishing_cast_cycle_missing";
            return false;
        }

        if (NormalizePrimitiveType(primitive.type) != "perform_fishing_cast_cycle")
        {
            reason = "fishing_cast_cycle_primitive_type_invalid";
            return false;
        }

        CP_PerceptionPacket packet = perceptionBuilder != null ? perceptionBuilder.latestPerceptionPacket : null;
        if (packet == null || packet.activity == null || packet.companion == null || packet.entry_checks == null)
        {
            return Fail(out reason, "fishing_cast_cycle_perception_unavailable");
        }

        if (NormalizeOptionalToken(packet.activity.id) != "fishing" ||
            NormalizeOptionalToken(packet.activity.phase) != "engagement" ||
            packet.activity.fishing_completed)
        {
            return Fail(out reason, "fishing_cast_cycle_not_in_f3");
        }

        if (string.IsNullOrWhiteSpace(packet.companion.assigned_fishing_anchor_id) ||
            !packet.entry_checks.companion_at_valid_fishing_anchor)
        {
            return Fail(out reason, "fishing_cast_cycle_anchor_not_ready");
        }

        if (!packet.companion.fishing_rod_runtime_available ||
            NormalizeOptionalToken(packet.companion.companion_fishing_rod_state) != "visible_in_hand" ||
            !packet.entry_checks.fishing_rod_ready)
        {
            return Fail(out reason, "fishing_cast_cycle_rod_not_ready");
        }

        if (NormalizeOptionalToken(packet.companion.current_hand_pose) != "fishing_ready")
        {
            return Fail(out reason, "fishing_cast_cycle_ready_pose_not_ready");
        }

        return true;
    }

    private bool IsAllowedFishingF2TransitionPrimitive(CP_MotionPrimitive primitive, out string reason)
    {
        reason = string.Empty;
        if (primitive == null)
        {
            reason = "transition_primitive_null";
            return false;
        }

        string primitiveType = NormalizePrimitiveType(primitive.type);
        string scope = NormalizeScope(primitive.scope);
        string target = NormalizeTarget(primitive.target);
        string pose = NormalizeOptionalToken(primitive.pose);
        string prop = NormalizeOptionalToken(primitive.prop);
        string propState = NormalizeOptionalToken(primitive.state);

        switch (primitiveType)
        {
            case "move_within_scope":
                return scope == "transition_only" && target == "nearest_valid_fishing_interaction_area_point"
                    ? true
                    : Fail(out reason, "f2_fishing_transition_target_invalid");
            case "set_companion_prop":
                return prop == "fishing_rod" && (propState == "visible_in_hand" || propState == "hidden")
                    ? true
                    : Fail(out reason, "f2_fishing_transition_prop_invalid");
            case "set_hand_pose":
                return pose == "fishing_ready"
                    ? true
                    : Fail(out reason, "f2_fishing_transition_pose_invalid");
            default:
                return Fail(out reason, "transition_priority_requires_move_within_scope");
        }
    }

    private bool IsAllowedFishingF3TransitionPrimitive(CP_MotionPrimitive primitive, out string reason)
    {
        reason = string.Empty;
        if (primitive == null)
        {
            reason = "transition_primitive_null";
            return false;
        }

        string primitiveType = NormalizePrimitiveType(primitive.type);
        string scope = NormalizeScope(primitive.scope);
        string target = NormalizeTarget(primitive.target);
        string pose = NormalizeOptionalToken(primitive.pose);
        string prop = NormalizeOptionalToken(primitive.prop);
        string propState = NormalizeOptionalToken(primitive.state);

        switch (primitiveType)
        {
            case "move_within_scope":
                return scope == "transition_only" && target == "assigned_fishing_anchor"
                    ? true
                    : Fail(out reason, "f3_fishing_transition_target_invalid");
            case "face_toward":
            case "look_at":
                return target == "assigned_fishing_cast_target" ||
                       target == "water_focus" ||
                       target == "player_fishing_rod" ||
                       target == "player_bobber"
                    ? true
                    : Fail(out reason, "f3_fishing_transition_target_invalid");
            case "set_companion_prop":
                return prop == "fishing_rod" && (propState == "visible_in_hand" || propState == "hidden")
                    ? true
                    : Fail(out reason, "f3_fishing_transition_prop_invalid");
            case "set_hand_pose":
                return pose == "fishing_ready"
                    ? true
                    : Fail(out reason, "f3_fishing_transition_pose_invalid");
            default:
                return Fail(out reason, "transition_priority_requires_move_within_scope");
        }
    }

    private bool TryResolveAssignedFishingCastTarget(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "assigned_fishing_cast_target";
        warning = string.Empty;

        CP_FishingAnchorManager anchorManager = GetFishingAnchorManager();
        if (anchorManager == null)
        {
            warning = "assigned_fishing_cast_target_unresolved";
            return false;
        }

        Transform castTarget;
        if (!anchorManager.TryGetAssignedCastTargetTransform(out castTarget) || castTarget == null)
        {
            warning = "assigned_fishing_cast_target_unresolved";
            return false;
        }

        worldPosition = castTarget.position;
        return true;
    }

    private bool TryResolveWaterFocus(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "water_focus";
        warning = string.Empty;

        if (TryResolveAssignedFishingCastTarget(out worldPosition, out _, out _))
        {
            return true;
        }

        FishingStateBlackboard fishingBlackboard = GetFishingBlackboard();
        if (fishingBlackboard != null && fishingBlackboard.playerCastTarget != null)
        {
            worldPosition = fishingBlackboard.playerCastTarget.position;
            return true;
        }

        CP_FishingAnchorManager anchorManager = GetFishingAnchorManager();
        if (anchorManager != null && anchorManager.CastDirectionReference != null)
        {
            worldPosition = anchorManager.CastDirectionReference.position;
            return true;
        }

        warning = "water_focus_unresolved";
        return false;
    }

    private bool TryResolvePlayerFishingRod(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "player_fishing_rod";
        warning = string.Empty;

        FishingStateBlackboard fishingBlackboard = GetFishingBlackboard();
        if (fishingBlackboard == null || fishingBlackboard.fishingRodObject == null)
        {
            warning = "player_fishing_rod_unresolved";
            return false;
        }

        worldPosition = fishingBlackboard.fishingRodObject.transform.position;
        return true;
    }

    private bool TryResolvePlayerBobber(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "player_bobber";
        warning = string.Empty;

        FishingStateBlackboard fishingBlackboard = GetFishingBlackboard();
        if (fishingBlackboard == null || fishingBlackboard.currentBobberInstance == null)
        {
            warning = "player_bobber_unresolved";
            return false;
        }

        worldPosition = fishingBlackboard.currentBobberInstance.transform.position;
        return true;
    }

    private FishingStateBlackboard GetFishingBlackboard()
    {
        return perceptionBuilder != null && perceptionBuilder.activityContextRouter != null
            ? perceptionBuilder.activityContextRouter.FishingBlackboard
            : null;
    }

    private CP_FishingAnchorManager GetFishingAnchorManager()
    {
        return perceptionBuilder != null ? perceptionBuilder.fishingAnchorManager : null;
    }

    private bool TryResolveCampfireCenter(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "campfire_center";
        warning = string.Empty;

        RoastingStateBlackboard blackboard = GetBlackboard();
        if (blackboard == null)
        {
            warning = "CP_LegalityChecker: campfire_center could not be resolved because blackboard is missing.";
            return false;
        }

        if (blackboard.currentCampfireInstance != null)
        {
            Transform fireCenter = FindChildByName(blackboard.currentCampfireInstance.transform, "FireCenterPoint");
            if (fireCenter != null)
            {
                worldPosition = fireCenter.position;
                return true;
            }
        }

        if (blackboard.campfireSpawnPoint != null)
        {
            worldPosition = blackboard.campfireSpawnPoint.position;
            warning = "CP_LegalityChecker: campfire_center used campfireSpawnPoint fallback.";
            return true;
        }

        warning = "CP_LegalityChecker: campfire_center could not be resolved because both FireCenterPoint and campfireSpawnPoint are missing.";
        return false;
    }

    private bool TryResolvePlayerHead(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "player_head";
        warning = string.Empty;

        Transform playerHead = GetPlayerHead();
        if (playerHead == null)
        {
            warning = "CP_LegalityChecker: player_head could not be resolved because playerHead is missing.";
            return false;
        }

        worldPosition = playerHead.position;
        return true;
    }

    private bool TryResolveNeutralForward(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "neutral_forward";
        warning = string.Empty;

        Transform companionRoot = GetCompanionRoot();
        if (companionRoot == null)
        {
            warning = "CP_LegalityChecker: neutral_forward could not be resolved because companionRoot is missing.";
            return false;
        }

        worldPosition = companionRoot.position + companionRoot.forward;
        return true;
    }

    private bool TryResolveInteractionArea(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "interaction_area";
        warning = string.Empty;

        CP_InteractionArea interactionArea = GetInteractionArea();
        if (interactionArea == null)
        {
            warning = "CP_LegalityChecker: interaction_area could not be resolved because interactionArea is missing.";
            return false;
        }

        worldPosition = interactionArea.GetAreaCenterWorldPosition();
        return true;
    }

    private bool TryResolveCompanionMarshmallow(out Vector3 worldPosition, out string resolvedTargetName, out string warning)
    {
        worldPosition = Vector3.zero;
        resolvedTargetName = "companion_marshmallow";
        warning = string.Empty;

        CP_PropController controller = GetPropController();
        if (controller == null || !controller.IsRuntimeReady)
        {
            warning = "companion_marshmallow_unavailable";
            return false;
        }

        if (!controller.TryGetCompanionMarshmallowLookTarget(out worldPosition))
        {
            warning = "companion_marshmallow_target_unresolved";
            return false;
        }

        return true;
    }

    private CP_PolicyResponse ClonePolicyResponse(CP_PolicyResponse source)
    {
        CP_PolicyResponse clone = new CP_PolicyResponse();
        EnsurePolicySections(clone);

        clone.response_meta.schema_version = source.response_meta.schema_version;
        clone.response_meta.response_id = source.response_meta.response_id;
        clone.response_meta.request_id = source.response_meta.request_id;
        clone.control.execute_policy = source.control.execute_policy;
        clone.control.companion_should_run = source.control.companion_should_run;
        clone.control.shutdown_acknowledged = source.control.shutdown_acknowledged;
        clone.control.policy_error = source.control.policy_error;
        clone.state.current_state = source.state.current_state;
        clone.state.previous_state = source.state.previous_state;
        clone.state.state_changed = source.state.state_changed;
        clone.state.state_change_id = source.state.state_change_id;
        clone.state.state_reason = source.state.state_reason;
        clone.policy.policy_id = source.policy.policy_id;
        clone.policy.policy_source = source.policy.policy_source;
        clone.policy.activity_mode = source.policy.activity_mode;
        clone.policy.policy_lifetime = source.policy.policy_lifetime;
        clone.policy.intent_summary = source.policy.intent_summary;
        clone.allowed_pools.activity_modes = new List<string>(source.allowed_pools.activity_modes);
        clone.allowed_pools.motion_primitives = new List<string>(source.allowed_pools.motion_primitives);
        clone.allowed_pools.allowed_targets = new List<string>(source.allowed_pools.allowed_targets);
        clone.allowed_pools.forbidden_motion_primitives = new List<string>(source.allowed_pools.forbidden_motion_primitives);
        clone.allowed_pools.allowed_scopes = new List<string>(source.allowed_pools.allowed_scopes);
        clone.allowed_pools.allowed_poses = new List<string>(source.allowed_pools.allowed_poses);
        clone.allowed_pools.allowed_props = new List<string>(source.allowed_pools.allowed_props);
        clone.allowed_pools.allowed_gestures = new List<string>(source.allowed_pools.allowed_gestures);
        clone.motion_primitives = new List<CP_MotionPrimitive>();
        for (int i = 0; i < source.motion_primitives.Count; i++)
        {
            CP_MotionPrimitive primitive = source.motion_primitives[i];
            if (primitive != null)
            {
                clone.motion_primitives.Add(ClonePrimitive(primitive));
            }
        }

        clone.debug.provider_name = source.debug.provider_name;
        clone.debug.source_request_id = source.debug.source_request_id;
        clone.debug.source_state = source.debug.source_state;
        clone.debug.build_time_utc = source.debug.build_time_utc;
        clone.debug.error_code = source.debug.error_code;
        clone.debug.error_message = source.debug.error_message;
        clone.debug.warnings = new List<string>(source.debug.warnings);
        clone.queue = CloneQueueSection(source.queue);
        return clone;
    }

    private CP_PolicyQueueSection CloneQueueSection(CP_PolicyQueueSection source)
    {
        CP_PolicyQueueSection clone = new CP_PolicyQueueSection();
        if (source == null)
        {
            return clone;
        }

        clone.has_pending_transition = source.has_pending_transition;
        clone.queue_length = source.queue_length;
        clone.current_transition_id = source.current_transition_id;
        clone.current_task_status = source.current_task_status;
        clone.current_task_goal_type = source.current_task_goal_type;
        clone.current_task_goal_target = source.current_task_goal_target;
        clone.current_task_completed = source.current_task_completed;
        return clone;
    }

    private CP_MotionPrimitive ClonePrimitive(CP_MotionPrimitive primitive)
    {
        CP_MotionPrimitive copy = new CP_MotionPrimitive();
        copy.type = primitive.type;
        copy.target = primitive.target;
        copy.duration = primitive.duration;
        copy.style = primitive.style;
        copy.notes = primitive.notes;
        copy.scope = primitive.scope;
        copy.radius_min = primitive.radius_min;
        copy.radius_max = primitive.radius_max;
        copy.pose = primitive.pose;
        copy.prop = primitive.prop;
        copy.state = primitive.state;
        copy.gesture = primitive.gesture;
        return copy;
    }

    private CP_PolicyResponse CreatePolicyErrorResponse(string errorCode, string errorMessage)
    {
        CP_PolicyResponse response = new CP_PolicyResponse();
        EnsurePolicySections(response);
        response.response_meta.schema_version = "version_A_1.0.1";
        response.response_meta.response_id = $"policy_error_{Time.frameCount}";
        response.response_meta.request_id = string.Empty;
        response.control.execute_policy = false;
        response.control.companion_should_run = true;
        response.control.shutdown_acknowledged = false;
        response.control.policy_error = true;
        response.policy.policy_source = "policy_error";
        response.policy.policy_id = "policy_error";
        response.policy.activity_mode = string.Empty;
        response.policy.policy_lifetime = 0f;
        response.policy.intent_summary = errorMessage;
        response.motion_primitives.Clear();
        response.queue.has_pending_transition = false;
        response.queue.queue_length = 0;
        response.queue.current_transition_id = string.Empty;
        response.queue.current_task_status = string.Empty;
        response.queue.current_task_goal_type = string.Empty;
        response.queue.current_task_goal_target = string.Empty;
        response.queue.current_task_completed = true;
        response.debug.provider_name = nameof(CP_LegalityChecker);
        response.debug.error_code = errorCode;
        response.debug.error_message = errorMessage;
        response.debug.warnings.Add(errorMessage);
        return response;
    }

    private void EnsurePolicySections(CP_PolicyResponse response)
    {
        if (response == null)
        {
            return;
        }

        if (response.response_meta == null) response.response_meta = new CP_PolicyResponseMetaSection();
        if (response.control == null) response.control = new CP_PolicyControlSection();
        if (response.state == null) response.state = new CP_PolicyStateSection();
        if (response.policy == null) response.policy = new CP_PolicySection();
        if (response.allowed_pools == null) response.allowed_pools = new CP_AllowedPoolsSection();
        if (response.allowed_pools.activity_modes == null || response.allowed_pools.activity_modes.Count == 0)
        {
            response.allowed_pools.activity_modes = new List<string> { "idle_presence", "ambient_explore", "co_attention", "self_prepare", "parallel_engage", "responsive_feedback" };
        }
        if (response.allowed_pools.motion_primitives == null || response.allowed_pools.motion_primitives.Count == 0)
        {
            response.allowed_pools.motion_primitives = new List<string> { "look_at", "face_toward", "move_within_scope", "hold_position", "idle_motion", "set_hand_pose", "set_companion_prop", "play_gesture" };
        }
        if (response.allowed_pools.allowed_targets == null) response.allowed_pools.allowed_targets = new List<string>();
        if (response.allowed_pools.forbidden_motion_primitives == null) response.allowed_pools.forbidden_motion_primitives = new List<string>();
        if (response.allowed_pools.allowed_scopes == null) response.allowed_pools.allowed_scopes = new List<string>();
        if (response.allowed_pools.allowed_poses == null) response.allowed_pools.allowed_poses = new List<string>();
        if (response.allowed_pools.allowed_props == null) response.allowed_pools.allowed_props = new List<string>();
        if (response.allowed_pools.allowed_gestures == null) response.allowed_pools.allowed_gestures = new List<string>();
        if (response.motion_primitives == null) response.motion_primitives = new List<CP_MotionPrimitive>();
        if (response.queue == null) response.queue = new CP_PolicyQueueSection();
        if (response.debug == null) response.debug = new CP_PolicyDebugSection();
        if (response.debug.warnings == null) response.debug.warnings = new List<string>();
    }

    private string NormalizePrimitiveType(string primitiveType)
    {
        return string.IsNullOrWhiteSpace(primitiveType) ? string.Empty : primitiveType.Trim().ToLowerInvariant();
    }

    private string NormalizePolicySource(string policySource)
    {
        return string.IsNullOrWhiteSpace(policySource) ? string.Empty : policySource.Trim().ToLowerInvariant();
    }

    private string NormalizeStateName(string stateName)
    {
        return string.IsNullOrWhiteSpace(stateName) ? string.Empty : stateName.Trim();
    }

    private string NormalizeOptionalToken(string token)
    {
        return string.IsNullOrWhiteSpace(token) ? string.Empty : token.Trim().ToLowerInvariant();
    }

    private string NormalizeScope(string token)
    {
        return NormalizeOptionalToken(token);
    }

    private string NormalizeTarget(string token)
    {
        return NormalizeOptionalToken(token);
    }

    private bool Fail(out string reason, string value)
    {
        reason = value;
        return false;
    }

    private string BuildSummary(CP_PolicyResponse response, int sourcePrimitiveCount, int acceptedPrimitiveCount, int warningCount)
    {
        summaryBuilder.Clear();
        summaryBuilder.AppendLine("Policy ID: " + (response != null && response.policy != null ? response.policy.policy_id : string.Empty));
        summaryBuilder.AppendLine("Policy Source: " + (response != null && response.policy != null ? response.policy.policy_source : string.Empty));
        summaryBuilder.AppendLine("Activity Mode: " + (response != null && response.policy != null ? response.policy.activity_mode : string.Empty));
        summaryBuilder.AppendLine("State: " + (response != null && response.state != null ? response.state.current_state : string.Empty));
        summaryBuilder.AppendLine("Source Primitives: " + sourcePrimitiveCount);
        summaryBuilder.AppendLine("Accepted Primitives: " + acceptedPrimitiveCount);
        summaryBuilder.AppendLine("Sanitized Primitive Count: " + (response != null && response.motion_primitives != null ? response.motion_primitives.Count : 0));
        summaryBuilder.AppendLine("Warnings: " + warningCount);
        if (response != null && response.debug != null && (!string.IsNullOrEmpty(response.debug.error_code) || !string.IsNullOrEmpty(response.debug.error_message)))
        {
            summaryBuilder.AppendLine("Error Code: " + response.debug.error_code);
            summaryBuilder.AppendLine("Error Message: " + response.debug.error_message);
        }
        return summaryBuilder.ToString();
    }

    private string BuildWarningsText(List<string> warnings)
    {
        warningBuilder.Clear();
        if (warnings == null || warnings.Count == 0)
        {
            return string.Empty;
        }

        for (int i = 0; i < warnings.Count; i++)
        {
            warningBuilder.AppendLine(warnings[i]);
        }

        string warningsText = warningBuilder.ToString();
        if (logLegalityWarnings && warningsText != lastLoggedWarningsText)
        {
            for (int i = 0; i < warnings.Count; i++)
            {
                Debug.LogWarning(warnings[i], this);
            }

            lastLoggedWarningsText = warningsText;
        }

        return warningsText;
    }

    private string BuildPrimitiveDebugLabel(CP_MotionPrimitive primitive)
    {
        if (primitive == null)
        {
            return "null";
        }

        string primitiveType = NormalizePrimitiveType(primitive.type);
        string target = string.IsNullOrWhiteSpace(primitive.target) ? "none" : NormalizeTarget(primitive.target);
        return $"type={primitiveType}, duration={primitive.duration:F2}, target={target}";
    }

    private RoastingStateBlackboard GetBlackboard()
    {
        return perceptionBuilder != null ? perceptionBuilder.blackboard : null;
    }

    private Transform GetPlayerHead()
    {
        return perceptionBuilder != null ? perceptionBuilder.playerHead : null;
    }

    private Transform GetCompanionRoot()
    {
        return perceptionBuilder != null ? perceptionBuilder.companionRoot : null;
    }

    private CP_InteractionArea GetInteractionArea()
    {
        return perceptionBuilder != null ? perceptionBuilder.interactionArea : null;
    }

    private CP_PropController GetPropController()
    {
        if (propController != null)
        {
            return propController;
        }

        return perceptionBuilder != null ? perceptionBuilder.propController : null;
    }

    private bool IsCompanionStickCurrentlyVisible()
    {
        CP_PropController controller = GetPropController();
        return controller != null && controller.HasCompanionStick;
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
}
