"""Basic AI policy validation for Stage 4A-2."""

from __future__ import annotations

import json
import math
from copy import deepcopy

from .models import (
    ACTIVITY_AMBIENT_EXPLORE,
    ACTIVITY_CO_ATTENTION,
    ACTIVITY_IDLE_PRESENCE,
    ACTIVITY_PARALLEL_ENGAGE,
    ACTIVITY_RESPONSIVE_FEEDBACK,
    ACTIVITY_SELF_PREPARE,
    GESTURE_SMALL_NOD,
    GESTURE_SMALL_SHIFT,
    GESTURE_SUBTLE_REACTION,
    POSE_NEUTRAL,
    POSE_READY,
    POSE_RELAXED,
    POSE_ROAST_POSE,
    POLICY_SOURCE_AI,
    POLICY_SOURCE_FALLBACK,
    POLICY_SOURCE_POLICY_ERROR,
    POLICY_SOURCE_SHUTDOWN,
    POLICY_SOURCE_TRANSITION_PRIORITY,
    PROP_MARSHMALLOW,
    PROP_STICK,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_IDLE_MOTION,
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
    PRIMITIVE_PLAY_GESTURE,
    PRIMITIVE_SET_COMPANION_PROP,
    PRIMITIVE_SET_HAND_POSE,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    SCOPE_NAMED_POINT,
    SCOPE_TRANSITION_ONLY,
    STATE_F2,
    STATE_F3,
    STATE_S1,
    STATE_S3,
    TARGET_CAMPFIRE_CENTER,
    TARGET_CAMPFIRE_HINT,
    TARGET_COMPANION_MARSHMALLOW,
    TARGET_COMPANION_STICK,
    TARGET_INTERACTION_AREA,
    TARGET_NEAREST_FREE_ROASTING_ANCHOR,
    TARGET_NEAREST_VALID_INTERACTION_AREA_POINT,
    TARGET_NEUTRAL_FORWARD,
    TARGET_PLAYER_HEAD,
    TARGET_PLAYER_HAND_ITEM,
    TARGET_PLAYER_MARSHMALLOW,
)
from .state_config import get_allowed_activity_modes, get_allowed_motion_primitives, get_state_prompt_definitions


ALLOWED_POLICY_FIELDS = {"activity_mode", "policy_lifetime", "intent_summary", "motion_primitives"}
ALLOWED_PRIMITIVE_FIELDS = {
    "type",
    "target",
    "scope",
    "duration",
    "style",
    "pose",
    "prop",
    "state",
    "gesture",
    "radius_min",
    "radius_max",
    "notes",
}
FORBIDDEN_TOP_LEVEL_FIELDS = {
    "policy_source",
    "execute_policy",
    "response_meta",
    "control",
    "debug",
    "state",
    "allowed_pools",
    "queue",
    "transition_queue",
    "scheduler",
    "scheduler_state",
    "request_role",
    "source_state_epoch",
    "state_epoch",
    "policy_index",
    "speech",
    "dialogue",
    "say",
    "text",
    "message",
    "message_to_player",
    "blackboard_update",
    "set_blackboard",
    "player_action",
    "control_player",
    "coordinates",
    "position",
    "rotation",
    "bone",
    "bones",
    "animation_curve",
    "frame_animation",
}

FORBIDDEN_TEXT_PHRASES = (
    "say",
    "speak",
    "talk",
    "dialog",
    "dialogue",
    "modify blackboard",
    "control player",
    "move player",
    "grab stick",
    "grab marshmallow",
    "pick up stick",
    "pick up marshmallow",
    "output text to player",
    "text to player",
)

KNOWN_EXECUTABLE_SOURCES = {
    POLICY_SOURCE_AI,
    POLICY_SOURCE_FALLBACK,
    POLICY_SOURCE_TRANSITION_PRIORITY,
}


def _make_error(code, message, details=None):
    error = {"code": code, "message": message}
    if details is not None:
        error["details"] = details
    return error


