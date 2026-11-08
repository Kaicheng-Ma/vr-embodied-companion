"""Prompt construction helpers for the Stage 4A AI policy path."""

from __future__ import annotations

import json
import math

from .config import FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS
from .models import (
    ACTIVITY_ID_CAMPFIRE,
    ACTIVITY_ID_COMMON,
    ACTIVITY_ID_FISHING,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
    PRIMITIVE_SET_COMPANION_PROP,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    STATE_F2,
    STATE_F3,
    STATE_S1,
    STATE_S2,
    STATE_S3,
)
from .state_config import (
    S1_AROUND_PLAYER_RADIUS_MAX,
    S1_AROUND_PLAYER_RADIUS_MIN,
    S1_AROUND_PLAYER_REENGAGE_BUFFER,
    S1_PLAYER_DISPLACEMENT_RECOMMEND_THRESHOLD,
    get_state_config,
    get_state_prompt_definitions,
)


def _compact_json(value):
    try:
        return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    except TypeError:
        return str(value)


def _build_perception_summary(perception):
    if not isinstance(perception, dict):
        return {}

    task = perception.get("task") if isinstance(perception.get("task"), dict) else {}
    control = perception.get("control") if isinstance(perception.get("control"), dict) else {}
    companion = perception.get("companion") if isinstance(perception.get("companion"), dict) else {}
    entry_checks = perception.get("entry_checks") if isinstance(perception.get("entry_checks"), dict) else {}
    scene = perception.get("scene") if isinstance(perception.get("scene"), dict) else {}
    frame = perception.get("frame") if isinstance(perception.get("frame"), dict) else {}
    activity = perception.get("activity") if isinstance(perception.get("activity"), dict) else {}

    look_targets = scene.get("look_targets_available")
    if isinstance(look_targets, list):
        look_target_set = {str(value) for value in look_targets}
    elif isinstance(look_targets, dict):
        look_target_set = {str(key) for key, value in look_targets.items() if value}
    else:
        look_target_set = set()

    return {
        "request_id": frame.get("request_id", ""),
        "campfire_started": bool(task.get("campfire_started")),
        "stick_picked": bool(task.get("stick_picked")),
        "activity_id": activity.get("id", ""),
        "activity_phase": activity.get("phase", ""),
        "activity_detail_phase": activity.get("detail_phase", ""),
        "activity_fishing_completed": bool(activity.get("fishing_completed")),
        "activity_context_key": activity.get("context_key", ""),
        "activity_context_epoch": activity.get("context_epoch", -1),
        "raw_unity_current_state": companion.get("current_state", ""),
        "raw_unity_previous_state": companion.get("previous_state", ""),
        "companion_inside_interaction_area": bool(entry_checks.get("companion_inside_interaction_area")),
        "companion_at_valid_roasting_anchor": bool(entry_checks.get("companion_at_valid_roasting_anchor")),
        "nearest_free_roasting_anchor_id": entry_checks.get("nearest_free_roasting_anchor_id", ""),
        "current_anchor_id": companion.get("current_anchor_id", ""),
        "campfire_center_available": bool(scene.get("campfire_center_available")),
        "player_head_available": "player_head" in look_target_set,
        "neutral_forward_available": "neutral_forward" in look_target_set,
        "companion_marshmallow_available": "companion_marshmallow" in look_target_set,
        "hand_pose_runtime_available": bool(companion.get("hand_pose_runtime_available")),
        "companion_prop_runtime_available": bool(companion.get("companion_prop_runtime_available")),
        "current_hand_pose": companion.get("current_hand_pose", ""),
        "companion_inside_fishing_interaction_area": bool(entry_checks.get("companion_inside_fishing_interaction_area")),
        "companion_at_valid_fishing_anchor": bool(entry_checks.get("companion_at_valid_fishing_anchor")),
        "fishing_rod_ready": bool(entry_checks.get("fishing_rod_ready")),
        "assigned_fishing_anchor_id": companion.get("assigned_fishing_anchor_id", ""),
        "assigned_fishing_cast_target_id": companion.get("assigned_fishing_cast_target_id", ""),
        "companion_fishing_rod_state": companion.get("companion_fishing_rod_state", ""),
        "fishing_rod_runtime_available": bool(companion.get("fishing_rod_runtime_available")),
        "companion_stick_state": companion.get("companion_stick_state", ""),
        "companion_marshmallow_state": companion.get("companion_marshmallow_state", ""),
        "companion_enabled": bool(control.get("companion_enabled", True)),
    }


def _coerce_float(value):
    try:
        numeric_value = float(value)
    except (TypeError, ValueError):
        return None
    return numeric_value if math.isfinite(numeric_value) else None


def _extract_xz_position(section):
    if not isinstance(section, dict):
        return None

    position = section.get("position")
    if not isinstance(position, dict):
        return None

    x = _coerce_float(position.get("x"))
    z = _coerce_float(position.get("z"))
    if x is None or z is None:
        return None

    return {"x": x, "z": z}


def _distance_xz(a, b):
    if not isinstance(a, dict) or not isinstance(b, dict):
        return None
    x_a = _coerce_float(a.get("x"))
    z_a = _coerce_float(a.get("z"))
    x_b = _coerce_float(b.get("x"))
    z_b = _coerce_float(b.get("z"))
    if x_a is None or z_a is None or x_b is None or z_b is None:
        return None
    return math.hypot(x_a - x_b, z_a - z_b)


