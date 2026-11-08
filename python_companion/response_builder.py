"""Helpers for building formal Policy JSON responses."""

from copy import deepcopy
from datetime import datetime, timezone
from uuid import uuid4

from .config import (
    DEFAULT_POLICY_LIFETIME,
    FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS,
    SCHEMA_VERSION,
    SERVER_NAME,
    SERVER_VERSION,
    TRANSITION_POLICY_LIFETIME,
)
from .models import (
    ACTIVITY_CO_ATTENTION,
    ACTIVITY_IDLE_PRESENCE,
    ACTIVITY_PARALLEL_ENGAGE,
    ERROR_EMPTY_REQUEST,
    ERROR_MALFORMED_PERCEPTION,
    ERROR_MISSING_REQUIRED_FIELDS,
    ERROR_SERVER_UNAVAILABLE,
    ERROR_SHUTDOWN,
    ERROR_UNKNOWN_STATE,
    POLICY_SOURCE_FALLBACK,
    POLICY_SOURCE_POLICY_ERROR,
    POLICY_SOURCE_SHUTDOWN,
    POLICY_SOURCE_AI,
    POLICY_SOURCE_TRANSITION_PRIORITY,
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
    PRIMITIVE_SET_COMPANION_PROP,
    PRIMITIVE_SET_HAND_POSE,
    PROP_STATE_VISIBLE_IN_HAND,
    PROP_STATE_VISIBLE_ON_STICK,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    SCOPE_TRANSITION_ONLY,
    TARGET_CAMPFIRE_CENTER,
    TARGET_COMPANION_MARSHMALLOW,
    TARGET_NEUTRAL_FORWARD,
    TARGET_PLAYER_HEAD,
    TARGET_PLAYER_BOBBER,
    TARGET_PLAYER_FISHING_ROD,
    STATE_F2,
    STATE_F3,
    TARGET_ASSIGNED_FISHING_ANCHOR,
    TARGET_ASSIGNED_FISHING_CAST_TARGET,
    TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT,
    PROP_FISHING_ROD,
    POSE_FISHING_READY,
    POSE_READY,
    POSE_ROAST_POSE,
    TARGET_WATER_FOCUS,
)
from .state_config import S1_AROUND_PLAYER_RADIUS_MAX, S1_AROUND_PLAYER_RADIUS_MIN, build_allowed_pools, get_state_config


def _utc_now_iso():
    return datetime.now(timezone.utc).isoformat()


def _empty_queue():
    return {
        "has_pending_transition": False,
        "queue_length": 0,
        "current_transition_id": "",
        "current_task_status": "",
        "current_task_goal_type": "",
        "current_task_goal_target": "",
        "current_task_completed": True,
    }


def _empty_ai_debug():
    return {
        "ai_enabled": False,
        "fallback_enabled": True,
        "force_fallback_for_test": False,
        "ai_status": "",
        "ai_error_status": "",
        "ai_error_message": "",
        "provider": "",
        "model": "",
        "latency_ms": 0.0,
        "validator_error_codes": [],
        "validator_warnings": [],
        "validator_debug": {},
        "fallback_reason": "",
        "request_role": "",
        "source_state_epoch": -1,
        "source_policy_index": -1,
        "ai_job_status": "completed",
        "pending_policy_status": "not_applicable",
        "state_change_invalidated_policy": False,
        "fallback_status": "not_used",
    }


def _normalize_string_list(values):
    if not isinstance(values, list):
        return []
    return [str(value) for value in values if str(value)]


def _normalize_ai_debug(ai_debug=None):
    normalized = _empty_ai_debug()
    if not isinstance(ai_debug, dict):
        return normalized

    for key in normalized.keys():
        if key not in ai_debug:
            continue
        value = ai_debug.get(key)
        if key in {"validator_error_codes", "validator_warnings"}:
            normalized[key] = _normalize_string_list(value)
        elif key == "validator_debug":
            normalized[key] = deepcopy(value) if isinstance(value, dict) else {}
        elif key == "latency_ms":
            if isinstance(value, (int, float)):
                normalized[key] = float(value)
            else:
                try:
                    normalized[key] = float(value)
                except (TypeError, ValueError):
                    normalized[key] = 0.0
        elif isinstance(value, bool):
            normalized[key] = value
        elif value is not None:
            normalized[key] = str(value)

    return normalized


