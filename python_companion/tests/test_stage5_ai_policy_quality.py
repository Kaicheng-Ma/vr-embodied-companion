import copy
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from python_companion import config
from python_companion.ai_policy_client import generate_ai_policy
from python_companion.models import (
    ACTIVITY_AMBIENT_EXPLORE,
    AI_ERROR_VALIDATION_FAILED,
    POLICY_SOURCE_AI,
    POLICY_SOURCE_FALLBACK,
    POLICY_SOURCE_POLICY_ERROR,
    POLICY_SOURCE_TRANSITION_PRIORITY,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
    PRIMITIVE_SET_COMPANION_PROP,
    PRIMITIVE_SET_HAND_POSE,
    PROP_STATE_HIDDEN,
    PROP_STATE_VISIBLE_IN_HAND,
    PROP_STATE_VISIBLE_ON_STICK,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    SCOPE_TRANSITION_ONLY,
    STATE_S1,
    STATE_S2,
    STATE_F2,
    STATE_F3,
    STATE_S3,
    TARGET_CAMPFIRE_CENTER,
    TARGET_COMPANION_MARSHMALLOW,
    TARGET_INTERACTION_AREA,
    TARGET_NEUTRAL_FORWARD,
    TARGET_PLAYER_HEAD,
    TARGET_WATER_FOCUS,
)
from python_companion.policy_layer import generate_policy_response
from python_companion.policy_memory import PolicyMemory
from python_companion.policy_validator import validate_policy_dict
from python_companion.prompt_builder import build_f2_movement_context, build_policy_prompt, build_s1_spatial_movement_context
from python_companion.response_builder import build_fallback_response, build_policy_error_response
from python_companion.state_config import (
    S1_AROUND_PLAYER_RADIUS_MAX,
    S1_AROUND_PLAYER_RADIUS_MIN,
    build_allowed_pools,
    get_state_prompt_definitions,
)
from python_companion.state_layer import StateResolution
from python_companion.transition_queue import TransitionQueue


def _state_resolution(current_state):
    return StateResolution(
        current_state=current_state,
        internal_state=current_state,
        activity_id="test",
        previous_state=current_state,
        state_changed=False,
        state_reason="test",
    )


def _s1_perception(player_x=0.0, player_z=0.0, companion_x=4.2, companion_z=0.0):
    return {
        "frame": {"request_id": "test_request", "request_role": "current_policy", "current_state_epoch": 1, "policy_index": 1},
        "task": {"campfire_started": False, "stick_picked": False},
        "control": {"companion_enabled": True, "shutdown_signal": False, "ai_enabled": True, "fallback_enabled": True},
        "player": {
            "has_player_root": True,
            "has_player_head": True,
            "position": {"x": player_x, "y": 0.0, "z": player_z},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
        },
        "companion": {
            "current_state": STATE_S1,
            "previous_state": "unknown",
            "state_changed": False,
            "state_reason": "test",
            "position": {"x": companion_x, "y": 0.0, "z": companion_z},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
            "hand_pose_runtime_available": False,
            "companion_prop_runtime_available": False,
        },
        "scene": {"campfire_center_available": False, "look_targets_available": {TARGET_PLAYER_HEAD: True, "neutral_forward": True}},
        "entry_checks": {"companion_inside_interaction_area": False, "companion_at_valid_roasting_anchor": False},
        "events": {"state_change_id": 1, "recent_events": []},
    }


def _s2_perception():
    return {
        "frame": {"request_id": "test_request", "request_role": "current_policy", "current_state_epoch": 2, "policy_index": 1},
        "task": {"campfire_started": True, "stick_picked": False},
        "control": {"companion_enabled": True, "shutdown_signal": False, "ai_enabled": True, "fallback_enabled": True},
        "player": {
            "has_player_root": True,
            "has_player_head": True,
            "position": {"x": 0.0, "y": 0.0, "z": 0.0},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
        },
        "companion": {
            "current_state": STATE_S2,
            "previous_state": STATE_S1,
            "state_changed": False,
            "state_reason": "test",
            "position": {"x": 1.5, "y": 0.0, "z": 1.0},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
            "hand_pose_runtime_available": False,
            "companion_prop_runtime_available": False,
        },
        "scene": {"campfire_center_available": True, "look_targets_available": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, "neutral_forward"]},
        "entry_checks": {"companion_inside_interaction_area": True, "companion_at_valid_roasting_anchor": False},
        "events": {"state_change_id": 2, "recent_events": []},
    }


def _f2_perception(*, inside_fishing_area=True):
    return {
        "frame": {"request_id": "test_f2_request", "request_role": "current_policy", "current_state_epoch": 4, "policy_index": 3},
        "task": {"campfire_started": False, "stick_picked": False},
        "control": {"companion_enabled": True, "shutdown_signal": False, "ai_enabled": True, "fallback_enabled": True},
        "activity": {
            "id": "fishing",
            "phase": "ready",
            "detail_phase": "BucketReady",
            "fishing_completed": False,
            "context_key": "fishing/ready/BucketReady",
            "context_epoch": 4,
        },
        "player": {
            "has_player_root": True,
            "has_player_head": True,
            "position": {"x": 0.0, "y": 0.0, "z": 0.0},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
        },
        "companion": {
            "current_state": STATE_F2,
            "previous_state": STATE_F2,
            "state_changed": False,
            "state_reason": "test",
            "position": {"x": 1.5, "y": 0.0, "z": 1.0},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
            "hand_pose_runtime_available": True,
            "companion_prop_runtime_available": True,
            "current_hand_pose": "fishing_ready",
            "companion_fishing_rod_state": PROP_STATE_VISIBLE_IN_HAND,
            "fishing_rod_runtime_available": True,
        },
        "scene": {"campfire_center_available": False, "look_targets_available": [TARGET_PLAYER_HEAD, TARGET_WATER_FOCUS, TARGET_NEUTRAL_FORWARD]},
        "entry_checks": {
            "companion_inside_interaction_area": False,
            "companion_at_valid_roasting_anchor": False,
            "companion_inside_fishing_interaction_area": inside_fishing_area,
            "companion_at_valid_fishing_anchor": False,
            "fishing_rod_ready": True,
        },
        "events": {"state_change_id": 4, "recent_events": []},
    }


def _s3_perception(
    *,
    prop_runtime_available=True,
    stick_state=PROP_STATE_HIDDEN,
    marshmallow_state=PROP_STATE_HIDDEN,
    hand_runtime_available=True,
    hand_pose="roast_pose",
):
    return {
        "frame": {"request_id": "test_request", "request_role": "current_policy", "current_state_epoch": 3, "policy_index": 2},
        "task": {"campfire_started": True, "stick_picked": True},
        "control": {"companion_enabled": True, "shutdown_signal": False, "ai_enabled": True, "fallback_enabled": True},
        "player": {
            "has_player_root": True,
            "has_player_head": True,
            "position": {"x": 0.0, "y": 0.0, "z": 0.0},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
        },
        "companion": {
            "current_state": STATE_S3,
            "previous_state": STATE_S3,
            "state_changed": False,
            "state_reason": "test",
            "position": {"x": 1.5, "y": 0.0, "z": 1.0},
            "forward": {"x": 0.0, "y": 0.0, "z": 1.0},
            "hand_pose_runtime_available": hand_runtime_available,
            "companion_prop_runtime_available": prop_runtime_available,
            "current_hand_pose": hand_pose,
            "companion_stick_state": stick_state,
            "companion_marshmallow_state": marshmallow_state,
            "has_companion_marshmallow": marshmallow_state == PROP_STATE_VISIBLE_ON_STICK,
        },
        "scene": {"campfire_center_available": True, "look_targets_available": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, "neutral_forward"]},
        "entry_checks": {"companion_inside_interaction_area": True, "companion_at_valid_roasting_anchor": True},
        "events": {"state_change_id": 3, "recent_events": []},
    }


