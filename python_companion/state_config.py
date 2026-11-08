"""State configuration and allowed primitive pools."""

from copy import deepcopy

from .models import (
    ACTIVITY_ID_CAMPFIRE,
    ACTIVITY_ID_COMMON,
    ACTIVITY_ID_FISHING,
    ACTIVITY_AMBIENT_EXPLORE,
    ACTIVITY_CO_ATTENTION,
    ACTIVITY_IDLE_PRESENCE,
    ACTIVITY_PARALLEL_ENGAGE,
    ACTIVITY_RESPONSIVE_FEEDBACK,
    ACTIVITY_SELF_PREPARE,
    ACTIVITY_PHASE_ENGAGEMENT,
    ACTIVITY_PHASE_FREE,
    ACTIVITY_PHASE_READY,
    POSE_FISHING_READY,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
    PRIMITIVE_SET_COMPANION_PROP,
    PRIMITIVE_SET_HAND_POSE,
    PROP_FISHING_ROD,
    PROP_STATE_HIDDEN,
    PROP_STATE_VISIBLE_IN_HAND,
    PROP_STATE_VISIBLE_ON_STICK,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    SCOPE_NAMED_POINT,
    STATE_F2,
    STATE_F3,
    STATE_S1,
    STATE_S2,
    STATE_S3,
    TARGET_CAMPFIRE_CENTER,
    TARGET_COMPANION_MARSHMALLOW,
    TARGET_NEUTRAL_FORWARD,
    TARGET_PLAYER_BOBBER,
    TARGET_PLAYER_FISHING_ROD,
    TARGET_PLAYER_HEAD,
    TARGET_WATER_FOCUS,
)

S1_AROUND_PLAYER_RADIUS_MIN = 1.2
S1_AROUND_PLAYER_RADIUS_MAX = 2.5
S1_AROUND_PLAYER_REENGAGE_BUFFER = 0.35
S1_PLAYER_DISPLACEMENT_RECOMMEND_THRESHOLD = 0.75


def _activity_mode_definitions(entries):
    return {key: value for key, value in entries}


def _motion_primitive_definitions(entries):
    return {key: value for key, value in entries}


def _state_metadata(activity_id, activity_phase, *, execution_ready=True, future_motion_primitives=None):
    return {
        "activity_id": activity_id,
        "activity_phase": activity_phase,
        "execution_ready": bool(execution_ready),
        "future_motion_primitives": list(future_motion_primitives or []),
    }