def _extract_primitive_types(primitives):
    if not isinstance(primitives, list):
        return []
    return [
        str(primitive.get("type", "")).strip()
        for primitive in primitives
        if isinstance(primitive, dict) and str(primitive.get("type", "")).strip()
    ]


def _compact_motion_primitives(primitives, max_primitives=4):
    if not isinstance(primitives, list):
        return []

    compact = []
    for primitive in primitives[:max_primitives]:
        if not isinstance(primitive, dict):
            continue
        entry = {}
        for key in (
            "type",
            "scope",
            "target",
            "duration",
            "radius_min",
            "radius_max",
            "pose",
            "prop",
            "state",
            "gesture",
        ):
            if key in primitive:
                entry[key] = primitive.get(key)
        compact.append(entry)
    return compact


def _strip_code_fences(raw_text):
    text = raw_text.strip()
    if text.startswith("```"):
        lines = text.splitlines()
        if len(lines) >= 2 and lines[0].startswith("```"):
            if lines[-1].strip().startswith("```"):
                lines = lines[1:-1]
            else:
                lines = lines[1:]
            text = "\n".join(lines).strip()
    return text


def _contains_forbidden_text(value):
    if not isinstance(value, str):
        return False
    lowered = value.lower()
    return any(phrase in lowered for phrase in FORBIDDEN_TEXT_PHRASES)


def _coerce_positive_float(value):
    try:
        numeric_value = float(value)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(numeric_value) or numeric_value <= 0.0:
        return None
    return numeric_value


def _coerce_optional_float(value):
    if value is None or value == "":
        return None
    try:
        numeric_value = float(value)
    except (TypeError, ValueError):
        return None
    if not math.isfinite(numeric_value):
        return None
    return numeric_value


def parse_ai_json(raw_text):
    """Parse a JSON string into a Python object."""

    if isinstance(raw_text, dict):
        return deepcopy(raw_text)

    if not isinstance(raw_text, str):
        raise ValueError("AI output must be a JSON string or dict.")

    text = _strip_code_fences(raw_text)
    if not text:
        raise ValueError("AI output JSON is empty.")

    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:
        raise ValueError(f"AI output is not valid JSON: {exc}") from exc


def _normalize_allowed_pools(current_state, allowed_pools):
    state_prompt = get_state_prompt_definitions(current_state)
    allowed_activity_modes = list((allowed_pools or {}).get("activity_modes") or get_allowed_activity_modes(current_state))
    allowed_motion_primitives = list((allowed_pools or {}).get("motion_primitives") or get_allowed_motion_primitives(current_state))
    allowed_targets = list((allowed_pools or {}).get("allowed_targets") or state_prompt.get("allowed_targets") or [])
    forbidden_motion_primitives = list((allowed_pools or {}).get("forbidden_motion_primitives") or state_prompt.get("forbidden_motion_primitives") or [])
    allowed_scopes = list((allowed_pools or {}).get("allowed_scopes") or state_prompt.get("allowed_scopes") or [])
    allowed_poses = list((allowed_pools or {}).get("allowed_poses") or state_prompt.get("allowed_poses") or [])
    allowed_props = list((allowed_pools or {}).get("allowed_props") or state_prompt.get("allowed_props") or [])
    allowed_prop_states = deepcopy((allowed_pools or {}).get("allowed_prop_states") or state_prompt.get("allowed_prop_states") or {})
    allowed_gestures = list((allowed_pools or {}).get("allowed_gestures") or state_prompt.get("allowed_gestures") or [])
    return (
        state_prompt,
        allowed_activity_modes,
        allowed_motion_primitives,
        allowed_targets,
        forbidden_motion_primitives,
        allowed_scopes,
        allowed_poses,
        allowed_props,
        allowed_prop_states,
        allowed_gestures,
    )


def _validate_top_level_fields(policy_dict):
    errors = []
    for key in policy_dict.keys():
        if key in FORBIDDEN_TOP_LEVEL_FIELDS:
            errors.append(_make_error("FORBIDDEN_TOP_LEVEL_FIELD", f"AI policy must not include {key}."))
            continue
        if key not in ALLOWED_POLICY_FIELDS:
            errors.append(_make_error("FORBIDDEN_TOP_LEVEL_FIELD", f"AI policy must not include unexpected field {key}."))
    return errors