def _round_optional(value):
    if value is None:
        return None
    return round(float(value), 2)


def _format_optional_distance(value):
    return "unknown" if value is None else f"{value:.2f} m"


def build_s1_spatial_movement_context(perception, policy_memory_context=None):
    """Build compact S1 movement guidance without exposing raw coordinates."""

    if not isinstance(perception, dict):
        return {
            "movement_recommendation": "unknown",
            "reason": "perception_missing",
        }

    player_position = _extract_xz_position(perception.get("player"))
    companion_position = _extract_xz_position(perception.get("companion"))
    memory_context = policy_memory_context if isinstance(policy_memory_context, dict) else {}
    previous_player_position = memory_context.get("last_player_position_xz")
    previous_companion_position = memory_context.get("last_companion_position_xz")

    distance = _distance_xz(player_position, companion_position)
    player_displacement = _distance_xz(previous_player_position, player_position)
    companion_displacement = _distance_xz(previous_companion_position, companion_position)
    last_policy_had_movement = bool(memory_context.get("last_policy_had_movement"))
    try:
        passive_streak = int(memory_context.get("consecutive_passive_policy_count") or 0)
    except (TypeError, ValueError):
        passive_streak = 0

    if distance is None:
        recommendation = "unknown"
        outside_band = None
        inside_band = None
        reason = "position_missing_or_malformed"
    else:
        inside_band = S1_AROUND_PLAYER_RADIUS_MIN <= distance <= S1_AROUND_PLAYER_RADIUS_MAX
        outside_band = not inside_band
        if distance > S1_AROUND_PLAYER_RADIUS_MAX + S1_AROUND_PLAYER_REENGAGE_BUFFER and not last_policy_had_movement:
            recommendation = "required"
            reason = "companion_clearly_farther_than_preferred_band"
        elif distance > S1_AROUND_PLAYER_RADIUS_MAX + S1_AROUND_PLAYER_REENGAGE_BUFFER:
            recommendation = "recommended"
            reason = "recent_movement_policy_may_be_executing_or_pending"
        elif outside_band:
            recommendation = "recommended"
            reason = "companion_outside_preferred_band"
        elif (
            player_displacement is not None
            and player_displacement >= S1_PLAYER_DISPLACEMENT_RECOMMEND_THRESHOLD
            and not last_policy_had_movement
        ):
            recommendation = "recommended"
            reason = "player_moved_since_previous_accepted_normal_policy"
        else:
            recommendation = "not_needed"
            reason = "companion_inside_preferred_band"

    return {
        "player_companion_distance_xz": _round_optional(distance),
        "preferred_radius_min": S1_AROUND_PLAYER_RADIUS_MIN,
        "preferred_radius_max": S1_AROUND_PLAYER_RADIUS_MAX,
        "companion_inside_preferred_band": inside_band,
        "companion_outside_preferred_band": outside_band,
        "player_displacement_since_previous_normal_policy_xz": _round_optional(player_displacement),
        "companion_displacement_since_previous_normal_policy_xz": _round_optional(companion_displacement),
        "last_policy_had_movement": last_policy_had_movement,
        "consecutive_passive_policy_count": passive_streak,
        "movement_recommendation": recommendation,
        "reason": reason,
    }


def _format_s1_spatial_context_lines(context):
    if not isinstance(context, dict):
        return []

    return [
        "Spatial movement context:",
        f"- player-companion XZ distance: {_format_optional_distance(context.get('player_companion_distance_xz'))}",
        f"- preferred nearby band: {S1_AROUND_PLAYER_RADIUS_MIN:.1f}-{S1_AROUND_PLAYER_RADIUS_MAX:.1f} m",
        f"- companion is outside the nearby band: {context.get('companion_outside_preferred_band')}",
        f"- player displacement since previous accepted normal policy: {_format_optional_distance(context.get('player_displacement_since_previous_normal_policy_xz'))}",
        f"- previous accepted normal policy contained movement: {context.get('last_policy_had_movement')}",
        f"- consecutive passive accepted normal policies: {context.get('consecutive_passive_policy_count', 0)}",
        f"- movement recommendation: {context.get('movement_recommendation', 'unknown')}",
    ]


def _build_s1_movement_prompt_text(context):
    recommendation = context.get("movement_recommendation", "unknown") if isinstance(context, dict) else "unknown"
    return "\n".join(
        [
            "S1 around_player movement rule:",
            "- move_within_scope + around_player is a finite semantic movement request; Unity chooses and live-updates the NavMesh destination.",
            '- Canonical S1 movement example: {"type":"move_within_scope","scope":"around_player","target":"","radius_min":1.2,"radius_max":2.5,"duration":4.0}',
            "- Do not output coordinates, paths, follow loops, live repath intervals, destination_update, follow_player, default_attention, base_idle, or base_move.",
            '- Invalid: {"type":"move_within_scope","scope":"around_player","target":"player_head","duration":4.0}; around_player target must be an empty string.',
            f"- Current movement recommendation: {recommendation}.",
            "- If recommendation is required, include exactly one valid move_within_scope around_player primitive unless current-state legality prevents it.",
            "- If recommendation is recommended, generally prefer one movement primitive unless a recent movement policy already addressed the spatial need.",
            "- If recommendation is not_needed, movement is optional; do not move in every Policy.",
            "- After a previous accepted Policy used only passive Look/Face/Hold, one short legal around_player reposition can be appropriate.",
            "- After recent movement, passive attention is appropriate.",
            "- Do not repeatedly output only look_at + hold_position while the companion remains clearly too far from the player.",
            "- A movement primitive may be followed by compatible look_at or hold_position if the duration budget permits.",
        ]
    )