STATE_CONFIG = {
    STATE_S1: {
        "description": "Common free-companion state. No activity-specific participation context is currently active.",
        "activity_modes": [ACTIVITY_IDLE_PRESENCE, ACTIVITY_AMBIENT_EXPLORE, ACTIVITY_CO_ATTENTION],
        "motion_primitives": [
            PRIMITIVE_LOOK_AT,
            PRIMITIVE_FACE_TOWARD,
            PRIMITIVE_MOVE_WITHIN_SCOPE,
            PRIMITIVE_HOLD_POSITION,
        ],
        "default_activity_mode": ACTIVITY_IDLE_PRESENCE,
        "policy_id_prefix": "stage4a_s1",
        "allowed_targets": [TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD, TARGET_CAMPFIRE_CENTER],
        "forbidden_motion_primitives": [],
        "allowed_scopes": [SCOPE_AROUND_PLAYER, SCOPE_NAMED_POINT],
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
        "forbidden_rules": [
            "move_within_scope may only use around_player or named_point.",
            "For around_player movement, use target=\"\"; Unity resolves the player-centered destination.",
            "set_companion_prop is forbidden in S1 normal policy.",
            "idle_motion and play_gesture remain unavailable.",
            "No speech or Blackboard modification.",
            "No player task control.",
        ],
        "policy_notes": [
            "Stay present and calm.",
            "Hand pose is capability-gated and may only use relaxed or neutral when available.",
        ],
        "activity_mode_definitions": _activity_mode_definitions(
            [
                (ACTIVITY_IDLE_PRESENCE, "Stay nearby without task pressure or direct action."),
                (ACTIVITY_AMBIENT_EXPLORE, "Lightly observe the environment or make one short reposition within the allowed around_player scope while remaining calm and near the player."),
                (ACTIVITY_CO_ATTENTION, "Share attention with the player toward a meaningful target."),
            ]
        ),
        "motion_primitive_definitions": _motion_primitive_definitions(
            [
                (PRIMITIVE_LOOK_AT, "Look toward a semantic target."),
                (PRIMITIVE_FACE_TOWARD, "Turn the body toward a semantic target."),
                (PRIMITIVE_MOVE_WITHIN_SCOPE, "Move only inside a permitted semantic scope."),
                (PRIMITIVE_HOLD_POSITION, "Stay in place while the policy is active."),
                (PRIMITIVE_SET_HAND_POSE, "Set the companion's coarse right-hand pose when capability is available."),
            ]
        ),
        "state_metadata": _state_metadata(ACTIVITY_ID_COMMON, ACTIVITY_PHASE_FREE),
    },
    STATE_F2: {
        "description": "Fishing ready state. The companion has completed Fishing entry and can remain inside the Fishing interaction area with visible Fishing-ready embodiment.",
        "activity_modes": [ACTIVITY_IDLE_PRESENCE, ACTIVITY_CO_ATTENTION, ACTIVITY_SELF_PREPARE],
        "motion_primitives": [
            PRIMITIVE_LOOK_AT,
            PRIMITIVE_FACE_TOWARD,
            PRIMITIVE_MOVE_WITHIN_SCOPE,
            PRIMITIVE_HOLD_POSITION,
        ],
        "default_activity_mode": ACTIVITY_SELF_PREPARE,
        "policy_id_prefix": "stage6b_f2",
        "allowed_targets": [TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD, TARGET_WATER_FOCUS, TARGET_PLAYER_FISHING_ROD, TARGET_PLAYER_BOBBER],
        "forbidden_motion_primitives": [PRIMITIVE_PERFORM_FISHING_CAST_CYCLE],
        "allowed_scopes": [SCOPE_INTERACTION_AREA],
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
        "forbidden_rules": [
            "move_within_scope must stay inside interaction_area.",
            "around_player is forbidden in F2 normal policy.",
            "perform_fishing_cast_cycle is forbidden in F2 normal policy.",
            "Do not output Campfire targets, Campfire props, Campfire poses, or transition-only movement.",
            "No speech or Blackboard modification.",
            "No player task control.",
        ],
        "policy_notes": [
            "Remain inside the Fishing interaction area with visible Fishing-ready presentation.",
            "Normal short repositioning inside interaction_area is allowed, but do not drift into around_player semantics.",
            "Do not perform the F3 cast cycle here.",
        ],
        "activity_mode_definitions": _activity_mode_definitions(
            [
                (ACTIVITY_IDLE_PRESENCE, "Stay nearby without task pressure or direct action."),
                (ACTIVITY_CO_ATTENTION, "Share attention with the player toward the current Fishing moment."),
                (ACTIVITY_SELF_PREPARE, "Adopt a quiet ready posture inside the Fishing interaction area."),
            ]
        ),
        "motion_primitive_definitions": _motion_primitive_definitions(
            [
                (PRIMITIVE_LOOK_AT, "Look toward a semantic target."),
                (PRIMITIVE_FACE_TOWARD, "Turn the body toward a semantic target."),
                (PRIMITIVE_MOVE_WITHIN_SCOPE, "Move only inside the Fishing interaction area."),
                (PRIMITIVE_HOLD_POSITION, "Stay in place while the policy is active."),
                (PRIMITIVE_SET_HAND_POSE, "Set the companion's coarse right-hand pose when capability is available."),
                (PRIMITIVE_SET_COMPANION_PROP, "Show or hide the companion-owned Fishing Rod when capability is available."),
            ]
        ),
        "state_metadata": _state_metadata(ACTIVITY_ID_FISHING, ACTIVITY_PHASE_READY),
    },
    STATE_F3: {
        "description": "Fishing engagement state. The companion is stationed at the assigned Fishing anchor and may express restrained embodied participation without moving away.",
        "activity_modes": [
            ACTIVITY_IDLE_PRESENCE,
            ACTIVITY_CO_ATTENTION,
            ACTIVITY_SELF_PREPARE,
            ACTIVITY_PARALLEL_ENGAGE,
            ACTIVITY_RESPONSIVE_FEEDBACK,
        ],
        "motion_primitives": [
            PRIMITIVE_LOOK_AT,
            PRIMITIVE_FACE_TOWARD,
            PRIMITIVE_HOLD_POSITION,
            PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
        ],
        "default_activity_mode": ACTIVITY_PARALLEL_ENGAGE,
        "policy_id_prefix": "stage6b_f3",
        "allowed_targets": [TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD, TARGET_WATER_FOCUS, TARGET_PLAYER_FISHING_ROD, TARGET_PLAYER_BOBBER],
        "forbidden_motion_primitives": [PRIMITIVE_MOVE_WITHIN_SCOPE],
        "allowed_scopes": [],
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
        "forbidden_rules": [
            "move_within_scope is forbidden in F3 normal policy.",
            "perform_fishing_cast_cycle is one finite action and must not be emitted more than once in a single policy.",
            "Do not represent cast, wait, and reel as separate public primitives.",
            "Do not output Campfire targets, Campfire props, Campfire poses, or transition-only movement.",
            "No speech or Blackboard modification.",
            "No player task control.",
        ],
        "policy_notes": [
            "Stay anchor-locked in Fishing engagement.",
            "perform_fishing_cast_cycle is the primary F3 parallel-engagement action when Fishing setup is complete.",
            "Normally prefer one cast cycle when the previous accepted Fishing policy did not already contain one.",
            "Use one short non-cast attention policy between successive cast policies; never auto-loop casting.",
            "Outside the cast cycle, remain in fishing_ready while staying anchored with the Fishing Rod visible.",
        ],
        "activity_mode_definitions": _activity_mode_definitions(
            [
                (ACTIVITY_IDLE_PRESENCE, "Stay nearby without task pressure or direct action."),
                (ACTIVITY_CO_ATTENTION, "Share attention with the player toward the current Fishing moment."),
                (ACTIVITY_SELF_PREPARE, "Quietly maintain readiness for Fishing participation."),
                (ACTIVITY_PARALLEL_ENGAGE, "Participate alongside the player in a restrained nonverbal way."),
                (ACTIVITY_RESPONSIVE_FEEDBACK, "Give a small nonverbal response to player progress or timing cues."),
            ]
        ),
        "motion_primitive_definitions": _motion_primitive_definitions(
            [
                (PRIMITIVE_LOOK_AT, "Look toward a semantic target."),
                (PRIMITIVE_FACE_TOWARD, "Turn the body toward a semantic target."),
                (PRIMITIVE_HOLD_POSITION, "Stay in place while the policy is active."),
                (PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "Perform one finite internal Fishing cast-wait-reel embodiment cycle."),
                (PRIMITIVE_SET_HAND_POSE, "Set the companion's coarse right-hand pose when capability is available."),
                (PRIMITIVE_SET_COMPANION_PROP, "Show or hide the companion-owned Fishing Rod when capability is available."),
            ]
        ),
        "state_metadata": _state_metadata(ACTIVITY_ID_FISHING, ACTIVITY_PHASE_ENGAGEMENT),
    },
    STATE_S2: {
        "description": "Campfire has started and the stick is not picked yet.",
        "activity_modes": [ACTIVITY_IDLE_PRESENCE, ACTIVITY_AMBIENT_EXPLORE, ACTIVITY_CO_ATTENTION, ACTIVITY_SELF_PREPARE],
        "motion_primitives": [
            PRIMITIVE_LOOK_AT,
            PRIMITIVE_FACE_TOWARD,
            PRIMITIVE_MOVE_WITHIN_SCOPE,
            PRIMITIVE_HOLD_POSITION,
        ],
        "default_activity_mode": ACTIVITY_SELF_PREPARE,
        "policy_id_prefix": "stage4a_s2",
        "allowed_targets": [TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD],
        "forbidden_motion_primitives": [],
        "allowed_scopes": [SCOPE_INTERACTION_AREA],
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
        "forbidden_rules": [
            "parallel_engage is forbidden in S2 normal policy.",
            "responsive_feedback is forbidden unless a separate response layer exists.",
            "move_within_scope must stay inside interaction_area.",
            "transition_only is reserved for transition_priority responses.",
            "Marshmallow is forbidden in S2 normal policy.",
            "idle_motion and play_gesture remain unavailable.",
            "No speech or Blackboard modification.",
            "No player task control.",
        ],
        "policy_notes": [
            "Use co-attention toward the campfire or simple pre-positioning inside the interaction area.",
            "When capability is available, companion prop may use stick only; ready hand pose is established by deterministic S2 Entry.",
            "When stick prop capability is available, prefer keeping the companion's own stick visible_in_hand during normal S2 activity and in-area reposition.",
            "Do not repeatedly hide/show the companion stick merely for behavioral variety.",
        ],
        "activity_mode_definitions": _activity_mode_definitions(
            [
                (ACTIVITY_IDLE_PRESENCE, "Stay nearby without task pressure or direct action."),
                (ACTIVITY_AMBIENT_EXPLORE, "Lightly observe the fire area or make one short reposition inside interaction_area while staying within bounds."),
                (ACTIVITY_CO_ATTENTION, "Share attention with the player toward the campfire or related target."),
                (ACTIVITY_SELF_PREPARE, "Prepare the companion's own stick or posture for the next task."),
            ]
        ),
        "motion_primitive_definitions": _motion_primitive_definitions(
            [
                (PRIMITIVE_LOOK_AT, "Look toward a semantic target."),
                (PRIMITIVE_FACE_TOWARD, "Turn the body toward a semantic target."),
                (PRIMITIVE_MOVE_WITHIN_SCOPE, "Move only inside a permitted semantic scope."),
                (PRIMITIVE_HOLD_POSITION, "Stay in place while the policy is active."),
                (PRIMITIVE_SET_HAND_POSE, "Set the companion's coarse right-hand pose when capability is available."),
                (PRIMITIVE_SET_COMPANION_PROP, "Show or hide the pre-existing companion-owned visual stick when capability is available."),
            ]
        ),
        "state_metadata": _state_metadata(ACTIVITY_ID_CAMPFIRE, ACTIVITY_PHASE_READY),
    },
    STATE_S3: {
        "description": "Campfire has started and the stick is picked.",
        "activity_modes": [
            ACTIVITY_IDLE_PRESENCE,
            ACTIVITY_CO_ATTENTION,
            ACTIVITY_SELF_PREPARE,
            ACTIVITY_PARALLEL_ENGAGE,
            ACTIVITY_RESPONSIVE_FEEDBACK,
        ],
        "motion_primitives": [
            PRIMITIVE_LOOK_AT,
            PRIMITIVE_FACE_TOWARD,
            PRIMITIVE_HOLD_POSITION,
        ],
        "default_activity_mode": ACTIVITY_PARALLEL_ENGAGE,
        "policy_id_prefix": "stage4a_s3",
        "allowed_targets": [TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD],
        "forbidden_motion_primitives": [PRIMITIVE_MOVE_WITHIN_SCOPE],
        "allowed_scopes": [],
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
        "forbidden_rules": [
            "ambient_explore is forbidden in S3 normal policy.",
            "move_within_scope is forbidden in S3 normal policy.",
            "S3 movement to anchor is transition-only and should not come from normal policy.",
            "idle_motion and play_gesture remain unavailable.",
            "Only companion-owned props may be controlled.",
            "No speech or Blackboard modification.",
            "No player task control.",
        ],
        "policy_notes": [
            "Stay engaged around the campfire through look, facing, hold, and capability-gated prop setup.",
            "companion_marshmallow is only available when Unity confirms the companion marshmallow target is visible and resolvable.",
            "Keep the body roasting orientation toward campfire_center when body-facing is useful.",
            "campfire_center should remain the dominant gaze target in S3.",
            "Occasional short looks toward player_head are natural.",
            "If companion_marshmallow is available, occasional looks toward it are also natural.",
            "Do not mechanically change gaze target every Policy; repeating the same meaningful target across multiple Policies is acceptable.",
        ],
        "activity_mode_definitions": _activity_mode_definitions(
            [
                (ACTIVITY_IDLE_PRESENCE, "Stay nearby without task pressure or direct action."),
                (ACTIVITY_CO_ATTENTION, "Share attention with the player toward the fire or roasting focus."),
                (ACTIVITY_SELF_PREPARE, "Prepare the companion's own prop or posture."),
                (ACTIVITY_PARALLEL_ENGAGE, "Participate in the activity alongside the player."),
                (ACTIVITY_RESPONSIVE_FEEDBACK, "Give a small nonverbal response to player progress or completion."),
            ]
        ),
        "motion_primitive_definitions": _motion_primitive_definitions(
            [
                (PRIMITIVE_LOOK_AT, "Look toward a semantic target."),
                (PRIMITIVE_FACE_TOWARD, "Turn the body toward a semantic target."),
                (PRIMITIVE_HOLD_POSITION, "Stay in place while the policy is active."),
                (PRIMITIVE_SET_HAND_POSE, "Set the companion's coarse right-hand pose when capability is available."),
                (PRIMITIVE_SET_COMPANION_PROP, "Show or hide the pre-existing companion-owned visual stick or marshmallow when capability is available."),
            ]
        ),
        "state_metadata": _state_metadata(ACTIVITY_ID_CAMPFIRE, ACTIVITY_PHASE_ENGAGEMENT),
    },
}