def _validate_sensitive_strings(policy_dict):
    errors = []
    for key in ("intent_summary", "notes", "description", "rationale", "message"):
        if key in policy_dict and _contains_forbidden_text(policy_dict.get(key)):
            errors.append(_make_error("FORBIDDEN_INTENT_TEXT", f"Field '{key}' contains forbidden control or speech language."))
    return errors


def _validate_target(target, current_state, allowed_targets, primitive_type):
    if not target:
        return [_make_error("INVALID_TARGET", f"Primitive '{primitive_type}' requires a target.")]

    if target in allowed_targets:
        return []

    return [_make_error("INVALID_TARGET", f"Primitive '{primitive_type}' target '{target}' is not allowed in {current_state}.")]


def _validate_move_within_scope(primitive, current_state, allowed_targets, allowed_scopes):
    errors = []
    scope = str(primitive.get("scope", "")).strip()
    target = str(primitive.get("target", "")).strip()
    radius_min = _coerce_optional_float(primitive.get("radius_min"))
    radius_max = _coerce_optional_float(primitive.get("radius_max"))

    if not scope:
        errors.append(_make_error("INVALID_SCOPE", "move_within_scope must include a scope."))
    elif allowed_scopes and scope not in allowed_scopes:
        errors.append(_make_error("INVALID_SCOPE", f"Scope '{scope}' is not allowed in {current_state}."))

    if scope == SCOPE_TRANSITION_ONLY:
        errors.append(_make_error("INVALID_SCOPE", "transition_only is reserved for transition_priority responses and is not available to normal AI/fallback policy generation."))
    elif scope == SCOPE_INTERACTION_AREA:
        if target and target != TARGET_INTERACTION_AREA:
            errors.append(_make_error("INVALID_TARGET", f"Scope '{scope}' only allows target '{TARGET_INTERACTION_AREA}' in {current_state}."))
    elif scope == SCOPE_AROUND_PLAYER:
        if current_state != STATE_S1:
            errors.append(_make_error("INVALID_SCOPE", f"Scope '{scope}' is only available for S1 normal movement."))
        if target:
            errors.append(_make_error("INVALID_AROUND_PLAYER_TARGET", "around_player movement must use target as an empty string; Unity resolves the player-centered destination."))
        if radius_min is None:
            errors.append(_make_error("INVALID_RADIUS", "around_player movement must include finite numeric radius_min."))
        if radius_max is None:
            errors.append(_make_error("INVALID_RADIUS", "around_player movement must include finite numeric radius_max."))
    elif scope == SCOPE_NAMED_POINT:
        if not target:
            errors.append(_make_error("INVALID_TARGET", "named_point movement must include a registered semantic point id."))
        elif target in {TARGET_NEAREST_VALID_INTERACTION_AREA_POINT, TARGET_NEAREST_FREE_ROASTING_ANCHOR}:
            errors.append(_make_error("INVALID_TARGET", f"Target '{target}' is transition-only and cannot be used by normal AI/fallback movement."))
    elif target:
        errors.extend(_validate_target(target, current_state, allowed_targets, "move_within_scope"))

    if radius_min is not None and radius_min < 0.0:
        errors.append(_make_error("INVALID_RADIUS", "radius_min must be non-negative when provided."))
    if radius_max is not None and radius_max <= 0.0:
        errors.append(_make_error("INVALID_RADIUS", "radius_max must be positive when provided."))
    if radius_min is not None and radius_max is not None and radius_min > radius_max:
        errors.append(_make_error("INVALID_RADIUS", "radius_min must not be greater than radius_max."))

    return errors


def _validate_hand_pose(primitive, current_state, allowed_poses):
    pose = str(primitive.get("pose", "")).strip()
    if not pose:
        return [_make_error("INVALID_POSE", "set_hand_pose must include a pose.")]

    if allowed_poses and pose not in allowed_poses:
        return [_make_error("INVALID_POSE", f"Pose '{pose}' is not allowed in {current_state}.")]

    return []