def _build_s2_normal_movement_prompt_text():
    return "\n".join(
        [
            "S2 normal in-area reposition rule:",
            "- Applies only after strict S2 Entry is already satisfied.",
            "- Normal S2 repositioning uses scope=interaction_area and target=interaction_area.",
            "- interaction_area is a movement-only target in this rule.",
            "- Never use interaction_area as the target of look_at.",
            "- Never use interaction_area as the target of face_toward.",
            "- For look_at and face_toward, use only the normal allowed semantic target list.",
            "- It represents one small finite position change within the shared fire area.",
            "- It is not transition_only movement.",
            "- nearest_valid_interaction_area_point is transition-only and forbidden in normal AI output.",
            "- Do not output coordinates or NavMesh paths.",
            "- Do not move in every Policy, but regular short repositioning inside interaction_area is an important part of normal S2 behavior.",
            "- If the previous accepted normal Policy contained no move_within_scope, strongly prefer exactly one canonical interaction_area reposition in the current Policy before returning to look_at, face_toward, or hold_position.",
            '- Canonical S2 normal movement primitive: {"type":"move_within_scope","scope":"interaction_area","target":"interaction_area","duration":2.0}',
        ]
    )


def _build_s1_s2_anti_repetition_prompt_text(current_state, allowed_motion_primitives, allowed_scopes):
    has_move = PRIMITIVE_MOVE_WITHIN_SCOPE in list(allowed_motion_primitives or [])
    s1_can_reposition = current_state == STATE_S1 and SCOPE_AROUND_PLAYER in list(allowed_scopes or [])
    s2_can_reposition = current_state == STATE_S2 and SCOPE_INTERACTION_AREA in list(allowed_scopes or [])
    if not has_move or not (s1_can_reposition or s2_can_reposition):
        return ""

    return "\n".join(
        [
            "S1/S2 soft anti-repetition guidance:",
            "- Use the previous Policy memory summary to avoid repeating the exact same passive primitive pattern in consecutive Policies.",
            "- If the previous accepted Policy contained only look_at, face_toward, and/or hold_position, and movement is legal in the current State, prefer one short legal reposition.",
            "- This is a soft behavior preference, not a legality requirement; optional passive attention remains valid.",
            "- Do not move in every Policy.",
            "- After a movement Policy, returning to passive attention is appropriate.",
        ]
    )


def build_f2_movement_context(perception, policy_memory_context=None):
    """Build F2 Fishing in-area movement cadence guidance from existing memory."""

    memory_context = policy_memory_context if isinstance(policy_memory_context, dict) else {}
    if not isinstance(perception, dict):
        return {
            "companion_inside_fishing_interaction_area": False,
            "last_state": str(memory_context.get("last_state") or ""),
            "last_policy_had_movement": bool(memory_context.get("last_policy_had_movement")),
            "consecutive_passive_policy_count": 0,
            "movement_recommendation": "not_needed",
            "reason": "perception_missing",
        }

    entry_checks = perception.get("entry_checks") if isinstance(perception.get("entry_checks"), dict) else {}
    inside_fishing_area = bool(entry_checks.get("companion_inside_fishing_interaction_area"))
    last_state = str(memory_context.get("last_state") or "")
    last_policy_had_movement = bool(memory_context.get("last_policy_had_movement"))
    try:
        passive_streak = int(memory_context.get("consecutive_passive_policy_count") or 0)
    except (TypeError, ValueError):
        passive_streak = 0

    if not inside_fishing_area:
        recommendation = "not_needed"
        reason = "defer_to_transition_priority"
    elif last_state != STATE_F2:
        recommendation = "not_needed"
        reason = "no_recent_f2_normal_policy_history"
    elif last_policy_had_movement:
        recommendation = "not_needed"
        reason = "recent_movement_prevents_repeat"
    elif passive_streak >= 2:
        recommendation = "required"
        reason = "passive_streak_requires_f2_reposition"
    elif passive_streak == 1:
        recommendation = "recommended"
        reason = "recent_passive_policy_recommends_f2_reposition"
    else:
        recommendation = "not_needed"
        reason = "no_stable_f2_cadence_history"

    return {
        "companion_inside_fishing_interaction_area": inside_fishing_area,
        "last_state": last_state,
        "last_policy_had_movement": last_policy_had_movement,
        "consecutive_passive_policy_count": passive_streak,
        "movement_recommendation": recommendation,
        "reason": reason,
    }


def _format_f2_movement_context_lines(context):
    if not isinstance(context, dict):
        return []

    return [
        "F2 movement context:",
        f"- companion inside Fishing interaction area: {context.get('companion_inside_fishing_interaction_area')}",
        f"- previous accepted normal F2 policy contained movement: {context.get('last_policy_had_movement')}",
        f"- consecutive passive accepted normal policies: {context.get('consecutive_passive_policy_count', 0)}",
        f"- current movement recommendation: {context.get('movement_recommendation', 'unknown')}",
        f"- reason: {context.get('reason', 'unknown')}",
    ]