def _base_response(
    perception,
    state_resolution,
    policy_source,
    policy_id,
    intent_summary,
    *,
    execute_policy,
    shutdown_acknowledged=False,
    policy_lifetime=0.0,
    activity_mode="",
    motion_primitives=None,
    warnings=None,
    error_code="",
    error_message="",
    queue_state=None,
    entry_block_reason="",
    ai_debug=None,
    allowed_pools=None,
):
    request_id = ""
    request_role = ""
    source_state_epoch = -1
    source_policy_index = -1
    if isinstance(perception, dict):
        frame = perception.get("frame") if isinstance(perception.get("frame"), dict) else {}
        request_id = frame.get("request_id", "")
        request_role = str(frame.get("request_role", "") or "")
        try:
            source_state_epoch = int(frame.get("current_state_epoch"))
        except (TypeError, ValueError):
            source_state_epoch = -1
        try:
            source_policy_index = int(frame.get("policy_index"))
        except (TypeError, ValueError):
            source_policy_index = -1

    current_state = state_resolution.current_state if state_resolution is not None else ""
    internal_state = getattr(state_resolution, "internal_state", current_state) if state_resolution is not None else ""
    activity_id = getattr(state_resolution, "activity_id", "") if state_resolution is not None else ""
    activity_context = getattr(state_resolution, "activity_context", None) if state_resolution is not None else None
    previous_state = state_resolution.previous_state if state_resolution is not None else ""
    state_changed = bool(state_resolution.state_changed) if state_resolution is not None else False
    state_reason = state_resolution.state_reason if state_resolution is not None else ""
    motion_primitives = list(motion_primitives or [])
    warnings = list(warnings or [])
    ai_debug = _normalize_ai_debug(ai_debug)
    ai_debug["request_role"] = request_role
    ai_debug["source_state_epoch"] = source_state_epoch
    ai_debug["source_policy_index"] = source_policy_index
    if policy_source == POLICY_SOURCE_FALLBACK:
        ai_debug["fallback_status"] = "active"
    elif policy_source == POLICY_SOURCE_POLICY_ERROR:
        ai_debug["fallback_status"] = "not_used_policy_error"

    response = {
        "response_meta": {
            "schema_version": SCHEMA_VERSION,
            "request_id": request_id,
            "response_id": f"{policy_source}_{uuid4().hex}",
        },
        "control": {
            "companion_should_run": not shutdown_acknowledged,
            "shutdown_acknowledged": shutdown_acknowledged,
            "execute_policy": execute_policy,
            "policy_error": policy_source == POLICY_SOURCE_POLICY_ERROR,
        },
        "state": {
            "current_state": current_state,
            "previous_state": previous_state,
            "state_changed": state_changed,
            "state_change_id": _extract_state_change_id(perception),
            "state_reason": state_reason,
        },
        "policy": {
            "policy_id": policy_id,
            "policy_source": policy_source,
            "activity_mode": activity_mode,
            "policy_lifetime": policy_lifetime,
            "intent_summary": intent_summary,
        },
        "allowed_pools": deepcopy(allowed_pools) if isinstance(allowed_pools, dict) else build_allowed_pools(current_state, perception),
        "motion_primitives": motion_primitives,
        "queue": deepcopy(queue_state) if isinstance(queue_state, dict) else _empty_queue(),
        "debug": {
            "provider_name": SERVER_NAME,
            "source_request_id": request_id,
            "source_state": current_state,
            "build_time_utc": _utc_now_iso(),
            "error_code": error_code,
            "error_message": error_message,
            "warnings": warnings,
            "ai_enabled": ai_debug["ai_enabled"],
            "fallback_enabled": ai_debug["fallback_enabled"],
            "force_fallback_for_test": ai_debug["force_fallback_for_test"],
            "ai_status": ai_debug["ai_status"],
            "ai_error_status": ai_debug["ai_error_status"],
            "ai_error_message": ai_debug["ai_error_message"],
            "provider": ai_debug["provider"],
            "model": ai_debug["model"],
            "latency_ms": ai_debug["latency_ms"],
            "validator_error_codes": list(ai_debug["validator_error_codes"]),
            "validator_warnings": list(ai_debug["validator_warnings"]),
            "validator_debug": deepcopy(ai_debug["validator_debug"]),
            "fallback_reason": ai_debug["fallback_reason"],
            "request_role": ai_debug["request_role"],
            "source_state_epoch": ai_debug["source_state_epoch"],
            "source_policy_index": ai_debug["source_policy_index"],
            "ai_job_status": ai_debug["ai_job_status"],
            "pending_policy_status": ai_debug["pending_policy_status"],
            "state_change_invalidated_policy": ai_debug["state_change_invalidated_policy"],
            "fallback_status": ai_debug["fallback_status"],
            "resolved_activity_id": activity_id,
            "internal_state": internal_state,
            "executable_state": current_state,
        },
    }

    if activity_context is not None:
        response["debug"]["activity_context"] = {
            "id": getattr(activity_context, "activity_id", ""),
            "phase": getattr(activity_context, "activity_phase", ""),
            "detail_phase": getattr(activity_context, "detail_phase", ""),
            "fishing_completed": bool(getattr(activity_context, "fishing_completed", False)),
            "context_key": getattr(activity_context, "context_key", ""),
            "context_epoch": getattr(activity_context, "context_epoch", -1),
        }

    if entry_block_reason:
        response["debug"]["entry_block_reason"] = entry_block_reason

    return response