def _validate_companion_prop(primitive, current_state, allowed_props, allowed_prop_states, primitive_index=None):
    prop = str(primitive.get("prop", "")).strip()
    state_value = str(primitive.get("state", "")).strip()
    failure_details = {
        "primitive_index": primitive_index,
        "primitive_type": "set_companion_prop",
        "current_state": current_state,
        "prop": prop,
        "state": state_value,
        "allowed_props": list(allowed_props or []),
        "allowed_prop_states": deepcopy(allowed_prop_states or {}),
    }

    if not prop:
        return [_make_error("INVALID_PROP", "set_companion_prop must include a prop.")], None

    if prop.startswith("player_"):
        return [_make_error("INVALID_PROP", f"Prop '{prop}' must be companion-owned, not player-owned.")], None

    if prop not in allowed_props:
        return [_make_error("INVALID_PROP", f"Prop '{prop}' is not allowed in {current_state}.")], None

    valid_states = list((allowed_prop_states or {}).get(prop) or [])
    failure_details["allowed_states_for_prop"] = list(valid_states)

    if not valid_states:
        return [
            _make_error(
                "INVALID_PROP_STATE",
                f"No allowed prop states are configured for '{prop}' in {current_state}.",
                failure_details,
            )
        ], failure_details

    if not state_value:
        return [
            _make_error(
                "INVALID_PROP_STATE",
                f"Prop '{prop}' in {current_state} must include an explicit state from: {', '.join(valid_states)}.",
                failure_details,
            )
        ], failure_details

    if state_value not in valid_states:
        return [
            _make_error(
                "INVALID_PROP_STATE",
                f"Prop state '{state_value}' is not allowed for '{prop}' in {current_state}. Allowed states: {', '.join(valid_states)}.",
                failure_details,
            )
        ], failure_details

    return [], None


def _validate_gesture(primitive, current_state, allowed_gestures):
    gesture = str(primitive.get("gesture", "")).strip()
    if not gesture:
        return [_make_error("INVALID_GESTURE", "play_gesture must include a gesture.")]

    if allowed_gestures and gesture not in allowed_gestures:
        return [_make_error("INVALID_GESTURE", f"Gesture '{gesture}' is not allowed in {current_state}.")]

    return []