def _build_f2_normal_movement_prompt_text(context):
    recommendation = context.get("movement_recommendation", "unknown") if isinstance(context, dict) else "unknown"
    reason = context.get("reason", "unknown") if isinstance(context, dict) else "unknown"
    return "\n".join(
        [
            "F2 normal in-area reposition rule:",
            "- Applies only after strict F2 Entry is already satisfied.",
            "- Remain inside the Fishing interaction area.",
            f"- Current movement recommendation: {recommendation}.",
            f"- Current recommendation reason: {reason}.",
            "- Normal F2 repositioning uses scope=interaction_area and target=interaction_area.",
            "- interaction_area is the movement scope/semantic movement target.",
            "- Do not use interaction_area as a look_at target.",
            "- Do not use interaction_area as a face_toward target.",
            "- For gaze/facing use only the current allowed semantic target pool.",
            "- Never use around_player in F2.",
            "- Do not use transition_only in normal F2.",
            "- nearest_valid_fishing_interaction_area_point is transition-only and forbidden in normal AI output.",
            "- Do not output coordinates or NavMesh paths.",
            '- Canonical F2 normal movement primitive: {"type":"move_within_scope","scope":"interaction_area","target":"interaction_area","duration":2.0}',
            "- One reposition may be followed by a compatible look_at or hold_position if policy duration permits.",
            "- Do not move in every Policy.",
            "- Do not immediately repeat movement after a previous accepted movement.",
            "- If recommendation is required, include exactly one canonical move_within_scope interaction_area primitive.",
            "- If recommendation is required, do not return a passive-only Look/Face/Hold policy.",
            "- If recommendation is recommended, strongly prefer exactly one canonical F2 reposition.",
            "- If recommendation is recommended, passive-only output remains technically legal, but movement is the preferred behavior.",
            "- If recommendation is not_needed because of recent_movement_prevents_repeat, prefer passive Fishing attention and do not immediately reposition again.",
            "- If recommendation is not_needed because of defer_to_transition_priority, normal F2 movement guidance must not compete with deterministic Entry.",
            "- F2 does not perform perform_fishing_cast_cycle.",
        ]
    )


def _build_f3_normal_prompt_text(perception_summary=None, policy_memory_context=None):
    perception_summary = perception_summary if isinstance(perception_summary, dict) else {}
    policy_memory_context = policy_memory_context if isinstance(policy_memory_context, dict) else {}
    last_motion_types = [str(value) for value in list(policy_memory_context.get("last_motion_primitive_types") or [])]
    previous_policy_cast = PRIMITIVE_PERFORM_FISHING_CAST_CYCLE in last_motion_types
    f3_setup_ready = (
        bool(perception_summary.get("companion_at_valid_fishing_anchor"))
        and bool(perception_summary.get("fishing_rod_ready"))
        and str(perception_summary.get("current_hand_pose") or "").strip() == "fishing_ready"
    )
    cast_duration = f"{FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS:.1f}"

    lines = [
        "F3 anchor-locked rule:",
        "- The companion is already stationed at the assigned Fishing anchor when F3 Entry is complete.",
        "- Outside perform_fishing_cast_cycle, the companion remains in fishing_ready.",
        "- Normal movement is forbidden in F3.",
        "- perform_fishing_cast_cycle is the primary F3 parallel-engagement behavior when Fishing setup is complete.",
        "- perform_fishing_cast_cycle already owns cast, waiting, reel, and recovery; do not prepend redundant setup primitives just to fill the policy.",
        "- Canonical legal F3 cast-only policy example:",
        f'  {{"activity_mode":"parallel_engage","policy_lifetime":{cast_duration},"intent_summary":"Perform one finite companion Fishing participation cycle.","motion_primitives":[{{"type":"perform_fishing_cast_cycle","duration":{cast_duration}}}]}}',
        "- For an F3 policy containing perform_fishing_cast_cycle, the 20.0-second cast-cycle duration is state-appropriate and must be covered by policy_lifetime.",
        "- Do not split perform_fishing_cast_cycle into cast, wait, or reel primitives.",
        "- Do not output internal hand-pose tokens such as fishing_cast, fishing_hold, or fishing_reel.",
        "- Never output more than one perform_fishing_cast_cycle in a single policy.",
        "- Do not auto-loop casting across the policy.",
        "- Do not generate fish or alter player gameplay truth.",
        "F3 Fishing readiness facts from perception:",
        f"- companion_inside_fishing_interaction_area={bool(perception_summary.get('companion_inside_fishing_interaction_area'))}",
        f"- companion_at_valid_fishing_anchor={bool(perception_summary.get('companion_at_valid_fishing_anchor'))}",
        f"- fishing_rod_ready={bool(perception_summary.get('fishing_rod_ready'))}",
        f"- assigned_fishing_anchor_id={perception_summary.get('assigned_fishing_anchor_id') or ''}",
        f"- assigned_fishing_cast_target_id={perception_summary.get('assigned_fishing_cast_target_id') or ''}",
        f"- companion_fishing_rod_state={perception_summary.get('companion_fishing_rod_state') or ''}",
        f"- fishing_rod_runtime_available={bool(perception_summary.get('fishing_rod_runtime_available'))}",
        f"- current_hand_pose={perception_summary.get('current_hand_pose') or ''}",
    ]

    if f3_setup_ready:
        lines.extend(
            [
                "F3 setup is already complete in this request.",
                "- Do not emit set_companion_prop fishing_rod visible_in_hand merely to maintain an already-valid rod state.",
                "- Do not emit set_hand_pose fishing_ready merely to maintain an already-valid pose.",
                "- These setters are setup/recovery actions, not mandatory keep-alive actions.",
                "- Prefer actual F3 participation behavior.",
            ]
        )

    if previous_policy_cast:
        lines.extend(
            [
                "F3 cadence memory: the previous accepted Fishing policy already contained perform_fishing_cast_cycle.",
                "- Do not immediately cast again.",
                "- Prefer one short non-cast F3 policy using legal attention, facing, and/or hold behavior.",
                "- A later F3 policy may cast again after this non-cast follow-up.",
            ]
        )
    else:
        lines.extend(
            [
                "F3 cadence memory: the previous accepted Fishing policy did not contain perform_fishing_cast_cycle.",
                "- If F3 setup is complete, strongly prefer exactly one perform_fishing_cast_cycle.",
                f"- The clean {cast_duration}-second cast-only policy is the preferred default.",
                "- Avoid redundant already-satisfied rod or fishing_ready setters.",
            ]
        )

    return "\n".join(lines)