def _extract_state_change_id(perception):
    if not isinstance(perception, dict):
        return ""

    events = perception.get("events")
    if isinstance(events, dict) and events.get("state_change_id") is not None:
        return str(events.get("state_change_id"))

    companion = perception.get("companion")
    if isinstance(companion, dict) and companion.get("state_change_id") is not None:
        return str(companion.get("state_change_id"))

    return ""


def build_fallback_response(perception, state_resolution, queue_state=None, warnings=None, ai_debug=None, fallback_reason="", policy_memory_context=None):
    current_state = state_resolution.current_state if state_resolution else ""
    state_config = get_state_config(current_state)
    policy_id = f"{state_config['policy_id_prefix']}_fallback"
    allowed_pools = build_allowed_pools(current_state, perception)
    policy_lifetime = DEFAULT_POLICY_LIFETIME

    if ai_debug is None and fallback_reason:
        ai_debug = {
            "fallback_reason": fallback_reason,
        }

    companion = perception.get("companion") if isinstance(perception, dict) and isinstance(perception.get("companion"), dict) else {}
    hand_ready = bool(companion.get("hand_pose_runtime_available"))
    prop_ready = bool(companion.get("companion_prop_runtime_available"))
    current_hand_pose = str(companion.get("current_hand_pose") or "").strip().lower()
    current_stick_state = str(companion.get("companion_stick_state") or "").strip().lower()
    current_marshmallow_state = str(companion.get("companion_marshmallow_state") or "").strip().lower()
    current_fishing_rod_state = str(companion.get("companion_fishing_rod_state") or "").strip().lower()

    motion_primitives = []
    frame = perception.get("frame") if isinstance(perception, dict) and isinstance(perception.get("frame"), dict) else {}
    allowed_look_targets = {str(value) for value in list(allowed_pools.get("allowed_targets") or [])}
    memory_context = policy_memory_context if isinstance(policy_memory_context, dict) else {}
    last_state = str(memory_context.get("last_state") or "")
    last_policy_had_movement = bool(memory_context.get("last_policy_had_movement"))
    try:
        consecutive_passive_policy_count = int(memory_context.get("consecutive_passive_policy_count") or 0)
    except (TypeError, ValueError):
        consecutive_passive_policy_count = 0
    last_motion_types = [str(value) for value in list(memory_context.get("last_motion_primitive_types") or []) if str(value)]

    def is_look_target_available(target_id):
        return str(target_id or "") in allowed_look_targets

    def select_s3_fallback_gaze_target():
        try:
            policy_index = int(frame.get("policy_index", 0))
        except (TypeError, ValueError):
            policy_index = 0

        cadence = policy_index % 4
        if cadence in {0, 1}:
            return TARGET_CAMPFIRE_CENTER
        if cadence == 2:
            return TARGET_PLAYER_HEAD if is_look_target_available(TARGET_PLAYER_HEAD) else TARGET_CAMPFIRE_CENTER
        return TARGET_COMPANION_MARSHMALLOW if is_look_target_available(TARGET_COMPANION_MARSHMALLOW) else TARGET_CAMPFIRE_CENTER

    def select_fishing_gaze_target():
        for target_id in (TARGET_WATER_FOCUS, TARGET_PLAYER_BOBBER, TARGET_PLAYER_FISHING_ROD, TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD):
            if is_look_target_available(target_id):
                return target_id
        return TARGET_NEUTRAL_FORWARD

    def maybe_add_hand_pose(target_pose, duration):
        if hand_ready and PRIMITIVE_SET_HAND_POSE in allowed_pools["motion_primitives"] and current_hand_pose != target_pose:
            motion_primitives.append(
                {"type": PRIMITIVE_SET_HAND_POSE, "pose": target_pose, "duration": duration, "style": "", "notes": f"Fallback sets hand pose to {target_pose}."}
            )

    def maybe_add_stick_visible(duration):
        nonlocal current_stick_state
        if prop_ready and PRIMITIVE_SET_COMPANION_PROP in allowed_pools["motion_primitives"] and current_stick_state != PROP_STATE_VISIBLE_IN_HAND:
            motion_primitives.append(
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": duration, "style": "", "notes": "Fallback shows the companion stick first."}
            )
            current_stick_state = PROP_STATE_VISIBLE_IN_HAND

    def maybe_add_marshmallow_visible(duration):
        nonlocal current_marshmallow_state
        if (
            prop_ready
            and PRIMITIVE_SET_COMPANION_PROP in allowed_pools["motion_primitives"]
            and current_stick_state == PROP_STATE_VISIBLE_IN_HAND
            and current_marshmallow_state != PROP_STATE_VISIBLE_ON_STICK
        ):
            motion_primitives.append(
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": duration, "style": "", "notes": "Fallback shows the companion marshmallow after the stick is visible."}
            )
            current_marshmallow_state = PROP_STATE_VISIBLE_ON_STICK

    def add_s3_roasting_prop_setup(duration):
        nonlocal current_stick_state, current_marshmallow_state
        if not prop_ready or PRIMITIVE_SET_COMPANION_PROP not in allowed_pools["motion_primitives"]:
            return

        # S3 fallback intentionally repeats these idempotent setters because
        # Stage 4B lookahead can make request-time prop snapshots stale.
        motion_primitives.append(
            {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": duration, "style": "", "notes": "Fallback deterministically shows the companion stick first."}
        )
        current_stick_state = PROP_STATE_VISIBLE_IN_HAND
        motion_primitives.append(
            {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": duration, "style": "", "notes": "Fallback deterministically shows the companion marshmallow after the stick."}
        )
        current_marshmallow_state = PROP_STATE_VISIBLE_ON_STICK

    if current_state == "S1_pre_fire_free_companion":
        if hand_ready and current_hand_pose not in {"neutral", "relaxed"}:
            maybe_add_hand_pose("neutral", 0.25)
        motion_primitives.extend(
            [
                {
                    "type": "move_within_scope",
                    "target": "",
                    "scope": SCOPE_AROUND_PLAYER,
                    "radius_min": S1_AROUND_PLAYER_RADIUS_MIN,
                    "radius_max": S1_AROUND_PLAYER_RADIUS_MAX,
                    "duration": 3.0,
                    "style": "calm_reposition",
                    "notes": "S1 fallback repositions calmly around the player.",
                },
                {"type": "look_at", "target": "player_head", "duration": 1.0, "style": "", "notes": "S1 fallback keeps calm attention on the player."},
            ]
        )
        activity_mode = ACTIVITY_IDLE_PRESENCE
        intent_summary = "S1 reposition calmly around the player and keep light attention."
    elif current_state == "S2_fire_ready_companion":
        maybe_add_stick_visible(0.2)
        motion_primitives.extend(
            [
                {
                    "type": "move_within_scope",
                    "target": "interaction_area",
                    "scope": SCOPE_INTERACTION_AREA,
                    "duration": 2.0,
                    "style": "controlled_reposition",
                    "notes": "S2 fallback stays inside the interaction area.",
                },
                {"type": "look_at", "target": "campfire_center", "duration": 5.0, "style": "", "notes": "S2 co-attention toward campfire"},
            ]
        )
        activity_mode = ACTIVITY_CO_ATTENTION
        intent_summary = "S2 stay inside the interaction area and watch the campfire."
        policy_lifetime = max(policy_lifetime, sum(float(primitive.get("duration", 0.0)) for primitive in motion_primitives))
    elif current_state == STATE_F2:
        if prop_ready and PRIMITIVE_SET_COMPANION_PROP in allowed_pools["motion_primitives"] and current_fishing_rod_state != PROP_STATE_VISIBLE_IN_HAND:
            motion_primitives.append(
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": PROP_FISHING_ROD, "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.2, "style": "", "notes": "F2 fallback keeps the companion Fishing Rod visible."}
            )
        should_reposition = last_state != STATE_F2 or not last_policy_had_movement
        if not should_reposition and consecutive_passive_policy_count >= 2:
            should_reposition = True

        if should_reposition:
            motion_primitives.extend(
                [
                    {
                        "type": "move_within_scope",
                        "target": "interaction_area",
                        "scope": SCOPE_INTERACTION_AREA,
                        "duration": 2.0,
                        "style": "controlled_reposition",
                        "notes": "F2 fallback stays inside the Fishing interaction area.",
                    },
                    {"type": "look_at", "target": select_fishing_gaze_target(), "duration": 1.5, "style": "", "notes": "F2 fallback keeps Fishing-related co-attention after repositioning."},
                ]
            )
            intent_summary = "F2 make one short in-area reposition and maintain Fishing-ready co-attention."
        else:
            motion_primitives.extend(
                [
                    {"type": PRIMITIVE_LOOK_AT, "target": select_fishing_gaze_target(), "duration": 1.5, "style": "", "notes": "F2 fallback keeps Fishing-related co-attention without moving again immediately."},
                    {"type": PRIMITIVE_HOLD_POSITION, "target": "", "duration": 1.0, "style": "", "notes": "F2 fallback remains still inside the Fishing interaction area."},
                ]
            )
            intent_summary = "F2 remain still inside the Fishing interaction area and keep Fishing-ready co-attention."
        activity_mode = ACTIVITY_CO_ATTENTION
        policy_lifetime = max(policy_lifetime, sum(float(primitive.get("duration", 0.0)) for primitive in motion_primitives))
    elif current_state == STATE_F3:
        if prop_ready and PRIMITIVE_SET_COMPANION_PROP in allowed_pools["motion_primitives"] and current_fishing_rod_state != PROP_STATE_VISIBLE_IN_HAND:
            motion_primitives.append(
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": PROP_FISHING_ROD, "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.2, "style": "", "notes": "F3 fallback keeps the companion Fishing Rod visible."}
            )
        motion_primitives.append(
            {"type": PRIMITIVE_HOLD_POSITION, "target": "", "duration": 1.0, "style": "", "notes": "F3 fallback holds the assigned Fishing anchor while remaining Fishing-ready outside the cycle."}
        )
        motion_primitives.append(
            {"type": PRIMITIVE_FACE_TOWARD, "target": select_fishing_gaze_target(), "duration": 1.0, "style": "", "notes": "F3 fallback keeps the body oriented toward the Fishing moment."}
        )
        if PRIMITIVE_PERFORM_FISHING_CAST_CYCLE in allowed_pools["motion_primitives"] and PRIMITIVE_PERFORM_FISHING_CAST_CYCLE not in last_motion_types:
            motion_primitives.append(
                {
                    "type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
                    "target": "",
                    "duration": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS,
                    "style": "",
                    "notes": "F3 fallback performs one finite Fishing cast cycle.",
                }
            )
            intent_summary = "F3 remain Fishing-ready at the assigned anchor and perform one finite Fishing cast cycle."
        else:
            motion_primitives.append(
                {"type": PRIMITIVE_LOOK_AT, "target": select_fishing_gaze_target(), "duration": 1.5, "style": "", "notes": "F3 fallback watches the Fishing area without repeating a cast cycle."}
            )
            intent_summary = "F3 remain Fishing-ready at the assigned anchor and keep restrained Fishing attention."
        activity_mode = ACTIVITY_PARALLEL_ENGAGE
        policy_lifetime = max(policy_lifetime, sum(float(primitive.get("duration", 0.0)) for primitive in motion_primitives))
    elif current_state == "S3_roasting_engagement":
        add_s3_roasting_prop_setup(0.2)
        s3_gaze_target = select_s3_fallback_gaze_target()
        motion_primitives.extend(
            [
                {"type": "hold_position", "target": "", "duration": 1.0, "style": "", "notes": "S3 hold anchor position"},
                {"type": PRIMITIVE_FACE_TOWARD, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0, "style": "", "notes": "S3 fallback explicitly faces the body toward the campfire."},
                {"type": PRIMITIVE_LOOK_AT, "target": s3_gaze_target, "duration": 1.5, "style": "", "notes": "S3 fallback keeps the head gaze natural while the body stays oriented toward the campfire."},
            ]
        )
        activity_mode = ACTIVITY_PARALLEL_ENGAGE
        intent_summary = "S3 hold anchor position and look toward campfire"
        policy_lifetime = max(policy_lifetime, sum(float(primitive.get("duration", 0.0)) for primitive in motion_primitives))
    else:
        activity_mode = ""
        intent_summary = "Unknown state fallback requested."

    return _base_response(
        perception,
        state_resolution,
        POLICY_SOURCE_FALLBACK,
        policy_id,
        intent_summary,
        execute_policy=True,
        policy_lifetime=policy_lifetime,
        activity_mode=activity_mode,
        motion_primitives=motion_primitives,
        warnings=warnings,
        queue_state=queue_state,
        ai_debug=ai_debug,
        allowed_pools=allowed_pools,
    )