PLACEHOLDER_TRANSITION_PRIMITIVES = {
    STATE_S2: [PRIMITIVE_MOVE_WITHIN_SCOPE],
    STATE_S3: [PRIMITIVE_MOVE_WITHIN_SCOPE],
}


def _default_state_config():
    return {
        "description": "",
        "activity_modes": [],
        "motion_primitives": [],
        "default_activity_mode": "",
        "policy_id_prefix": "stage4a_unknown",
        "allowed_targets": [],
        "forbidden_motion_primitives": [],
        "allowed_scopes": [],
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
        "forbidden_rules": [],
        "policy_notes": [],
        "activity_mode_definitions": {},
        "motion_primitive_definitions": {},
        "state_metadata": _state_metadata("", "", execution_ready=False),
    }


def get_state_config(state_id):
    return deepcopy(STATE_CONFIG.get(state_id, _default_state_config()))


def get_allowed_activity_modes(state_id):
    return list(get_state_config(state_id)["activity_modes"])


def get_allowed_motion_primitives(state_id):
    return list(get_state_config(state_id)["motion_primitives"])


def _bool_from_perception(perception, *path):
    current = perception if isinstance(perception, dict) else {}
    for key in path:
        if not isinstance(current, dict):
            return False
        current = current.get(key)
    return bool(current)