def _policy_response(policy_source, primitives, state=STATE_S1, execute_policy=None):
    if execute_policy is None:
        execute_policy = policy_source != POLICY_SOURCE_POLICY_ERROR
    return {
        "control": {"execute_policy": execute_policy},
        "state": {"current_state": state},
        "policy": {"policy_source": policy_source, "activity_mode": "idle_presence"},
        "motion_primitives": primitives,
        "debug": {},
    }


def _provider_config():
    return SimpleNamespace(
        force_fallback_for_test=False,
        ai_enabled=True,
        provider_name="qwen",
        model="test-model",
    )


def _provider_success(policy):
    return {
        "success": True,
        "raw_text": json.dumps(policy),
        "parsed_json_text": json.dumps(policy),
        "latency_ms": 1,
    }


def _canonical_movement_policy():
    return {
        "activity_mode": "idle_presence",
        "policy_lifetime": 4.0,
        "intent_summary": "move near player",
        "motion_primitives": [
            {
                "type": PRIMITIVE_MOVE_WITHIN_SCOPE,
                "scope": SCOPE_AROUND_PLAYER,
                "target": "",
                "radius_min": 1.2,
                "radius_max": 2.5,
                "duration": 4.0,
            }
        ],
    }


def _passive_policy():
    return {
        "activity_mode": "idle_presence",
        "policy_lifetime": 4.0,
        "intent_summary": "look and hold",
        "motion_primitives": [
            {"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0},
            {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0},
        ],
    }


def _s3_passive_policy():
    return {
        "activity_mode": "parallel_engage",
        "policy_lifetime": 4.0,
        "intent_summary": "hold and look",
        "motion_primitives": [
            {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0},
            {"type": PRIMITIVE_LOOK_AT, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0},
        ],
    }


def _s3_prop_policy(primitives):
    return {
        "activity_mode": "parallel_engage",
        "policy_lifetime": 4.0,
        "intent_summary": "set roasting props",
        "motion_primitives": primitives,
    }


class Stage5AiPolicyQualityTests(unittest.TestCase):
    def test_s1_prompt_uses_canonical_around_player_format_without_coordinates(self):
        prompt = build_policy_prompt(_s1_perception(), STATE_S1, build_allowed_pools(STATE_S1))
        text = prompt["prompt_text"]

        self.assertIn('"type":"move_within_scope","scope":"around_player","target":"","radius_min":1.2,"radius_max":2.5,"duration":4.0', text)
        self.assertIn("movement recommendation: required", text)
        self.assertIn("Do not output coordinates", text)
        self.assertNotIn('"x":', text)
        self.assertNotIn('"z":', text)
        self.assertNotIn("follow_player", text.split("Canonical S1 movement example:", 1)[0])

    def test_far_distance_prompt_recommends_required_movement(self):
        prompt = build_policy_prompt(_s1_perception(companion_x=4.2), STATE_S1, build_allowed_pools(STATE_S1))
        text = prompt["prompt_text"]

        self.assertIn("player-companion XZ distance: 4.20 m", text)
        self.assertIn("companion is outside the nearby band: True", text)
        self.assertIn("movement recommendation: required", text)
        self.assertIn("Do not repeatedly output only look_at + hold_position while the companion remains clearly too far", text)

    def test_far_distance_without_recent_movement_context_remains_required(self):
        context = build_s1_spatial_movement_context(
            _s1_perception(companion_x=4.2),
            {"last_policy_had_movement": False},
        )

        self.assertEqual(context["movement_recommendation"], "required")
        self.assertEqual(context["reason"], "companion_clearly_farther_than_preferred_band")

    def test_far_distance_with_recent_movement_context_downgrades_to_recommended(self):
        context = build_s1_spatial_movement_context(
            _s1_perception(companion_x=4.2),
            {"last_policy_had_movement": True},
        )

        self.assertEqual(context["movement_recommendation"], "recommended")
        self.assertEqual(context["reason"], "recent_movement_policy_may_be_executing_or_pending")

    def test_near_distance_prompt_does_not_force_variety_movement(self):
        prompt = build_policy_prompt(_s1_perception(companion_x=1.6), STATE_S1, build_allowed_pools(STATE_S1))
        text = prompt["prompt_text"]

        self.assertIn("movement recommendation: not_needed", text)
        self.assertIn("movement is optional; do not move in every Policy", text)
        self.assertIn("previous accepted Policy used only passive Look/Face/Hold", text)
        self.assertIn("After recent movement, passive attention is appropriate", text)
        self.assertNotIn("do not move only for variety", text)

    def test_s1_s2_prompt_bias_configuration_is_visible_without_runtime_mechanisms(self):
        s1_pools = build_allowed_pools(STATE_S1)
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, s1_pools["motion_primitives"])
        self.assertIn(SCOPE_AROUND_PLAYER, s1_pools["allowed_scopes"])
        self.assertNotIn(TARGET_INTERACTION_AREA, s1_pools["allowed_targets"])

        s2_pools = build_allowed_pools(STATE_S2)
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, s2_pools["motion_primitives"])
        self.assertIn(SCOPE_INTERACTION_AREA, s2_pools["allowed_scopes"])
        self.assertNotIn(TARGET_INTERACTION_AREA, s2_pools["allowed_targets"])
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, s2_pools["motion_primitives"])
        self.assertEqual(s2_pools["allowed_poses"], [])

        s2_prompt = build_policy_prompt(_s2_perception(), STATE_S2, s2_pools)
        s2_text = s2_prompt["prompt_text"]
        s2_target_line = next(line for line in s2_text.splitlines() if line.startswith("Allowed semantic targets for this state:"))

        self.assertEqual("Allowed semantic targets for this state: campfire_center, player_head, neutral_forward.", s2_target_line)
        self.assertIn("S2 normal in-area reposition rule:", s2_text)
        self.assertIn("scope=interaction_area and target=interaction_area", s2_text)
        self.assertIn("interaction_area is a movement-only target", s2_text)
        self.assertIn("Never use interaction_area as the target of look_at", s2_text)
        self.assertIn("Never use interaction_area as the target of face_toward", s2_text)
        self.assertIn('{"type":"move_within_scope","scope":"interaction_area","target":"interaction_area","duration":2.0}', s2_text)
        self.assertIn("It is not transition_only movement", s2_text)
        self.assertIn("nearest_valid_interaction_area_point is transition-only and forbidden in normal AI output", s2_text)
        self.assertIn("prefer keeping the companion's own stick visible_in_hand during normal S2 activity and in-area reposition", s2_text)
        self.assertIn("Do not repeatedly hide/show the companion stick merely for behavioral variety", s2_text)

    def test_f2_movement_context_uses_existing_memory_cadence(self):
        entry_incomplete = build_f2_movement_context(
            _f2_perception(inside_fishing_area=False),
            {"last_state": STATE_F2, "last_policy_had_movement": False, "consecutive_passive_policy_count": 3},
        )
        self.assertEqual(entry_incomplete["movement_recommendation"], "not_needed")
        self.assertEqual(entry_incomplete["reason"], "defer_to_transition_priority")

        recent_movement = build_f2_movement_context(
            _f2_perception(),
            {"last_state": STATE_F2, "last_policy_had_movement": True, "consecutive_passive_policy_count": 0},
        )
        self.assertEqual(recent_movement["movement_recommendation"], "not_needed")
        self.assertEqual(recent_movement["reason"], "recent_movement_prevents_repeat")

        one_passive = build_f2_movement_context(
            _f2_perception(),
            {"last_state": STATE_F2, "last_policy_had_movement": False, "consecutive_passive_policy_count": 1},
        )
        self.assertEqual(one_passive["movement_recommendation"], "recommended")
        self.assertEqual(one_passive["reason"], "recent_passive_policy_recommends_f2_reposition")

        two_passive = build_f2_movement_context(
            _f2_perception(),
            {"last_state": STATE_F2, "last_policy_had_movement": False, "consecutive_passive_policy_count": 2},
        )
        self.assertEqual(two_passive["movement_recommendation"], "required")
        self.assertEqual(two_passive["reason"], "passive_streak_requires_f2_reposition")

    def test_f2_prompt_exposes_required_movement_cadence_without_runtime_gate(self):
        perception = _f2_perception()
        f2_pools = build_allowed_pools(STATE_F2, perception)
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, f2_pools["motion_primitives"])
        self.assertIn(SCOPE_INTERACTION_AREA, f2_pools["allowed_scopes"])
        self.assertNotIn(TARGET_INTERACTION_AREA, f2_pools["allowed_targets"])
        self.assertIn(PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, f2_pools["forbidden_motion_primitives"])

        canonical_movement = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 3.0,
                "intent_summary": "F2 canonical in-area reposition.",
                "motion_primitives": [
                    {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_INTERACTION_AREA, "target": TARGET_INTERACTION_AREA, "duration": 2.0},
                    {"type": PRIMITIVE_HOLD_POSITION, "target": "", "duration": 0.5},
                ],
            },
            STATE_F2,
            f2_pools,
        )
        self.assertTrue(canonical_movement["valid"], msg=canonical_movement["errors"])

        prompt = build_policy_prompt(
            perception,
            STATE_F2,
            f2_pools,
            policy_memory_context={"last_state": STATE_F2, "last_policy_had_movement": False, "consecutive_passive_policy_count": 2},
        )
        text = prompt["prompt_text"]

        self.assertIn("F2 movement context:", text)
        self.assertIn("companion inside Fishing interaction area: True", text)
        self.assertIn("current movement recommendation: required", text)
        self.assertIn("reason: passive_streak_requires_f2_reposition", text)
        self.assertIn('{"type":"move_within_scope","scope":"interaction_area","target":"interaction_area","duration":2.0}', text)
        self.assertIn("include exactly one canonical move_within_scope interaction_area primitive", text)
        self.assertIn("do not return a passive-only Look/Face/Hold policy", text)
        self.assertIn("interaction_area is the movement scope/semantic movement target", text)
        self.assertIn("Do not use interaction_area as a look_at target", text)
        self.assertIn("Do not use interaction_area as a face_toward target", text)
        self.assertIn("For gaze/facing use only the current allowed semantic target pool", text)
        self.assertIn("Never use around_player in F2", text)
        self.assertIn("Do not use transition_only in normal F2", text)
        self.assertIn("nearest_valid_fishing_interaction_area_point is transition-only and forbidden in normal AI output", text)
        self.assertIn("F2 does not perform perform_fishing_cast_cycle", text)
        self.assertEqual(prompt["perception_summary"]["f2_movement_context"]["movement_recommendation"], "required")

    def test_f2_prompt_handles_recent_movement_and_passive_recommendation(self):
        perception = _f2_perception()
        f2_pools = build_allowed_pools(STATE_F2, perception)

        recent_movement_text = build_policy_prompt(
            perception,
            STATE_F2,
            f2_pools,
            policy_memory_context={"last_state": STATE_F2, "last_policy_had_movement": True, "consecutive_passive_policy_count": 0},
        )["prompt_text"]
        self.assertIn("current movement recommendation: not_needed", recent_movement_text)
        self.assertIn("reason: recent_movement_prevents_repeat", recent_movement_text)
        self.assertIn("prefer passive Fishing attention and do not immediately reposition again", recent_movement_text)

        one_passive_text = build_policy_prompt(
            perception,
            STATE_F2,
            f2_pools,
            policy_memory_context={"last_state": STATE_F2, "last_policy_had_movement": False, "consecutive_passive_policy_count": 1},
        )["prompt_text"]
        self.assertIn("current movement recommendation: recommended", one_passive_text)
        self.assertIn("reason: recent_passive_policy_recommends_f2_reposition", one_passive_text)
        self.assertIn("strongly prefer exactly one canonical F2 reposition", one_passive_text)

    def test_f2_entry_incomplete_does_not_compete_with_transition_priority(self):
        prompt = build_policy_prompt(
            _f2_perception(inside_fishing_area=False),
            STATE_F2,
            build_allowed_pools(STATE_F2, _f2_perception(inside_fishing_area=False)),
            policy_memory_context={"last_state": STATE_F2, "last_policy_had_movement": False, "consecutive_passive_policy_count": 2},
        )
        self.assertEqual(prompt["perception_summary"]["f2_movement_context"]["movement_recommendation"], "not_needed")
        self.assertEqual(prompt["perception_summary"]["f2_movement_context"]["reason"], "defer_to_transition_priority")
        self.assertIn("normal F2 movement guidance must not compete with deterministic Entry", prompt["prompt_text"])

    def test_f2_movement_cadence_text_does_not_leak_to_other_states(self):
        s1_text = build_policy_prompt(_s1_perception(companion_x=1.6), STATE_S1, build_allowed_pools(STATE_S1))["prompt_text"]
        s2_text = build_policy_prompt(_s2_perception(), STATE_S2, build_allowed_pools(STATE_S2))["prompt_text"]
        s3_text = build_policy_prompt(_s3_perception(), STATE_S3, build_allowed_pools(STATE_S3, _s3_perception()))["prompt_text"]
        f3_text = build_policy_prompt(_f2_perception(), STATE_F3, build_allowed_pools(STATE_F3, _f2_perception()))["prompt_text"]

        for text in (s1_text, s2_text, s3_text, f3_text):
            self.assertNotIn("F2 movement context:", text)
            self.assertNotIn("passive_streak_requires_f2_reposition", text)
            self.assertNotIn("F2 normal in-area reposition rule:", text)

        self.assertIn("S2 normal in-area reposition rule:", s2_text)
        self.assertIn("F3 anchor-locked rule:", f3_text)
        self.assertIn("20.0-second cast-cycle duration", f3_text)

    def test_s1_s2_soft_anti_repetition_guidance_does_not_apply_to_s3(self):
        s1_text = build_policy_prompt(
            _s1_perception(companion_x=1.6),
            STATE_S1,
            build_allowed_pools(STATE_S1),
            policy_memory_summary={"last_accepted_primitive_types": [PRIMITIVE_LOOK_AT, PRIMITIVE_HOLD_POSITION]},
        )["prompt_text"]
        s2_text = build_policy_prompt(
            _s2_perception(),
            STATE_S2,
            build_allowed_pools(STATE_S2),
            policy_memory_summary={"last_accepted_primitive_types": [PRIMITIVE_LOOK_AT, PRIMITIVE_HOLD_POSITION]},
        )["prompt_text"]
        s3_pools = build_allowed_pools(STATE_S3, _s3_perception())
        s3_text = build_policy_prompt(_s3_perception(), STATE_S3, s3_pools)["prompt_text"]

        for text in (s1_text, s2_text):
            self.assertIn("previous Policy memory summary", text)
            self.assertIn("avoid repeating the exact same passive primitive pattern", text)
            self.assertIn("look_at, face_toward, and/or hold_position", text)
            self.assertIn("prefer one short legal reposition", text)
            self.assertIn("soft behavior preference, not a legality requirement", text)
            self.assertIn("Do not move in every Policy", text)
            self.assertIn("After a movement Policy, returning to passive attention is appropriate", text)

        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, s3_pools["motion_primitives"])
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, s3_pools["forbidden_motion_primitives"])
        self.assertNotIn(TARGET_INTERACTION_AREA, s3_pools["allowed_targets"])
        self.assertNotIn("S1/S2 soft anti-repetition guidance", s3_text)
        self.assertNotIn("S2 normal in-area reposition rule", s3_text)

    def test_activity_mode_definitions_include_free_movement_bias(self):
        s1_definitions = get_state_prompt_definitions(STATE_S1)["activity_mode_definitions"]
        s2_definitions = get_state_prompt_definitions(STATE_S2)["activity_mode_definitions"]

        self.assertEqual(
            s1_definitions[ACTIVITY_AMBIENT_EXPLORE],
            "Lightly observe the environment or make one short reposition within the allowed around_player scope while remaining calm and near the player.",
        )
        self.assertEqual(
            s2_definitions[ACTIVITY_AMBIENT_EXPLORE],
            "Lightly observe the fire area or make one short reposition inside interaction_area while staying within bounds.",
        )

    def test_missing_position_prompt_uses_unknown_without_fabricating_distance(self):
        perception = _s1_perception()
        del perception["player"]["position"]
        prompt = build_policy_prompt(perception, STATE_S1, build_allowed_pools(STATE_S1))

        self.assertIn("movement recommendation: unknown", prompt["prompt_text"])
        self.assertIn("player-companion XZ distance: unknown", prompt["prompt_text"])
        self.assertIsNone(prompt["perception_summary"]["spatial_movement_context"]["player_companion_distance_xz"])

    def test_player_displacement_memory_is_computed_without_exposing_coordinates(self):
        memory_context = {
            "last_player_position_xz": {"x": 0.0, "z": 0.0},
            "last_companion_position_xz": {"x": 1.5, "z": 0.0},
            "last_policy_had_movement": False,
            "consecutive_passive_policy_count": 1,
        }
        perception = _s1_perception(player_x=0.8, companion_x=2.0)
        context = build_s1_spatial_movement_context(perception, memory_context)
        prompt = build_policy_prompt(
            perception,
            STATE_S1,
            build_allowed_pools(STATE_S1),
            policy_memory_context=memory_context,
        )

        self.assertEqual(context["player_displacement_since_previous_normal_policy_xz"], 0.8)
        self.assertIn("player displacement since previous accepted normal policy: 0.80 m", prompt["prompt_text"])
        self.assertNotIn('"x":0.0', prompt["prompt_text"])
        self.assertNotIn('"z":0.0', prompt["prompt_text"])

    def test_passive_streak_updates_only_for_accepted_normal_policies(self):
        memory = PolicyMemory()
        perception = _s1_perception(companion_x=1.6)
        passive = [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0}, {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0}]

        memory.update_from_response(_policy_response("ai", passive), perception=perception)
        self.assertEqual(memory.consecutive_passive_policy_count, 1)
        self.assertFalse(memory.last_policy_had_movement)

        memory.update_from_response(_policy_response(POLICY_SOURCE_TRANSITION_PRIORITY, [{"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_TRANSITION_ONLY, "target": "nearest_valid_interaction_area_point", "duration": 1.0}]), perception=perception)
        self.assertEqual(memory.consecutive_passive_policy_count, 1)

        memory.update_from_response(_policy_response(POLICY_SOURCE_POLICY_ERROR, []), perception=perception)
        self.assertEqual(memory.consecutive_passive_policy_count, 1)

        movement = [{"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "target": "", "radius_min": 1.2, "radius_max": 2.5, "duration": 3.0}]
        memory.update_from_response(_policy_response(POLICY_SOURCE_FALLBACK, movement), perception=perception)
        self.assertEqual(memory.consecutive_passive_policy_count, 0)
        self.assertTrue(memory.last_policy_had_movement)
        self.assertEqual(memory.last_movement_scope, SCOPE_AROUND_PLAYER)

    def test_recent_gaze_history_records_normal_ai_look_target(self):
        memory = PolicyMemory()
        memory.update_from_response(
            _policy_response(
                POLICY_SOURCE_AI,
                [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0}],
                state=STATE_S2,
            ),
            perception=_s2_perception(),
        )

        self.assertEqual(memory.recent_gaze_state, STATE_S2)
        self.assertEqual(memory.recent_normal_gaze_targets, [TARGET_PLAYER_HEAD])
        self.assertEqual(memory.build_prompt_context()["recent_normal_gaze_targets"], [TARGET_PLAYER_HEAD])
        self.assertNotIn("recent_normal_gaze_targets", memory.build_summary())

    def test_recent_gaze_history_records_normal_ai_face_toward_target(self):
        memory = PolicyMemory()
        memory.update_from_response(
            _policy_response(
                POLICY_SOURCE_AI,
                [{"type": PRIMITIVE_FACE_TOWARD, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0}],
                state=STATE_S2,
            ),
            perception=_s2_perception(),
        )

        self.assertEqual(memory.recent_normal_gaze_targets, [TARGET_CAMPFIRE_CENTER])

    def test_recent_gaze_history_uses_last_explicit_gaze_target_per_policy(self):
        memory = PolicyMemory()
        memory.update_from_response(
            _policy_response(
                POLICY_SOURCE_AI,
                [
                    {"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0},
                    {"type": PRIMITIVE_FACE_TOWARD, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0},
                ],
                state=STATE_S2,
            ),
            perception=_s2_perception(),
        )

        self.assertEqual(memory.recent_normal_gaze_targets, [TARGET_CAMPFIRE_CENTER])

    def test_recent_gaze_history_ignores_ai_policy_without_explicit_gaze(self):
        memory = PolicyMemory()
        memory.update_from_response(
            _policy_response(
                POLICY_SOURCE_AI,
                [{"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0}],
                state=STATE_S2,
            ),
            perception=_s2_perception(),
        )

        self.assertEqual(memory.recent_normal_gaze_targets, [])

    def test_recent_gaze_history_is_capped_at_three_entries(self):
        memory = PolicyMemory()
        for target in (TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD, TARGET_PLAYER_HEAD):
            memory.update_from_response(
                _policy_response(
                    POLICY_SOURCE_AI,
                    [{"type": PRIMITIVE_LOOK_AT, "target": target, "duration": 1.0}],
                    state=STATE_S2,
                ),
                perception=_s2_perception(),
            )

        self.assertEqual(
            memory.recent_normal_gaze_targets,
            [TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD, TARGET_PLAYER_HEAD],
        )

    def test_recent_gaze_history_clears_on_state_change(self):
        memory = PolicyMemory()
        memory.update_from_response(
            _policy_response(
                POLICY_SOURCE_AI,
                [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0}],
                state=STATE_S2,
            ),
            perception=_s2_perception(),
        )
        memory.update_from_response(
            _policy_response(
                POLICY_SOURCE_AI,
                [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0}],
                state=STATE_S3,
            ),
            perception=_s3_perception(),
        )

        self.assertEqual(memory.recent_gaze_state, STATE_S3)
        self.assertEqual(memory.recent_normal_gaze_targets, [TARGET_CAMPFIRE_CENTER])

    def test_fresh_policy_memory_has_empty_gaze_history(self):
        memory = PolicyMemory()

        self.assertEqual(memory.recent_gaze_state, "")
        self.assertEqual(memory.recent_normal_gaze_targets, [])
        self.assertEqual(memory.build_prompt_context()["recent_normal_gaze_targets"], [])
        self.assertNotIn("recent_gaze_state", memory.build_summary())
        self.assertNotIn("recent_normal_gaze_targets", memory.build_summary())

    def test_recent_gaze_history_filters_non_ai_and_non_executable_responses(self):
        memory = PolicyMemory()
        perception = _s2_perception()
        primitive = [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0}]

        memory.update_from_response(_policy_response(POLICY_SOURCE_FALLBACK, primitive, state=STATE_S2), perception=perception)
        memory.update_from_response(_policy_response(POLICY_SOURCE_TRANSITION_PRIORITY, primitive, state=STATE_S2), perception=perception)
        memory.update_from_response(_policy_response(POLICY_SOURCE_POLICY_ERROR, primitive, state=STATE_S2), perception=perception)
        memory.update_from_response(_policy_response(POLICY_SOURCE_AI, primitive, state=STATE_S2, execute_policy=False), perception=perception)

        self.assertEqual(memory.recent_normal_gaze_targets, [])

    def test_first_s3_prompt_does_not_receive_s2_gaze_history_through_generic_summary(self):
        memory = PolicyMemory()
        for target in (TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD):
            memory.update_from_response(
                _policy_response(
                    POLICY_SOURCE_AI,
                    [{"type": PRIMITIVE_LOOK_AT, "target": target, "duration": 1.0}],
                    state=STATE_S2,
                ),
                perception=_s2_perception(),
            )

        self.assertEqual(memory.recent_gaze_state, STATE_S2)
        self.assertEqual(
            memory.recent_normal_gaze_targets,
            [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD],
        )

        s3_perception = _s3_perception(prop_runtime_available=False, hand_runtime_available=False)
        prompt = build_policy_prompt(
            s3_perception,
            STATE_S3,
            build_allowed_pools(STATE_S3, s3_perception),
            policy_memory_summary=memory.build_summary(),
            policy_memory_context=memory.build_prompt_context(),
        )
        text = prompt["prompt_text"]

        self.assertNotIn("player_head -> campfire_center -> player_head", text)
        self.assertNotIn("recent_gaze_state=S2", text)
        self.assertNotIn("recent_normal_gaze_targets=", text)
        self.assertNotIn("Recent normal gaze targets in this State:", text)

    def test_same_state_recent_gaze_section_is_not_duplicated_by_generic_summary(self):
        memory = PolicyMemory()
        for target in (TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD):
            memory.update_from_response(
                _policy_response(
                    POLICY_SOURCE_AI,
                    [{"type": PRIMITIVE_LOOK_AT, "target": target, "duration": 1.0}],
                    state=STATE_S3,
                ),
                perception=_s3_perception(prop_runtime_available=False, hand_runtime_available=False),
            )

        s3_perception = _s3_perception(prop_runtime_available=False, hand_runtime_available=False)
        summary = memory.build_summary()
        prompt = build_policy_prompt(
            s3_perception,
            STATE_S3,
            build_allowed_pools(STATE_S3, s3_perception),
            policy_memory_summary=summary,
            policy_memory_context=memory.build_prompt_context(),
        )
        text = prompt["prompt_text"]

        self.assertIn("Policy memory summary:", text)
        self.assertIn("Recent normal gaze targets in this State:", text)
        self.assertEqual(text.count("player_head -> campfire_center -> player_head"), 1)
        self.assertNotIn("recent_gaze_state", summary)
        self.assertNotIn("recent_normal_gaze_targets", summary)
        self.assertNotIn("recent_gaze_state=", text)
        self.assertNotIn("recent_normal_gaze_targets=", text)

    def test_s2_prompt_includes_recent_gaze_soft_oscillation_guidance(self):
        memory = PolicyMemory()
        for target in (TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD):
            memory.update_from_response(
                _policy_response(
                    POLICY_SOURCE_AI,
                    [{"type": PRIMITIVE_LOOK_AT, "target": target, "duration": 1.0}],
                    state=STATE_S2,
                ),
                perception=_s2_perception(),
            )

        prompt = build_policy_prompt(
            _s2_perception(),
            STATE_S2,
            build_allowed_pools(STATE_S2),
            policy_memory_context=memory.build_prompt_context(),
        )
        text = prompt["prompt_text"]

        self.assertIn("Recent normal gaze targets in this State:", text)
        self.assertIn("player_head -> campfire_center -> player_head", text)
        self.assertIn("Briefly continuing attention toward the same meaningful target is acceptable", text)
        self.assertIn("Avoid obvious A-B-A gaze-target oscillation", text)
        self.assertIn("This is soft guidance", text)
        self.assertIn("Use only currently allowed targets", text)
        self.assertIn("A Policy does not have to contain look_at or face_toward", text)
        self.assertIn("hold_position without an explicit gaze change remains legal", text)
        self.assertIn("Do not force neutral_forward merely to increase variety", text)
        self.assertNotIn("must switch targets", text)
        self.assertNotIn("every Policy must change gaze", text)

    def test_s3_prompt_includes_recent_gaze_without_state_contamination_or_unsupported_targets(self):
        memory_context = {
            "recent_gaze_state": STATE_S3,
            "recent_normal_gaze_targets": [TARGET_PLAYER_HEAD, "companion_marshmallow", TARGET_CAMPFIRE_CENTER],
        }
        prompt = build_policy_prompt(
            _s3_perception(prop_runtime_available=False, hand_runtime_available=False),
            STATE_S3,
            build_allowed_pools(STATE_S3, _s3_perception(prop_runtime_available=False, hand_runtime_available=False)),
            policy_memory_context=memory_context,
        )
        text = prompt["prompt_text"]

        self.assertIn("Recent normal gaze targets in this State:", text)
        self.assertIn("player_head -> campfire_center", text)
        self.assertNotIn("companion_marshmallow ->", text)
        self.assertIn("Do not force companion_marshmallow unless it is already exposed", text)

        stale_s2_context = {
            "recent_gaze_state": STATE_S2,
            "recent_normal_gaze_targets": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_PLAYER_HEAD],
        }
        stale_prompt = build_policy_prompt(
            _s3_perception(prop_runtime_available=False, hand_runtime_available=False),
            STATE_S3,
            build_allowed_pools(STATE_S3, _s3_perception(prop_runtime_available=False, hand_runtime_available=False)),
            policy_memory_context=stale_s2_context,
        )
        self.assertNotIn("Recent normal gaze targets in this State:", stale_prompt["prompt_text"])

    def test_s3_prompt_includes_soft_gaze_variation_guidance_without_enabling_movement(self):
        perception = _s3_perception()
        perception["companion"]["companion_prop_runtime_available"] = True
        perception["companion"]["has_companion_marshmallow"] = True
        perception["companion"]["companion_marshmallow_state"] = PROP_STATE_VISIBLE_ON_STICK
        perception["scene"]["look_targets_available"].append(TARGET_COMPANION_MARSHMALLOW)
        prompt = build_policy_prompt(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))
        text = prompt["prompt_text"]

        self.assertIn("Keep the body roasting orientation toward campfire_center when body-facing is useful", text)
        self.assertIn("campfire_center should remain the dominant gaze target in S3", text)
        self.assertIn("Occasional short looks toward player_head are natural", text)
        self.assertIn("If companion_marshmallow is available, occasional looks toward it are also natural", text)
        self.assertIn("Do not mechanically change gaze target every Policy; repeating the same meaningful target across multiple Policies is acceptable", text)
        self.assertIn(f"Forbidden motion primitives for this state: {PRIMITIVE_MOVE_WITHIN_SCOPE}.", text)

    def test_s1_prompt_does_not_add_recent_gaze_guidance(self):
        prompt = build_policy_prompt(
            _s1_perception(companion_x=1.6),
            STATE_S1,
            build_allowed_pools(STATE_S1),
            policy_memory_context={
                "recent_gaze_state": STATE_S1,
                "recent_normal_gaze_targets": [TARGET_PLAYER_HEAD, TARGET_NEUTRAL_FORWARD],
            },
        )

        self.assertNotIn("Recent normal gaze targets in this State:", prompt["prompt_text"])

    def test_validator_accepts_canonical_s1_around_player_movement(self):
        result = validate_policy_dict(
            {
                "activity_mode": "idle_presence",
                "policy_lifetime": 4.0,
                "intent_summary": "canonical move",
                "motion_primitives": [
                    {
                        "type": PRIMITIVE_MOVE_WITHIN_SCOPE,
                        "scope": SCOPE_AROUND_PLAYER,
                        "target": "",
                        "radius_min": S1_AROUND_PLAYER_RADIUS_MIN,
                        "radius_max": S1_AROUND_PLAYER_RADIUS_MAX,
                        "duration": 4.0,
                    }
                ],
            },
            STATE_S1,
            build_allowed_pools(STATE_S1),
        )
        self.assertTrue(result["valid"], msg=result["errors"])
        self.assertEqual(result["policy"]["motion_primitives"][0]["target"], "")

    def test_validator_normalizes_missing_around_player_target_but_rejects_semantic_target(self):
        missing_target = validate_policy_dict(
            {
                "activity_mode": "idle_presence",
                "policy_lifetime": 4.0,
                "intent_summary": "missing target should normalize",
                "motion_primitives": [
                    {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "radius_min": 1.2, "radius_max": 2.5, "duration": 4.0}
                ],
            },
            STATE_S1,
            build_allowed_pools(STATE_S1),
        )
        self.assertTrue(missing_target["valid"], msg=missing_target["errors"])
        self.assertEqual(missing_target["policy"]["motion_primitives"][0]["target"], "")

        semantic_target = validate_policy_dict(
            {
                "activity_mode": "idle_presence",
                "policy_lifetime": 4.0,
                "intent_summary": "player head target should fail",
                "motion_primitives": [
                    {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "target": TARGET_PLAYER_HEAD, "radius_min": 1.2, "radius_max": 2.5, "duration": 4.0}
                ],
            },
            STATE_S1,
            build_allowed_pools(STATE_S1),
        )
        self.assertFalse(semantic_target["valid"])
        self.assertIn("INVALID_AROUND_PLAYER_TARGET", [error["code"] for error in semantic_target["errors"]])

    def test_validator_state_boundaries_remain_unchanged(self):
        s2_around_player = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid s2 around_player",
                "motion_primitives": [{"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "target": "", "radius_min": 1.2, "radius_max": 2.5, "duration": 1.5}],
            },
            STATE_S2,
            build_allowed_pools(STATE_S2),
        )
        self.assertFalse(s2_around_player["valid"])

        s2_interaction_area = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "valid s2 interaction area",
                "motion_primitives": [{"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_INTERACTION_AREA, "target": TARGET_INTERACTION_AREA, "duration": 1.5}],
            },
            STATE_S2,
            build_allowed_pools(STATE_S2),
        )
        self.assertTrue(s2_interaction_area["valid"], msg=s2_interaction_area["errors"])

        s3_move = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid s3 movement",
                "motion_primitives": [{"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_INTERACTION_AREA, "target": TARGET_INTERACTION_AREA, "duration": 1.5}],
            },
            STATE_S3,
            build_allowed_pools(STATE_S3),
        )
        self.assertFalse(s3_move["valid"])

        transition_only = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid transition only",
                "motion_primitives": [{"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_TRANSITION_ONLY, "target": "nearest_valid_interaction_area_point", "duration": 1.5}],
            },
            STATE_S2,
            build_allowed_pools(STATE_S2),
        )
        self.assertFalse(transition_only["valid"])

    def test_s2_interaction_area_is_movement_only_for_validator(self):
        s2_pools = build_allowed_pools(STATE_S2)
        self.assertNotIn(TARGET_INTERACTION_AREA, s2_pools["allowed_targets"])
        self.assertIn(SCOPE_INTERACTION_AREA, s2_pools["allowed_scopes"])
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, s2_pools["motion_primitives"])

        canonical_movement = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "small in-area reposition",
                "motion_primitives": [
                    {
                        "type": PRIMITIVE_MOVE_WITHIN_SCOPE,
                        "scope": SCOPE_INTERACTION_AREA,
                        "target": TARGET_INTERACTION_AREA,
                        "duration": 2.0,
                    }
                ],
            },
            STATE_S2,
            s2_pools,
        )
        self.assertTrue(canonical_movement["valid"], msg=canonical_movement["errors"])

        look_at_interaction_area = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid look target",
                "motion_primitives": [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_INTERACTION_AREA, "duration": 1.0}],
            },
            STATE_S2,
            s2_pools,
        )
        self.assertFalse(look_at_interaction_area["valid"])
        self.assertIn("INVALID_TARGET", [error["code"] for error in look_at_interaction_area["errors"]])

        face_toward_interaction_area = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid face target",
                "motion_primitives": [{"type": PRIMITIVE_FACE_TOWARD, "target": TARGET_INTERACTION_AREA, "duration": 1.0}],
            },
            STATE_S2,
            s2_pools,
        )
        self.assertFalse(face_toward_interaction_area["valid"])
        self.assertIn("INVALID_TARGET", [error["code"] for error in face_toward_interaction_area["errors"]])

    def test_raw_vs_validated_debug_is_json_serializable(self):
        result = validate_policy_dict(
            {
                "activity_mode": "idle_presence",
                "policy_lifetime": 4.0,
                "intent_summary": "debug evidence",
                "motion_primitives": [
                    {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "target": TARGET_PLAYER_HEAD, "radius_min": 1.2, "radius_max": 2.5, "duration": 4.0}
                ],
            },
            STATE_S1,
            build_allowed_pools(STATE_S1),
        )
        debug = result["debug"]
        self.assertEqual(debug["raw_ai_motion_primitive_types"], [PRIMITIVE_MOVE_WITHIN_SCOPE])
        self.assertEqual(debug["validated_motion_primitive_types"], [])
        self.assertIn("INVALID_AROUND_PLAYER_TARGET", debug["validator_error_codes"])
        json.dumps(debug)

    def test_prompt_pool_does_not_offer_implicit_runtime_behaviors(self):
        prompt = build_policy_prompt(_s1_perception(companion_x=1.6), STATE_S1, build_allowed_pools(STATE_S1))
        allowed_primitives = prompt["allowed_pools"]["motion_primitives"]

        self.assertNotIn("base_idle", allowed_primitives)
        self.assertNotIn("base_move", allowed_primitives)
        self.assertNotIn("default_attention", allowed_primitives)
        self.assertNotIn("idle_motion", allowed_primitives)
        self.assertNotIn("play_gesture", allowed_primitives)

    def test_fallback_s1_uses_canonical_empty_target_and_radius_band(self):
        response = build_fallback_response(_s1_perception(), _state_resolution(STATE_S1))
        primitive = response["motion_primitives"][0]

        self.assertEqual(primitive["type"], PRIMITIVE_MOVE_WITHIN_SCOPE)
        self.assertEqual(primitive["scope"], SCOPE_AROUND_PLAYER)
        self.assertEqual(primitive["target"], "")
        self.assertEqual(primitive["radius_min"], S1_AROUND_PLAYER_RADIUS_MIN)
        self.assertEqual(primitive["radius_max"], S1_AROUND_PLAYER_RADIUS_MAX)

    def test_fallback_policy_error_and_duration_regressions(self):
        fallback = build_fallback_response(_s1_perception(), _state_resolution(STATE_S3))
        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, [primitive["type"] for primitive in fallback["motion_primitives"]])

        policy_error = build_policy_error_response(_s1_perception(), _state_resolution(STATE_S1), "TEST_ERROR", "test")
        memory = PolicyMemory()
        memory.update_from_response(_policy_response("ai", [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0}]), perception=_s1_perception())
        memory.update_from_response(policy_error, perception=_s1_perception())
        self.assertEqual(memory.consecutive_passive_policy_count, 1)

        duration_result = validate_policy_dict(
            {
                "activity_mode": "idle_presence",
                "policy_lifetime": 4.0,
                "intent_summary": "too long",
                "motion_primitives": [
                    {"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 2.0},
                    {"type": PRIMITIVE_HOLD_POSITION, "duration": 2.5},
                ],
            },
            STATE_S1,
            build_allowed_pools(STATE_S1),
        )
        self.assertFalse(duration_result["valid"])
        self.assertIn("POLICY_DURATION_TOO_LONG", [error["code"] for error in duration_result["errors"]])

    def test_invalid_ai_fallback_replacement_debug_is_visible(self):
        perception = _s1_perception()
        queue = TransitionQueue()
        ai_result = {
            "success": False,
            "error_status": "AI_VALIDATION_FAILED",
            "error_message": "AI policy validation failed.",
            "validator_error_codes": ["INVALID_AROUND_PLAYER_TARGET"],
            "warnings": [],
            "validator_debug": {"raw_ai_motion_primitive_types": [PRIMITIVE_MOVE_WITHIN_SCOPE]},
            "provider": "stub",
            "model": "stub-model",
            "latency_ms": 1,
        }

        with patch.object(config, "AI_ENABLED", True), patch.object(config, "FALLBACK_ENABLED", True), patch.object(config, "FORCE_FALLBACK_FOR_TEST", False), patch(
            "python_companion.policy_layer.generate_ai_policy", return_value=ai_result
        ):
            response = generate_policy_response(perception, _state_resolution(STATE_S1), policy_memory=PolicyMemory(), transition_queue=queue)

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        validator_debug = response["debug"]["validator_debug"]
        self.assertTrue(validator_debug["fallback_replaced_invalid_ai_policy"])
        self.assertEqual(validator_debug["raw_ai_motion_primitive_types"], [PRIMITIVE_MOVE_WITHIN_SCOPE])

    def test_required_movement_gate_accepts_valid_ai_movement_policy(self):
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(_canonical_movement_policy())
        ):
            result = generate_ai_policy(_s1_perception(companion_x=4.2), STATE_S1, build_allowed_pools(STATE_S1))

        self.assertTrue(result["success"], msg=result)
        self.assertEqual(result["policy"]["motion_primitives"][0]["type"], PRIMITIVE_MOVE_WITHIN_SCOPE)
        self.assertNotIn("REQUIRED_S1_MOVEMENT_MISSING", result["validator_error_codes"])

    def test_required_movement_gate_rejects_valid_passive_ai_policy(self):
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(_passive_policy())
        ):
            result = generate_ai_policy(_s1_perception(companion_x=4.2), STATE_S1, build_allowed_pools(STATE_S1))

        self.assertFalse(result["success"])
        self.assertEqual(result["error_status"], AI_ERROR_VALIDATION_FAILED)
        self.assertIn("REQUIRED_S1_MOVEMENT_MISSING", result["validator_error_codes"])
        self.assertEqual(result["validator_debug"]["raw_ai_motion_primitive_types"], [PRIMITIVE_LOOK_AT, PRIMITIVE_HOLD_POSITION])
        self.assertEqual(result["validator_debug"]["validated_motion_primitive_types"], [PRIMITIVE_LOOK_AT, PRIMITIVE_HOLD_POSITION])
        self.assertEqual(result["validator_debug"]["required_s1_movement_gate"], "failed")
        self.assertTrue(result["validator_debug"]["required_s1_movement_missing"])

    def test_required_movement_failure_uses_existing_fallback_path(self):
        perception = _s1_perception(companion_x=4.2)
        ai_result = {
            "success": False,
            "error_status": AI_ERROR_VALIDATION_FAILED,
            "error_message": "S1 movement was required, but the validated AI policy did not include around_player movement.",
            "validator_error_codes": ["REQUIRED_S1_MOVEMENT_MISSING"],
            "warnings": [],
            "validator_debug": {
                "movement_recommendation": "required",
                "raw_ai_motion_primitive_types": [PRIMITIVE_LOOK_AT, PRIMITIVE_HOLD_POSITION],
                "validated_motion_primitive_types": [PRIMITIVE_LOOK_AT, PRIMITIVE_HOLD_POSITION],
                "required_s1_movement_gate": "failed",
                "required_s1_movement_missing": True,
            },
            "provider": "stub",
            "model": "stub-model",
            "latency_ms": 1,
        }

        with patch.object(config, "AI_ENABLED", True), patch.object(config, "FALLBACK_ENABLED", True), patch.object(config, "FORCE_FALLBACK_FOR_TEST", False), patch(
            "python_companion.policy_layer.generate_ai_policy", return_value=ai_result
        ):
            response = generate_policy_response(perception, _state_resolution(STATE_S1), policy_memory=PolicyMemory(), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        primitive = response["motion_primitives"][0]
        self.assertEqual(primitive["type"], PRIMITIVE_MOVE_WITHIN_SCOPE)
        self.assertEqual(primitive["scope"], SCOPE_AROUND_PLAYER)
        self.assertEqual(primitive["target"], "")
        self.assertTrue(response["debug"]["validator_debug"]["fallback_replaced_invalid_ai_policy"])
        self.assertIn("REQUIRED_S1_MOVEMENT_MISSING", response["debug"]["validator_error_codes"])

    def test_required_movement_failure_with_fallback_disabled_returns_policy_error(self):
        perception = _s1_perception(companion_x=4.2)
        ai_result = {
            "success": False,
            "error_status": AI_ERROR_VALIDATION_FAILED,
            "error_message": "S1 movement was required, but the validated AI policy did not include around_player movement.",
            "validator_error_codes": ["REQUIRED_S1_MOVEMENT_MISSING"],
            "warnings": [],
            "validator_debug": {"required_s1_movement_gate": "failed"},
            "provider": "stub",
            "model": "stub-model",
            "latency_ms": 1,
        }

        with patch.object(config, "AI_ENABLED", True), patch.object(config, "FALLBACK_ENABLED", False), patch.object(config, "FORCE_FALLBACK_FOR_TEST", False), patch(
            "python_companion.policy_layer.generate_ai_policy", return_value=ai_result
        ):
            response = generate_policy_response(perception, _state_resolution(STATE_S1), policy_memory=PolicyMemory(), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_POLICY_ERROR)
        self.assertFalse(response["control"]["execute_policy"])
        self.assertIn("REQUIRED_S1_MOVEMENT_MISSING", response["debug"]["validator_error_codes"])

    def test_recommended_movement_does_not_force_passive_ai_policy(self):
        memory_context = {"last_policy_had_movement": True}
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(_passive_policy())
        ):
            result = generate_ai_policy(
                _s1_perception(companion_x=4.2),
                STATE_S1,
                build_allowed_pools(STATE_S1),
                policy_memory_context=memory_context,
            )

        self.assertTrue(result["success"], msg=result)
        self.assertEqual(result["validator_debug"]["movement_recommendation"], "recommended")
        self.assertEqual(result["policy"]["motion_primitives"][0]["type"], PRIMITIVE_LOOK_AT)

    def test_s3_allowed_prop_states_no_longer_include_hidden(self):
        s3_perception = _s3_perception()
        s3_pools = build_allowed_pools(STATE_S3, s3_perception)
        self.assertEqual(s3_pools["allowed_prop_states"]["stick"], [PROP_STATE_VISIBLE_IN_HAND])
        self.assertEqual(s3_pools["allowed_prop_states"]["marshmallow"], [PROP_STATE_VISIBLE_ON_STICK])

        s2_perception = _s3_perception()
        s2_perception["task"]["stick_picked"] = False
        s2_perception["companion"]["current_state"] = STATE_S2
        s2_pools = build_allowed_pools(STATE_S2, s2_perception)
        self.assertEqual(s2_pools["allowed_prop_states"]["stick"], [PROP_STATE_VISIBLE_IN_HAND, PROP_STATE_HIDDEN])
        self.assertNotIn("marshmallow", s2_pools["allowed_props"])

        s1_pools = build_allowed_pools(STATE_S1, _s1_perception())
        self.assertEqual(s1_pools["allowed_prop_states"], {})

    def test_s3_prompt_includes_roasting_prop_invariant_when_runtime_ready(self):
        perception = _s3_perception()
        prompt = build_policy_prompt(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))
        text = prompt["prompt_text"]

        self.assertIn("S3 roasting prop invariant", text)
        self.assertIn("stick must remain visible_in_hand", text)
        self.assertIn("marshmallow must remain visible_on_stick", text)
        self.assertIn("Every executable S3 policy must begin with set_companion_prop stick visible_in_hand", text)
        self.assertIn("The second primitive must be set_companion_prop marshmallow visible_on_stick", text)
        self.assertIn("request snapshot may be stale", text)
        self.assertIn("roast_pose and reaching the roasting anchor do not automatically display props", text)
        self.assertIn("Do not hide either companion prop in S3", text)

    def test_s3_hidden_prop_setters_are_rejected_by_allowed_pool(self):
        perception = _s3_perception()
        pools = build_allowed_pools(STATE_S3, perception)
        result = validate_policy_dict(
            _s3_prop_policy(
                [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_HIDDEN, "duration": 0.25},
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_HIDDEN, "duration": 0.25},
                ]
            ),
            STATE_S3,
            pools,
        )

        self.assertFalse(result["valid"])
        self.assertIn("INVALID_PROP_STATE", [error["code"] for error in result["errors"]])

    def test_s3_hidden_initial_props_plus_passive_ai_policy_fails_quality_gate(self):
        perception = _s3_perception(stick_state=PROP_STATE_HIDDEN, marshmallow_state=PROP_STATE_HIDDEN)
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(_s3_passive_policy())
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertFalse(result["success"])
        self.assertEqual(result["error_status"], AI_ERROR_VALIDATION_FAILED)
        self.assertIn("REQUIRED_S3_ROASTING_PROP_SETUP_MISSING", result["validator_error_codes"])
        projection = result["validator_debug"]["s3_prop_projection"]
        self.assertEqual(projection["initial_stick_state"], PROP_STATE_HIDDEN)
        self.assertEqual(projection["projected_marshmallow_state"], PROP_STATE_HIDDEN)

    def test_s3_partial_setter_without_required_prefix_fails_quality_gate(self):
        perception = _s3_perception(stick_state=PROP_STATE_VISIBLE_IN_HAND, marshmallow_state=PROP_STATE_HIDDEN)
        policy = _s3_prop_policy(
            [
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.25},
                {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0},
            ]
        )
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(policy)
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertFalse(result["success"])
        self.assertIn("REQUIRED_S3_ROASTING_PROP_SETUP_MISSING", result["validator_error_codes"])
        self.assertFalse(result["validator_debug"]["s3_prop_projection"]["required_setup_prefix_valid"])

    def test_s3_already_visible_props_with_required_prefix_passes_quality_gate(self):
        perception = _s3_perception(stick_state=PROP_STATE_VISIBLE_IN_HAND, marshmallow_state=PROP_STATE_VISIBLE_ON_STICK)
        policy = _s3_prop_policy(
            [
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.25},
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.25},
                {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0},
            ]
        )
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(policy)
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertTrue(result["success"], msg=result)

    def test_s3_both_hidden_correct_setter_order_passes_quality_gate(self):
        perception = _s3_perception(stick_state=PROP_STATE_HIDDEN, marshmallow_state=PROP_STATE_HIDDEN)
        policy = _s3_prop_policy(
            [
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.25},
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.25},
                {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0},
            ]
        )
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(policy)
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertTrue(result["success"], msg=result)

    def test_s3_reversed_setter_order_fails_quality_gate(self):
        perception = _s3_perception(stick_state=PROP_STATE_HIDDEN, marshmallow_state=PROP_STATE_HIDDEN)
        policy = _s3_prop_policy(
            [
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.25},
                {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.25},
            ]
        )
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(policy)
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertFalse(result["success"])
        self.assertIn("REQUIRED_S3_ROASTING_PROP_SETUP_MISSING", result["validator_error_codes"])
        self.assertFalse(result["validator_debug"]["s3_prop_projection"]["ordering_valid"])

    def test_s3_already_correct_prop_state_still_requires_setup_prefix(self):
        perception = _s3_perception(stick_state=PROP_STATE_VISIBLE_IN_HAND, marshmallow_state=PROP_STATE_VISIBLE_ON_STICK)
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(_s3_passive_policy())
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertFalse(result["success"])
        self.assertIn("REQUIRED_S3_ROASTING_PROP_SETUP_MISSING", result["validator_error_codes"])
        self.assertFalse(result["validator_debug"]["s3_prop_projection"]["required_setup_prefix_valid"])

    def test_s3_prop_runtime_unavailable_does_not_activate_quality_gate(self):
        perception = _s3_perception(prop_runtime_available=False, stick_state=PROP_STATE_HIDDEN, marshmallow_state=PROP_STATE_HIDDEN)
        with patch("python_companion.ai_policy_client.get_ai_provider_config", return_value=_provider_config()), patch(
            "python_companion.ai_policy_client.request_chat_completion", return_value=_provider_success(_s3_passive_policy())
        ):
            result = generate_ai_policy(perception, STATE_S3, build_allowed_pools(STATE_S3, perception))

        self.assertTrue(result["success"], msg=result)

    def test_required_s3_prop_failure_uses_existing_fallback_path(self):
        perception = _s3_perception(stick_state=PROP_STATE_HIDDEN, marshmallow_state=PROP_STATE_HIDDEN)
        ai_result = {
            "success": False,
            "error_status": AI_ERROR_VALIDATION_FAILED,
            "error_message": "S3 requires the companion stick to be visible_in_hand and the companion marshmallow to be visible_on_stick.",
            "validator_error_codes": ["REQUIRED_S3_ROASTING_PROP_SETUP_MISSING"],
            "warnings": [],
            "validator_debug": {"required_s3_roasting_prop_gate": "failed"},
            "provider": "stub",
            "model": "stub-model",
            "latency_ms": 1,
        }

        with patch.object(config, "AI_ENABLED", True), patch.object(config, "FALLBACK_ENABLED", True), patch.object(config, "FORCE_FALLBACK_FOR_TEST", False), patch(
            "python_companion.policy_layer.generate_ai_policy", return_value=ai_result
        ):
            response = generate_policy_response(perception, _state_resolution(STATE_S3), policy_memory=PolicyMemory(), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        primitive_types = [primitive["type"] for primitive in response["motion_primitives"]]
        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, primitive_types)
        stick_index = next(i for i, primitive in enumerate(response["motion_primitives"]) if primitive["type"] == PRIMITIVE_SET_COMPANION_PROP and primitive["prop"] == "stick")
        marshmallow_index = next(i for i, primitive in enumerate(response["motion_primitives"]) if primitive["type"] == PRIMITIVE_SET_COMPANION_PROP and primitive["prop"] == "marshmallow")
        self.assertLess(stick_index, marshmallow_index)
        self.assertIn("REQUIRED_S3_ROASTING_PROP_SETUP_MISSING", response["debug"]["validator_error_codes"])

    def test_required_s3_prop_failure_with_fallback_disabled_returns_policy_error(self):
        perception = _s3_perception(stick_state=PROP_STATE_HIDDEN, marshmallow_state=PROP_STATE_HIDDEN)
        ai_result = {
            "success": False,
            "error_status": AI_ERROR_VALIDATION_FAILED,
            "error_message": "S3 requires the companion stick to be visible_in_hand and the companion marshmallow to be visible_on_stick.",
            "validator_error_codes": ["REQUIRED_S3_ROASTING_PROP_SETUP_MISSING"],
            "warnings": [],
            "validator_debug": {"required_s3_roasting_prop_gate": "failed"},
            "provider": "stub",
            "model": "stub-model",
            "latency_ms": 1,
        }

        with patch.object(config, "AI_ENABLED", True), patch.object(config, "FALLBACK_ENABLED", False), patch.object(config, "FORCE_FALLBACK_FOR_TEST", False), patch(
            "python_companion.policy_layer.generate_ai_policy", return_value=ai_result
        ):
            response = generate_policy_response(perception, _state_resolution(STATE_S3), policy_memory=PolicyMemory(), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_POLICY_ERROR)
        self.assertFalse(response["control"]["execute_policy"])
        self.assertIn("REQUIRED_S3_ROASTING_PROP_SETUP_MISSING", response["debug"]["validator_error_codes"])


if __name__ == "__main__":
    unittest.main()