def build_transition_priority_response(perception, state_resolution, queue_state, transition_reason, warnings=None, ai_debug=None):
    current_state = state_resolution.current_state if state_resolution else ""
    internal_state = getattr(state_resolution, "internal_state", current_state) if state_resolution else ""
    state_config = get_state_config(current_state)
    policy_id = f"{state_config['policy_id_prefix']}_transition_priority"
    placeholder_primitives = []
    current_goal_target = queue_state.get("current_task_goal_target", "") if isinstance(queue_state, dict) else ""

    if current_state == "S2_fire_ready_companion":
        placeholder_primitives = [
            {
                "type": "move_within_scope",
                "scope": SCOPE_TRANSITION_ONLY,
                "target": "nearest_valid_interaction_area_point",
                "duration": 3.0,
                "style": "entry_transition",
                "notes": "S2 transition enters the interaction area.",
            },
            {
                "type": "set_hand_pose",
                "pose": POSE_READY,
                "duration": 0.5,
                "style": "entry_transition",
                "notes": "S2 transition finishes with the companion hand in ready pose.",
            },
        ]
    elif current_state == "S3_roasting_engagement":
        placeholder_primitives = [
            {
                "type": "move_within_scope",
                "scope": SCOPE_TRANSITION_ONLY,
                "target": "nearest_free_roasting_anchor",
                "duration": 3.0,
                "style": "entry_transition",
                "notes": "S3 transition moves to the nearest free roasting anchor.",
            },
            {
                "type": "set_hand_pose",
                "pose": POSE_ROAST_POSE,
                "duration": 0.5,
                "style": "entry_transition",
                "notes": "S3 transition finishes with the companion hand in roast_pose.",
            },
        ]
    elif internal_state == STATE_F2 and current_goal_target == TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT:
        placeholder_primitives = [
            {
                "type": "move_within_scope",
                "scope": SCOPE_TRANSITION_ONLY,
                "target": TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT,
                "duration": 3.0,
                "style": "entry_transition",
                "notes": "F2 transition enters the Fishing interaction area.",
            },
            {
                "type": "set_companion_prop",
                "prop": PROP_FISHING_ROD,
                "state": PROP_STATE_VISIBLE_IN_HAND,
                "duration": 0.2,
                "style": "entry_transition",
                "notes": "F2 transition shows the companion Fishing Rod before active engagement.",
            },
            {
                "type": "set_hand_pose",
                "pose": POSE_FISHING_READY,
                "duration": 0.5,
                "style": "entry_transition",
                "notes": "F2 transition finishes with the companion hand in a Fishing-ready pose.",
            },
        ]
    elif internal_state == STATE_F3 and current_goal_target == TARGET_ASSIGNED_FISHING_ANCHOR:
        placeholder_primitives = [
            {
                "type": "move_within_scope",
                "scope": SCOPE_TRANSITION_ONLY,
                "target": TARGET_ASSIGNED_FISHING_ANCHOR,
                "duration": 3.0,
                "style": "entry_transition",
                "notes": "F3 transition moves to the assigned Fishing anchor.",
            },
            {
                "type": "face_toward",
                "target": TARGET_ASSIGNED_FISHING_CAST_TARGET,
                "duration": 1.0,
                "style": "entry_transition",
                "notes": "F3 transition aligns the companion toward the assigned Fishing cast target.",
            },
            {
                "type": "set_companion_prop",
                "prop": PROP_FISHING_ROD,
                "state": PROP_STATE_VISIBLE_IN_HAND,
                "duration": 0.2,
                "style": "entry_transition",
                "notes": "F3 transition shows the companion Fishing Rod.",
            },
            {
                "type": "set_hand_pose",
                "pose": POSE_FISHING_READY,
                "duration": 0.5,
                "style": "entry_transition",
                "notes": "F3 transition finishes with the companion hand in a Fishing-ready pose.",
            },
        ]

    transition_duration = sum(float(primitive.get("duration", 0.0)) for primitive in placeholder_primitives)
    return _base_response(
        perception,
        state_resolution,
        POLICY_SOURCE_TRANSITION_PRIORITY,
        policy_id,
        transition_reason,
        execute_policy=True,
        policy_lifetime=max(TRANSITION_POLICY_LIFETIME, transition_duration),
        activity_mode=ACTIVITY_IDLE_PRESENCE,
        motion_primitives=placeholder_primitives,
        warnings=warnings,
        queue_state=queue_state,
        entry_block_reason=transition_reason,
        ai_debug=ai_debug,
        allowed_pools=build_allowed_pools(current_state, perception),
    )