def _string_from_perception(perception, *path):
    current = perception if isinstance(perception, dict) else {}
    for key in path:
        if not isinstance(current, dict):
            return ""
        current = current.get(key)
    return str(current or "")


def _runtime_look_targets(perception):
    if not isinstance(perception, dict):
        return None

    scene = perception.get("scene")
    if not isinstance(scene, dict):
        return None

    look_targets = scene.get("look_targets_available")
    if not isinstance(look_targets, list):
        return None

    return {str(target) for target in look_targets if str(target)}


def _filter_dynamic_allowed_targets(allowed_targets, perception):
    runtime_targets = _runtime_look_targets(perception)
    if runtime_targets is None:
        return list(allowed_targets)

    dynamic_fishing_targets = {TARGET_PLAYER_FISHING_ROD, TARGET_PLAYER_BOBBER}
    filtered_targets = []
    for target in allowed_targets:
        if target in dynamic_fishing_targets and target not in runtime_targets:
            continue
        filtered_targets.append(target)

    return filtered_targets


def _build_capability_gated_pools(state_id, state_config, perception=None):
    pools = {
        "activity_modes": list(state_config["activity_modes"]),
        "motion_primitives": list(state_config["motion_primitives"]),
        "allowed_targets": list(state_config["allowed_targets"]),
        "forbidden_motion_primitives": list(state_config["forbidden_motion_primitives"]),
        "allowed_scopes": list(state_config["allowed_scopes"]),
        "allowed_poses": [],
        "allowed_props": [],
        "allowed_prop_states": {},
        "allowed_gestures": [],
    }

    hand_ready = _bool_from_perception(perception, "companion", "hand_pose_runtime_available")
    prop_ready = _bool_from_perception(perception, "companion", "companion_prop_runtime_available")
    has_marshmallow = _bool_from_perception(perception, "companion", "has_companion_marshmallow")
    marshmallow_state = _string_from_perception(perception, "companion", "companion_marshmallow_state")
    companion_marshmallow_target_available = False
    if isinstance(perception, dict):
        scene = perception.get("scene")
        if isinstance(scene, dict):
            look_targets = scene.get("look_targets_available")
            if isinstance(look_targets, list):
                companion_marshmallow_target_available = TARGET_COMPANION_MARSHMALLOW in look_targets

    if hand_ready and state_id == STATE_S1:
        # State-base poses for S2/F2/F3/S3 are owned by deterministic Entry
        # transitions, so normal AI/fallback pools expose only S1 coarse poses.
        pools["motion_primitives"].append(PRIMITIVE_SET_HAND_POSE)
        pools["allowed_poses"] = ["relaxed", "neutral"]

    if prop_ready:
        if state_id == STATE_F2:
            pools["motion_primitives"].append(PRIMITIVE_SET_COMPANION_PROP)
            pools["allowed_props"] = [PROP_FISHING_ROD]
            pools["allowed_prop_states"] = {
                PROP_FISHING_ROD: [PROP_STATE_VISIBLE_IN_HAND, PROP_STATE_HIDDEN],
            }
        elif state_id == STATE_F3:
            pools["motion_primitives"].append(PRIMITIVE_SET_COMPANION_PROP)
            pools["allowed_props"] = [PROP_FISHING_ROD]
            pools["allowed_prop_states"] = {
                PROP_FISHING_ROD: [PROP_STATE_VISIBLE_IN_HAND, PROP_STATE_HIDDEN],
            }
        elif state_id == STATE_S2:
            pools["motion_primitives"].append(PRIMITIVE_SET_COMPANION_PROP)
            pools["allowed_props"] = ["stick"]
            pools["allowed_prop_states"] = {
                "stick": [PROP_STATE_VISIBLE_IN_HAND, PROP_STATE_HIDDEN],
            }
        elif state_id == STATE_S3:
            pools["motion_primitives"].append(PRIMITIVE_SET_COMPANION_PROP)
            pools["allowed_props"] = ["stick", "marshmallow"]
            pools["allowed_prop_states"] = {
                "stick": [PROP_STATE_VISIBLE_IN_HAND],
                "marshmallow": [PROP_STATE_VISIBLE_ON_STICK],
            }
            if has_marshmallow and marshmallow_state == PROP_STATE_VISIBLE_ON_STICK and companion_marshmallow_target_available:
                pools["allowed_targets"].append(TARGET_COMPANION_MARSHMALLOW)

    # Keep state-level target semantics stable while exposing only currently
    # resolvable dynamic Fishing targets to AI generation and validation.
    pools["allowed_targets"] = _filter_dynamic_allowed_targets(pools["allowed_targets"], perception)

    return pools