def _validate_motion_primitive(
    primitive,
    current_state,
    allowed_motion_primitives,
    allowed_targets,
    forbidden_motion_primitives,
    allowed_scopes,
    allowed_poses,
    allowed_props,
    allowed_prop_states,
    allowed_gestures,
    primitive_index=None,
):
    if not isinstance(primitive, dict):
        return None, [_make_error("POLICY_PRIMITIVE_INVALID", "Each motion primitive must be an object.")]

    field_errors = []
    for key in primitive.keys():
        if key not in ALLOWED_PRIMITIVE_FIELDS:
            field_errors.append(_make_error("FORBIDDEN_PRIMITIVE_FIELD", f"Primitive must not include unsupported field '{key}'."))
    if field_errors:
        return None, field_errors

    primitive_type = str(primitive.get("type", "")).strip()
    target = str(primitive.get("target", "")).strip()
    style = str(primitive.get("style", "")).strip() if primitive.get("style") is not None else ""
    notes = str(primitive.get("notes", "")).strip() if primitive.get("notes") is not None else ""
    scope = str(primitive.get("scope", "")).strip() if primitive.get("scope") is not None else ""
    pose = str(primitive.get("pose", "")).strip() if primitive.get("pose") is not None else ""
    prop = str(primitive.get("prop", "")).strip() if primitive.get("prop") is not None else ""
    state_value = str(primitive.get("state", "")).strip() if primitive.get("state") is not None else ""
    gesture = str(primitive.get("gesture", "")).strip() if primitive.get("gesture") is not None else ""
    duration = primitive.get("duration")

    if not primitive_type:
        return None, [_make_error("POLICY_PRIMITIVE_TYPE_INVALID", "Each motion primitive must include a type.")]

    if _contains_forbidden_text(primitive_type) or _contains_forbidden_text(style) or _contains_forbidden_text(notes):
        return None, [_make_error("FORBIDDEN_INTENT_TEXT", f"Primitive '{primitive_type}' contains forbidden control or speech language.")]

    for value in (target, scope, pose, prop, state_value, gesture):
        if value and _contains_forbidden_text(value):
            return None, [_make_error("FORBIDDEN_INTENT_TEXT", f"Primitive '{primitive_type}' contains forbidden control or speech language.")]

    if primitive_type in forbidden_motion_primitives:
        return None, [_make_error("FORBIDDEN_PRIMITIVE_TYPE", f"Primitive '{primitive_type}' is forbidden in {current_state}.")]

    if current_state == STATE_S3 and primitive_type == PRIMITIVE_MOVE_WITHIN_SCOPE:
        return None, [_make_error("S3_MOVE_FORBIDDEN", "S3 normal policy cannot contain move_within_scope.")]
    if current_state == STATE_F3 and primitive_type == PRIMITIVE_MOVE_WITHIN_SCOPE:
        return None, [_make_error("F3_MOVE_FORBIDDEN", "F3 normal policy cannot contain move_within_scope.")]

    if primitive_type not in allowed_motion_primitives:
        return None, [_make_error("POLICY_PRIMITIVE_TYPE_INVALID", f"Primitive '{primitive_type}' is not allowed in {current_state}.")]

    primitive_errors = []
    if primitive_type in {PRIMITIVE_LOOK_AT, PRIMITIVE_FACE_TOWARD}:
        primitive_errors.extend(_validate_target(target, current_state, allowed_targets, primitive_type))
    elif primitive_type == PRIMITIVE_MOVE_WITHIN_SCOPE:
        primitive_errors.extend(_validate_move_within_scope(primitive, current_state, allowed_targets, allowed_scopes))
    elif primitive_type in {PRIMITIVE_HOLD_POSITION, PRIMITIVE_IDLE_MOTION}:
        if target:
            primitive_errors.extend(_validate_target(target, current_state, allowed_targets, primitive_type))
    elif primitive_type == PRIMITIVE_SET_HAND_POSE:
        primitive_errors.extend(_validate_hand_pose(primitive, current_state, allowed_poses))
    elif primitive_type == PRIMITIVE_SET_COMPANION_PROP:
        prop_errors, _ = _validate_companion_prop(
            primitive,
            current_state,
            allowed_props,
            allowed_prop_states,
            primitive_index=primitive_index,
        )
        primitive_errors.extend(prop_errors)
    elif primitive_type == PRIMITIVE_PERFORM_FISHING_CAST_CYCLE:
        if current_state != STATE_F3:
            primitive_errors.append(_make_error("FISHING_CAST_CYCLE_STATE_INVALID", "perform_fishing_cast_cycle is only allowed in F3 normal policy."))
        if target:
            primitive_errors.append(_make_error("FISHING_CAST_CYCLE_TARGET_FORBIDDEN", "perform_fishing_cast_cycle must not include a target."))
        if scope:
            primitive_errors.append(_make_error("FISHING_CAST_CYCLE_SCOPE_FORBIDDEN", "perform_fishing_cast_cycle must not include a scope."))
    elif primitive_type == PRIMITIVE_PLAY_GESTURE:
        primitive_errors.extend(_validate_gesture(primitive, current_state, allowed_gestures))

    duration_value = _coerce_positive_float(duration)
    if duration_value is None:
        primitive_errors.append(_make_error("POLICY_DURATION_INVALID", f"Primitive '{primitive_type}' must have a positive finite duration."))

    if primitive_errors:
        return None, primitive_errors

    normalized = {
        "type": primitive_type,
        "target": target,
        "duration": duration_value,
    }
    if style:
        normalized["style"] = style
    if notes:
        normalized["notes"] = notes
    if scope:
        normalized["scope"] = scope
        if primitive_type == PRIMITIVE_MOVE_WITHIN_SCOPE and scope == SCOPE_AROUND_PLAYER:
            normalized["target"] = ""
    if pose:
        normalized["pose"] = pose
    if prop:
        normalized["prop"] = prop
    if state_value:
        normalized["state"] = state_value
    if gesture:
        normalized["gesture"] = gesture

    radius_min = _coerce_optional_float(primitive.get("radius_min"))
    radius_max = _coerce_optional_float(primitive.get("radius_max"))
    if radius_min is not None:
        normalized["radius_min"] = radius_min
    if radius_max is not None:
        normalized["radius_max"] = radius_max

    return normalized, []