def build_policy_error_response(perception, state_resolution, error_code, error_message, warnings=None, ai_debug=None):
    state_id = state_resolution.current_state if state_resolution else ""
    policy_id = f"{state_id or 'unknown'}_policy_error"
    return _base_response(
        perception,
        state_resolution,
        POLICY_SOURCE_POLICY_ERROR,
        policy_id,
        error_message,
        execute_policy=False,
        policy_lifetime=0.0,
        activity_mode="",
        motion_primitives=[],
        warnings=warnings if warnings is not None else [error_message],
        error_code=error_code,
        error_message=error_message,
        queue_state=_empty_queue(),
        ai_debug=ai_debug,
        allowed_pools=build_allowed_pools(state_id, perception),
    )


def build_shutdown_response(perception, state_resolution, warnings=None, ai_debug=None):
    state_id = state_resolution.current_state if state_resolution else ""
    policy_id = f"{state_id or 'unknown'}_shutdown"
    return _base_response(
        perception,
        state_resolution,
        POLICY_SOURCE_SHUTDOWN,
        policy_id,
        "Shutdown acknowledged.",
        execute_policy=False,
        shutdown_acknowledged=True,
        policy_lifetime=0.0,
        activity_mode="",
        motion_primitives=[],
        warnings=warnings,
        error_code=ERROR_SHUTDOWN,
        error_message="Shutdown signal received.",
        queue_state=_empty_queue(),
        ai_debug=ai_debug,
        allowed_pools=build_allowed_pools(state_id, perception),
    )