def get_state_prompt_definitions(state_id, perception=None):
    state_config = get_state_config(state_id)
    pools = _build_capability_gated_pools(state_id, state_config, perception)
    return {
        "state_id": state_id,
        "description": state_config["description"],
        "allowed_activity_modes": list(pools["activity_modes"]),
        "allowed_motion_primitives": list(pools["motion_primitives"]),
        "allowed_targets": list(pools["allowed_targets"]),
        "forbidden_motion_primitives": list(pools["forbidden_motion_primitives"]),
        "allowed_scopes": list(pools["allowed_scopes"]),
        "allowed_poses": list(pools["allowed_poses"]),
        "allowed_props": list(pools["allowed_props"]),
        "allowed_prop_states": deepcopy(pools["allowed_prop_states"]),
        "allowed_gestures": list(pools["allowed_gestures"]),
        "forbidden_rules": list(state_config["forbidden_rules"]),
        "policy_notes": list(state_config["policy_notes"]),
        "default_activity_mode": state_config["default_activity_mode"],
        "policy_id_prefix": state_config["policy_id_prefix"],
        "activity_mode_definitions": deepcopy(state_config["activity_mode_definitions"]),
        "motion_primitive_definitions": deepcopy(state_config["motion_primitive_definitions"]),
        "state_metadata": deepcopy(state_config.get("state_metadata") or {}),
    }


def get_allowed_pools(state_id, perception=None):
    """Expose the effective capability-gated pool metadata used by prompt building and AI validation."""

    state_config = get_state_config(state_id)
    return _build_capability_gated_pools(state_id, state_config, perception)


def build_allowed_pools(state_id, perception=None):
    return get_allowed_pools(state_id, perception)