def validate_policy_dict(policy_dict, current_state, allowed_pools):
    """Validate a parsed AI policy object."""

    (
        state_prompt,
        allowed_activity_modes,
        allowed_motion_primitives,
        allowed_targets,
        forbidden_motion_primitives,
        allowed_scopes,
        allowed_poses,
        allowed_props,
        allowed_prop_states,
        allowed_gestures,
    ) = _normalize_allowed_pools(current_state, allowed_pools)
    warnings = []
    errors = []

    if not isinstance(policy_dict, dict):
        return {
            "valid": False,
            "policy": None,
            "warnings": warnings,
            "errors": [_make_error("POLICY_OBJECT_INVALID", "AI output must be a JSON object.")],
            "debug": {
                "current_state": current_state,
                "allowed_activity_modes": allowed_activity_modes,
                "allowed_motion_primitives": allowed_motion_primitives,
                "allowed_targets": allowed_targets,
                "forbidden_motion_primitives": forbidden_motion_primitives,
                "allowed_scopes": allowed_scopes,
                "allowed_poses": allowed_poses,
                "allowed_props": allowed_props,
                "allowed_prop_states": allowed_prop_states,
                "allowed_gestures": allowed_gestures,
                "state_description": state_prompt.get("description", ""),
            },
        }

    errors.extend(_validate_top_level_fields(policy_dict))
    errors.extend(_validate_sensitive_strings(policy_dict))

    activity_mode = str(policy_dict.get("activity_mode", "")).strip()
    if not activity_mode:
        errors.append(_make_error("POLICY_ACTIVITY_MODE_INVALID", "Policy must include activity_mode."))
    elif activity_mode not in allowed_activity_modes:
        errors.append(_make_error("POLICY_ACTIVITY_MODE_INVALID", f"activity_mode '{activity_mode}' is not allowed in {current_state}."))

    policy_lifetime = _coerce_positive_float(policy_dict.get("policy_lifetime"))
    if policy_lifetime is None or policy_lifetime > 30.0:
        errors.append(_make_error("POLICY_LIFETIME_INVALID", "policy_lifetime must be positive and reasonable."))

    motion_primitives = policy_dict.get("motion_primitives")
    if not isinstance(motion_primitives, list) or not motion_primitives:
        errors.append(_make_error("POLICY_MOTION_PRIMITIVES_INVALID", "Policy must include a non-empty motion_primitives list."))
        motion_primitives = []

    raw_motion_primitive_types = _extract_primitive_types(motion_primitives)
    if raw_motion_primitive_types.count(PRIMITIVE_PERFORM_FISHING_CAST_CYCLE) > 1:
        errors.append(_make_error("FISHING_CAST_CYCLE_REPEAT_FORBIDDEN", "A normal policy must not contain more than one perform_fishing_cast_cycle primitive."))
    raw_motion_primitives = _compact_motion_primitives(motion_primitives)
    normalized_primitives = []
    total_duration = 0.0
    prop_state_failure = None
    for primitive_index, primitive in enumerate(motion_primitives):
        normalized_primitive, primitive_errors = _validate_motion_primitive(
            primitive,
            current_state,
            allowed_motion_primitives,
            allowed_targets,
            forbidden_motion_primitives,
            allowed_scopes,
            allowed_poses,
            allowed_props,
            allowed_prop_states,
            allowed_gestures,
            primitive_index=primitive_index,
        )
        errors.extend(primitive_errors)
        if prop_state_failure is None:
            for error in primitive_errors:
                if error.get("code") == "INVALID_PROP_STATE" and error.get("details"):
                    prop_state_failure = deepcopy(error.get("details"))
                    break
        if normalized_primitive is None:
            continue
        normalized_primitives.append(normalized_primitive)
        total_duration += float(normalized_primitive["duration"])

    if policy_lifetime is not None and total_duration > policy_lifetime + 1e-6:
        duration_overflow = total_duration - policy_lifetime
        errors.append(_make_error("POLICY_DURATION_TOO_LONG", "Sum of motion primitive durations must not exceed policy_lifetime."))

    clean_policy = {
        "activity_mode": activity_mode,
        "policy_lifetime": policy_lifetime if policy_lifetime is not None else 0.0,
        "intent_summary": str(policy_dict.get("intent_summary", "")),
        "motion_primitives": normalized_primitives,
    }

    debug = {
        "current_state": current_state,
        "allowed_activity_modes": allowed_activity_modes,
        "allowed_motion_primitives": allowed_motion_primitives,
        "allowed_targets": allowed_targets,
        "forbidden_motion_primitives": forbidden_motion_primitives,
        "allowed_scopes": allowed_scopes,
        "allowed_poses": allowed_poses,
        "allowed_props": allowed_props,
        "allowed_prop_states": allowed_prop_states,
        "allowed_gestures": allowed_gestures,
        "state_description": state_prompt.get("description", ""),
        "raw_ai_motion_primitive_types": raw_motion_primitive_types,
        "raw_ai_motion_primitives": raw_motion_primitives,
        "validated_motion_primitive_types": _extract_primitive_types(normalized_primitives),
        "validated_motion_primitives": _compact_motion_primitives(normalized_primitives),
        "validator_error_codes": [error.get("code", "") for error in errors if isinstance(error, dict)],
        "validator_warning_codes": [],
    }

    if prop_state_failure is not None:
        debug["prop_state_failure"] = prop_state_failure

    if policy_lifetime is not None and total_duration > policy_lifetime + 1e-6:
        debug["policy_lifetime"] = policy_lifetime
        debug["total_primitive_duration"] = total_duration
        debug["duration_overflow"] = duration_overflow

    if errors:
        return {
            "valid": False,
            "policy": None,
            "warnings": warnings,
            "errors": errors,
            "debug": debug,
        }

    return {
        "valid": True,
        "policy": clean_policy,
        "warnings": warnings,
        "errors": [],
        "debug": debug,
    }