def build_ai_response(perception, state_resolution, ai_result, queue_state=None, warnings=None, ai_debug=None):
    current_state = state_resolution.current_state if state_resolution else ""
    state_config = get_state_config(current_state)
    policy_id = f"{state_config['policy_id_prefix']}_ai"
    clean_policy = ai_result.get("policy") if isinstance(ai_result, dict) else {}
    if not isinstance(clean_policy, dict):
        clean_policy = {}

    if ai_debug is None and isinstance(ai_result, dict):
        ai_debug = {
            "ai_enabled": True,
            "fallback_enabled": False,
            "force_fallback_for_test": False,
            "ai_status": "success",
            "ai_error_status": "",
            "ai_error_message": "",
            "provider": ai_result.get("provider", ""),
            "model": ai_result.get("model", ""),
            "latency_ms": ai_result.get("latency_ms", 0.0),
            "validator_error_codes": ai_result.get("validator_error_codes", []),
            "validator_warnings": ai_result.get("warnings", []),
            "validator_debug": ai_result.get("validator_debug", {}),
            "fallback_reason": "",
        }

    response_warnings = list(warnings or [])
    if warnings is None and isinstance(ai_result, dict):
        response_warnings = list(ai_result.get("warnings", []))

    return _base_response(
        perception,
        state_resolution,
        POLICY_SOURCE_AI,
        policy_id,
        clean_policy.get("intent_summary", "AI policy result."),
        execute_policy=True,
        policy_lifetime=clean_policy.get("policy_lifetime", DEFAULT_POLICY_LIFETIME),
        activity_mode=clean_policy.get("activity_mode", ""),
        motion_primitives=clean_policy.get("motion_primitives", []),
        warnings=response_warnings,
        queue_state=queue_state,
        ai_debug=ai_debug,
        allowed_pools=build_allowed_pools(current_state, perception),
    )


def build_json_payload(response):
    return response