def _extract_recent_gaze_targets_for_state(current_state, allowed_targets, policy_memory_context):
    if current_state not in {STATE_S2, STATE_S3} or not isinstance(policy_memory_context, dict):
        return []

    if str(policy_memory_context.get("recent_gaze_state") or "") != current_state:
        return []

    allowed_target_set = {str(target) for target in list(allowed_targets or [])}
    recent_targets = []
    for target in list(policy_memory_context.get("recent_normal_gaze_targets") or []):
        normalized_target = str(target or "").strip()
        if normalized_target and normalized_target in allowed_target_set:
            recent_targets.append(normalized_target)
    return recent_targets[-3:]


def _build_recent_gaze_prompt_text(recent_targets):
    if not recent_targets:
        return ""

    return "\n".join(
        [
            "Recent normal gaze targets in this State:",
            " -> ".join(recent_targets),
            "Soft gaze continuity guidance:",
            "- Do not switch gaze targets merely to make the next Policy appear different.",
            "- Briefly continuing attention toward the same meaningful target is acceptable.",
            "- Avoid obvious A-B-A gaze-target oscillation when another legal behavior is contextually appropriate.",
            "- This is soft guidance; returning attention to an earlier target is allowed when current perception genuinely justifies it.",
            "- Use only currently allowed targets; do not invent a new target.",
            "- A Policy does not have to contain look_at or face_toward.",
            "- hold_position without an explicit gaze change remains legal when appropriate.",
            "- Do not force neutral_forward merely to increase variety.",
            "- Do not force companion_marshmallow unless it is already exposed by existing S3 capability and perception rules.",
        ]
    )


def _format_list(values):
    values = list(values or [])
    if not values:
        return "none"
    return ", ".join(str(value) for value in values)


def _format_definition_lines(title, definitions, allowed_keys):
    if not isinstance(definitions, dict):
        return []

    lines = [title]
    for key in allowed_keys:
        description = definitions.get(key)
        if description:
            lines.append(f"- {key}: {description}")

    return lines if len(lines) > 1 else []


def _format_policy_memory_summary(policy_memory_summary):
    if policy_memory_summary is None:
        return ""
    if isinstance(policy_memory_summary, str):
        return policy_memory_summary.strip()
    return _compact_json(policy_memory_summary)


def _infer_activity_identity(current_state, perception_summary=None, state_prompt=None):
    metadata = {}
    if isinstance(state_prompt, dict):
        metadata = state_prompt.get("state_metadata") if isinstance(state_prompt.get("state_metadata"), dict) else {}

    activity_id = str(metadata.get("activity_id", "") or "").strip()
    activity_phase = str(metadata.get("activity_phase", "") or "").strip()
    detail_phase = ""

    if isinstance(perception_summary, dict):
        detail_phase = str(perception_summary.get("activity_detail_phase", "") or "").strip()
        if not activity_id:
            activity_id = str(perception_summary.get("activity_id", "") or "").strip()
        if not activity_phase:
            activity_phase = str(perception_summary.get("activity_phase", "") or "").strip()

    if not activity_id:
        if current_state == STATE_S1:
            activity_id = ACTIVITY_ID_COMMON
        elif current_state in {STATE_F2, STATE_F3}:
            activity_id = ACTIVITY_ID_FISHING
        elif current_state in {STATE_S2, STATE_S3}:
            activity_id = ACTIVITY_ID_CAMPFIRE

    return activity_id or "unknown", activity_phase or "unknown", detail_phase or "none"