def validate_ai_policy(raw_text, current_state, allowed_pools):
    """Parse and validate a raw AI response."""

    try:
        parsed_policy = parse_ai_json(raw_text)
    except ValueError as exc:
        (
            state_prompt,
            allowed_activity_modes,
            allowed_motion_primitives,
            allowed_targets,
            forbidden_motion_primitives,
            allowed_scopes,
            allowed_poses,
            allowed_props,
            allowed_prop_states,
            allowed_gestures,
        ) = _normalize_allowed_pools(current_state, allowed_pools)
        return {
            "valid": False,
            "policy": None,
            "warnings": [],
            "errors": [_make_error("POLICY_JSON_INVALID", str(exc))],
            "debug": {
                "current_state": current_state,
                "allowed_activity_modes": allowed_activity_modes,
                "allowed_motion_primitives": allowed_motion_primitives,
                "allowed_targets": allowed_targets,
                "forbidden_motion_primitives": forbidden_motion_primitives,
                "allowed_scopes": allowed_scopes,
                "allowed_poses": allowed_poses,
                "allowed_props": allowed_props,
                "allowed_prop_states": allowed_prop_states,
                "allowed_gestures": allowed_gestures,
                "state_description": state_prompt.get("description", ""),
            },
        }

    return validate_policy_dict(parsed_policy, current_state, allowed_pools)