def _build_schema_example_text():
    return "\n".join(
        [
            "Follow this JSON shape:",
            "{",
            '  "activity_mode": "<one allowed activity mode>",',
            '  "policy_lifetime": 4.0,',
            '  "intent_summary": "short internal summary, not speech",',
            '  "motion_primitives": [',
            "    {",
            '      "type": "<one allowed primitive>",',
            '      "target": "<allowed semantic target if required>",',
            '      "duration": 1.0',
            "    }",
            "  ]",
            "}",
            "Duration rule:",
            "- The sum of duration values across all motion_primitives must be less than or equal to policy_lifetime.",
            "- Before returning JSON, verify that sum(motion_primitives[*].duration) <= policy_lifetime.",
            "- Valid example: policy_lifetime=4.0; durations=1.0+1.5+1.0; total=3.5; valid because 3.5 <= 4.0.",
            "- Invalid example: policy_lifetime=4.0; durations=2.0+2.0+1.0; total=5.0; invalid because 5.0 > 4.0.",
            "Primitive-specific optional fields:",
            "- move_within_scope: scope, target, radius_min, radius_max, duration",
            "Movement constraints:",
            "- Output semantic movement only; never output world coordinates or a NavMesh path.",
            "- transition_only is reserved for Python transition_priority responses and must never appear in normal AI policy output.",
            "- S3 normal policy must not output move_within_scope.",
        ]
    )


def _format_prop_state_lines(allowed_prop_states):
    if not isinstance(allowed_prop_states, dict) or not allowed_prop_states:
        return []

    lines = ["Allowed companion prop states for this state:"]
    for prop, states in allowed_prop_states.items():
        state_text = ", ".join(str(state) for state in list(states or []))
        if state_text:
            lines.append(f"- {prop}: {state_text}")
    return lines if len(lines) > 1 else []


def _should_include_s3_prop_invariant(current_state, allowed_motion_primitives, perception_summary):
    return (
        current_state == STATE_S3
        and PRIMITIVE_SET_COMPANION_PROP in list(allowed_motion_primitives or [])
        and bool((perception_summary or {}).get("companion_prop_runtime_available"))
    )


def _build_s3_prop_invariant_prompt_text(perception_summary):
    stick_state = str((perception_summary or {}).get("companion_stick_state") or "unknown")
    marshmallow_state = str((perception_summary or {}).get("companion_marshmallow_state") or "unknown")
    return "\n".join(
        [
            "S3 roasting prop invariant:",
            "- S3 is active roasting presentation: companion stick must remain visible_in_hand and companion marshmallow must remain visible_on_stick.",
            "- Do not hide either companion prop in S3.",
            f"- Request-time prop state: stick={stick_state}; marshmallow={marshmallow_state}.",
            "- Every executable S3 policy must begin with set_companion_prop stick visible_in_hand.",
            "- The second primitive must be set_companion_prop marshmallow visible_on_stick.",
            "- Do this even when request-time perception says the props are already visible; the request snapshot may be stale.",
            "- roast_pose and reaching the roasting anchor do not automatically display props.",
            "- A hold/look-only S3 policy is not valid as the full policy while this invariant is active.",
            "- Keep the policy short.",
        ]
    )


def build_policy_prompt(perception, current_state, allowed_pools, policy_memory_summary=None, policy_memory_context=None):
    """Build a compact, state-specific prompt for the AI policy model."""

    state_prompt = get_state_prompt_definitions(current_state, perception)
    normalized_allowed_activity_modes = list((allowed_pools or {}).get("activity_modes") or state_prompt["allowed_activity_modes"])
    normalized_allowed_motion_primitives = list((allowed_pools or {}).get("motion_primitives") or state_prompt["allowed_motion_primitives"])
    normalized_allowed_targets = list((allowed_pools or {}).get("allowed_targets") or state_prompt.get("allowed_targets") or [])
    forbidden_motion_primitives = list((allowed_pools or {}).get("forbidden_motion_primitives") or state_prompt.get("forbidden_motion_primitives") or [])
    normalized_allowed_scopes = list((allowed_pools or {}).get("allowed_scopes") or state_prompt.get("allowed_scopes") or [])
    normalized_allowed_poses = list((allowed_pools or {}).get("allowed_poses") or state_prompt.get("allowed_poses") or [])
    normalized_allowed_props = list((allowed_pools or {}).get("allowed_props") or state_prompt.get("allowed_props") or [])
    normalized_allowed_prop_states = dict((allowed_pools or {}).get("allowed_prop_states") or state_prompt.get("allowed_prop_states") or {})
    normalized_allowed_gestures = list((allowed_pools or {}).get("allowed_gestures") or state_prompt.get("allowed_gestures") or [])
    activity_mode_definitions = state_prompt.get("activity_mode_definitions") or {}
    motion_primitive_definitions = state_prompt.get("motion_primitive_definitions") or {}
    policy_notes = list(state_prompt.get("policy_notes") or [])
    forbidden_rules = list(state_prompt.get("forbidden_rules") or [])
    perception_summary = _build_perception_summary(perception)
    state_metadata = state_prompt.get("state_metadata") if isinstance(state_prompt.get("state_metadata"), dict) else {}
    current_activity_id, current_activity_phase, current_detail_phase = _infer_activity_identity(
        current_state,
        perception_summary,
        state_prompt,
    )
    s1_spatial_context = None
    if current_state == STATE_S1 and PRIMITIVE_MOVE_WITHIN_SCOPE in normalized_allowed_motion_primitives:
        s1_spatial_context = build_s1_spatial_movement_context(perception, policy_memory_context)
        perception_summary["spatial_movement_context"] = dict(s1_spatial_context)
    f2_movement_context = None
    if (
        current_state == STATE_F2
        and PRIMITIVE_MOVE_WITHIN_SCOPE in normalized_allowed_motion_primitives
        and SCOPE_INTERACTION_AREA in normalized_allowed_scopes
    ):
        f2_movement_context = build_f2_movement_context(perception, policy_memory_context)
        perception_summary["f2_movement_context"] = dict(f2_movement_context)
    prompt_perception_summary = {
        key: value
        for key, value in perception_summary.items()
        if not str(key).startswith("raw_unity_")
    }
    memory_summary_text = _format_policy_memory_summary(policy_memory_summary)
    recent_gaze_targets = _extract_recent_gaze_targets_for_state(
        current_state,
        normalized_allowed_targets,
        policy_memory_context,
    )

    system_prompt_lines = [
        "You are a silent embodied companion policy writer.",
        "Output JSON only.",
        "Do not output markdown, code fences, or natural language explanations.",
        "Do not speak.",
        "Do not control the player's Stick or Marshmallow.",
        "Do not modify Blackboard truth values.",
        "Do not include policy_source.",
        "Do not include execute_policy.",
        "Do not include response_meta, control, debug, state, allowed_pools, queue, transition_queue, or scheduler fields.",
        "Do not include speech, dialogue, message, or text-to-player fields.",
        "Do not output coordinates, bones, or frame-level animation instructions.",
        "Do not output world positions, path corners, or any explicit NavMesh path.",
        "Return a JSON object with keys: activity_mode, policy_lifetime, intent_summary, motion_primitives.",
    ]

    system_prompt_lines.append(f"Current activity: {current_activity_id}.")
    system_prompt_lines.append(f"Current authoritative state: {current_state}.")
    system_prompt_lines.append(f"Current activity phase: {current_activity_phase}.")
    system_prompt_lines.append(f"Current detail phase: {current_detail_phase}.")
    system_prompt_lines.append(f"State description: {state_prompt.get('description', '')}")
    if current_activity_id == ACTIVITY_ID_FISHING:
        system_prompt_lines.append("Fishing reminder: the player owns Fishing gameplay truth; do not control the player's rod, line, fish, or catch count.")
        system_prompt_lines.append("Fishing no-loop rule: do not invent repeated cast loops, repeated wait loops, or self-triggered gameplay cycles.")
    system_prompt_lines.append(
        f"Allowed activity modes for this state: {_format_list(normalized_allowed_activity_modes)}."
    )
    system_prompt_lines.append(
        f"Allowed motion primitives for this state: {_format_list(normalized_allowed_motion_primitives)}."
    )
    system_prompt_lines.append(
        f"Allowed semantic targets for this state: {_format_list(normalized_allowed_targets)}."
    )
    system_prompt_lines.append(
        f"Allowed scopes for move_within_scope: {_format_list(normalized_allowed_scopes)}."
    )
    system_prompt_lines.append("Use only the listed normal semantic targets for look_at and face_toward. For move_within_scope, use only the explicitly documented scope-specific target format. Do not invent scope, target, pose, prop, or gesture tokens.")
    system_prompt_lines.append(
        f"Allowed hand poses: {_format_list(normalized_allowed_poses)}."
    )
    system_prompt_lines.append(
        f"Allowed companion props: {_format_list(normalized_allowed_props)}."
    )
    system_prompt_lines.extend(_format_prop_state_lines(normalized_allowed_prop_states))
    system_prompt_lines.append(
        f"Allowed gestures: {_format_list(normalized_allowed_gestures)}."
    )
    if (
        "idle_motion" not in normalized_allowed_motion_primitives
        and "set_hand_pose" not in normalized_allowed_motion_primitives
        and "set_companion_prop" not in normalized_allowed_motion_primitives
        and "play_gesture" not in normalized_allowed_motion_primitives
    ):
        system_prompt_lines.append(
            "Stage 5B-1 deferred primitives remain unavailable here: do not output idle_motion, set_hand_pose, set_companion_prop, or play_gesture."
        )
    else:
        if "set_hand_pose" in normalized_allowed_motion_primitives:
            system_prompt_lines.append("set_hand_pose changes only the companion's coarse right-hand pose. Use only allowed pose tokens.")
        if "set_companion_prop" in normalized_allowed_motion_primitives:
            system_prompt_lines.append("set_companion_prop controls only pre-existing companion-owned visual props. Never control player-owned props.")
            system_prompt_lines.append("If you request marshmallow visible_on_stick, ensure the stick is already visible earlier in the same policy or already confirmed visible in perception.")
        if "idle_motion" not in normalized_allowed_motion_primitives:
            system_prompt_lines.append("idle_motion remains unavailable.")
        if "play_gesture" not in normalized_allowed_motion_primitives:
            system_prompt_lines.append("play_gesture remains unavailable.")
    if forbidden_motion_primitives:
        system_prompt_lines.append(
            f"Forbidden motion primitives for this state: {_format_list(forbidden_motion_primitives)}."
        )
    if policy_notes:
        system_prompt_lines.append(f"Policy notes: {_format_list(policy_notes)}.")
    if normalized_allowed_props:
        system_prompt_lines.append(
            "For set_companion_prop, copy prop and state values exactly from the allowed prop-state table. Do not paraphrase, shorten, rename, or invent prop state values."
        )
        if "stick" in normalized_allowed_props:
            if current_state == "S2_fire_ready_companion":
                system_prompt_lines.append("In this state, the only allowed prop is stick. For prop=\"stick\", state must be exactly one of: visible_in_hand, hidden. Do not use marshmallow.")
            elif current_state == "S3_roasting_engagement":
                system_prompt_lines.append("For prop=\"stick\", state must be exactly one of: visible_in_hand.")
                if "marshmallow" in normalized_allowed_props:
                    system_prompt_lines.append("For prop=\"marshmallow\", state must be exactly one of: visible_on_stick.")
            else:
                system_prompt_lines.append("Do not invent alternative state names.")
    else:
        system_prompt_lines.append("set_companion_prop is not allowed in this state.")

    system_prompt_lines.extend(
        _format_definition_lines(
            "Allowed activity mode definitions:",
            activity_mode_definitions,
            normalized_allowed_activity_modes,
        )
    )
    system_prompt_lines.extend(
        _format_definition_lines(
            "Allowed motion primitive definitions:",
            motion_primitive_definitions,
            normalized_allowed_motion_primitives,
        )
    )
    if forbidden_rules:
        system_prompt_lines.append("State-specific forbidden rules:")
        system_prompt_lines.extend([f"- {rule}" for rule in forbidden_rules])

    if s1_spatial_context is not None and SCOPE_AROUND_PLAYER in normalized_allowed_scopes:
        system_prompt_lines.append(_build_s1_movement_prompt_text(s1_spatial_context))

    if (
        current_state == STATE_F2
        and PRIMITIVE_MOVE_WITHIN_SCOPE in normalized_allowed_motion_primitives
        and SCOPE_INTERACTION_AREA in normalized_allowed_scopes
    ):
        system_prompt_lines.append(_build_f2_normal_movement_prompt_text(f2_movement_context))

    if (
        current_state == STATE_S2
        and PRIMITIVE_MOVE_WITHIN_SCOPE in normalized_allowed_motion_primitives
        and SCOPE_INTERACTION_AREA in normalized_allowed_scopes
    ):
        system_prompt_lines.append(_build_s2_normal_movement_prompt_text())

    anti_repetition_text = _build_s1_s2_anti_repetition_prompt_text(
        current_state,
        normalized_allowed_motion_primitives,
        normalized_allowed_scopes,
    )
    if anti_repetition_text:
        system_prompt_lines.append(anti_repetition_text)

    recent_gaze_text = _build_recent_gaze_prompt_text(recent_gaze_targets)
    if recent_gaze_text:
        system_prompt_lines.append(recent_gaze_text)

    if _should_include_s3_prop_invariant(current_state, normalized_allowed_motion_primitives, perception_summary):
        system_prompt_lines.append(_build_s3_prop_invariant_prompt_text(perception_summary))

    if current_state == STATE_F3 and PRIMITIVE_PERFORM_FISHING_CAST_CYCLE in normalized_allowed_motion_primitives:
        system_prompt_lines.append(_build_f3_normal_prompt_text(perception_summary, policy_memory_context))

    system_prompt_lines.append(_build_schema_example_text())
    if normalized_allowed_props and "set_companion_prop" in normalized_allowed_motion_primitives:
        if current_state == "S2_fire_ready_companion":
            system_prompt_lines.append(
                'Example: {"type":"set_companion_prop","prop":"stick","state":"visible_in_hand","duration":0.5}'
            )
        elif current_state == "S3_roasting_engagement":
            system_prompt_lines.append(
                'Example: {"type":"set_companion_prop","prop":"stick","state":"visible_in_hand","duration":0.5}'
            )
            system_prompt_lines.append(
                'Example: {"type":"set_companion_prop","prop":"marshmallow","state":"visible_on_stick","duration":0.5}'
            )

    user_prompt_lines = [
        "Use the current perception and the state's allowed pools to produce one JSON policy object.",
        f"Perception summary: {_compact_json(prompt_perception_summary)}",
    ]

    if s1_spatial_context is not None:
        user_prompt_lines.extend(_format_s1_spatial_context_lines(s1_spatial_context))
    if f2_movement_context is not None:
        user_prompt_lines.extend(_format_f2_movement_context_lines(f2_movement_context))

    if memory_summary_text:
        user_prompt_lines.append(f"Policy memory summary: {memory_summary_text}")

    user_prompt_lines.extend(
        [
            "The policy should stay short and state-appropriate.",
            "For look_at and face_toward, use only the allowed semantic targets listed for the current State. For move_within_scope, follow the scope-specific movement target rules stated above.",
            "Do not output any extra text outside the JSON object.",
        ]
    )

    system_prompt = "\n".join(system_prompt_lines)
    user_prompt = "\n".join(user_prompt_lines)
    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]

    return {
        "current_state": current_state,
        "state_prompt": state_prompt,
        "allowed_pools": {
            "activity_modes": normalized_allowed_activity_modes,
            "motion_primitives": normalized_allowed_motion_primitives,
            "allowed_targets": normalized_allowed_targets,
            "forbidden_motion_primitives": forbidden_motion_primitives,
            "allowed_scopes": normalized_allowed_scopes,
            "allowed_poses": normalized_allowed_poses,
            "allowed_props": normalized_allowed_props,
            "allowed_prop_states": normalized_allowed_prop_states,
            "allowed_gestures": normalized_allowed_gestures,
        },
        "perception_summary": perception_summary,
        "policy_memory_summary": memory_summary_text,
        "system_prompt": system_prompt,
        "user_prompt": user_prompt,
        "messages": messages,
        "prompt_text": f"{system_prompt}\n\n{user_prompt}",
    }
