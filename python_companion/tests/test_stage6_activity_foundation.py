import copy
import unittest
from unittest.mock import patch

from python_companion.activity_policy_memory import ActivityPolicyMemoryStore
from python_companion.companion_server import CompanionRuntime
from python_companion.config import FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS
from python_companion.models import (
    ACTIVITY_ID_CAMPFIRE,
    ACTIVITY_ID_COMMON,
    ACTIVITY_ID_FISHING,
    AI_ERROR_DISABLED,
    AI_ERROR_PROVIDER_TIMEOUT,
    POLICY_SOURCE_AI,
    POLICY_SOURCE_FALLBACK,
    POLICY_SOURCE_POLICY_ERROR,
    POLICY_SOURCE_TRANSITION_PRIORITY,
    POSE_FISHING_READY,
    POSE_READY,
    POSE_ROAST_POSE,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_PERFORM_FISHING_CAST_CYCLE,
    PRIMITIVE_SET_COMPANION_PROP,
    PRIMITIVE_SET_HAND_POSE,
    PROP_FISHING_ROD,
    PROP_STATE_VISIBLE_IN_HAND,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    SCOPE_TRANSITION_ONLY,
    STATE_F2,
    STATE_F3,
    STATE_S1,
    STATE_S2,
    STATE_S3,
    TARGET_ASSIGNED_FISHING_ANCHOR,
    TARGET_ASSIGNED_FISHING_CAST_TARGET,
    TARGET_CAMPFIRE_CENTER,
    TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT,
    TARGET_PLAYER_BOBBER,
    TARGET_PLAYER_FISHING_ROD,
    TARGET_PLAYER_HEAD,
    TARGET_WATER_FOCUS,
)
from python_companion.policy_layer import generate_policy_response
from python_companion.policy_validator import validate_policy_dict
from python_companion.prompt_builder import build_policy_prompt
from python_companion.state_config import build_allowed_pools, get_state_config
from python_companion.state_layer import resolve_state
from python_companion.transition_priority import evaluate_transition_priority
from python_companion.transition_queue import TransitionQueue


def _base_perception():
    return {
        "frame": {"request_id": "activity_test", "request_role": "current_policy", "current_state_epoch": 3, "policy_index": 2},
        "task": {"campfire_started": False, "stick_picked": False},
        "control": {"companion_enabled": True, "shutdown_signal": False, "ai_enabled": True, "fallback_enabled": True},
        "player": {"position": {"x": 0.0, "y": 0.0, "z": 0.0}},
        "companion": {
            "current_state": STATE_S1,
            "previous_state": STATE_S1,
            "position": {"x": 1.5, "y": 0.0, "z": 0.0},
            "assigned_fishing_anchor_id": "",
            "assigned_fishing_cast_target_id": "",
            "current_hand_pose": "neutral",
            "hand_pose_runtime_available": True,
            "companion_prop_runtime_available": True,
            "companion_fishing_rod_state": "hidden",
            "companion_stick_state": "hidden",
            "companion_marshmallow_state": "hidden",
        },
        "scene": {
            "campfire_center_available": True,
            "look_targets_available": ["player_head", "neutral_forward", "campfire_center", "water_focus", "player_fishing_rod", "player_bobber"],
        },
        "entry_checks": {
            "companion_inside_interaction_area": False,
            "companion_at_valid_roasting_anchor": False,
            "companion_inside_fishing_interaction_area": False,
            "companion_at_valid_fishing_anchor": False,
            "fishing_rod_ready": False,
        },
        "events": {"state_change_id": 3, "recent_events": []},
    }


def _with_activity(perception, activity_id, phase, *, detail="none", fishing_completed=False, epoch=5):
    updated = copy.deepcopy(perception)
    updated["activity"] = {
        "id": activity_id,
        "phase": phase,
        "detail_phase": detail,
        "fishing_completed": fishing_completed,
        "context_key": f"{activity_id}/{phase}/{detail}",
        "context_epoch": epoch,
    }
    return updated


def _resolved(perception):
    resolution = resolve_state(perception)
    assert resolution.is_valid
    return resolution


class Stage6ActivityFoundationTests(unittest.TestCase):
    def test_activity_resolution_maps_all_final_canonical_states(self):
        self.assertEqual(_resolved(_with_activity(_base_perception(), ACTIVITY_ID_COMMON, "free")).current_state, STATE_S1)
        self.assertEqual(_resolved(_with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")).current_state, STATE_F2)
        self.assertEqual(_resolved(_with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")).current_state, STATE_F3)

        campfire_ready = _base_perception()
        campfire_ready["task"]["campfire_started"] = True
        self.assertEqual(_resolved(_with_activity(campfire_ready, ACTIVITY_ID_CAMPFIRE, "ready")).current_state, STATE_S2)

        campfire_engagement = copy.deepcopy(campfire_ready)
        campfire_engagement["task"]["stick_picked"] = True
        self.assertEqual(_resolved(_with_activity(campfire_engagement, ACTIVITY_ID_CAMPFIRE, "engagement")).current_state, STATE_S3)

    def test_f2_incomplete_returns_transition_priority_with_stable_id_and_package(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        resolution = _resolved(perception)
        queue = TransitionQueue()

        response = generate_policy_response(
            perception,
            resolution,
            policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING),
            transition_queue=queue,
        )

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_TRANSITION_PRIORITY)
        self.assertEqual(queue.current_task().transition_id, "f2_enter_fishing_interaction_area")
        self.assertEqual(queue.current_task().goal_target, TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT)
        self.assertEqual(response["motion_primitives"][0]["scope"], SCOPE_TRANSITION_ONLY)
        self.assertEqual(response["motion_primitives"][0]["target"], TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT)
        self.assertEqual(response["motion_primitives"][-1]["type"], PRIMITIVE_SET_HAND_POSE)
        self.assertEqual(response["motion_primitives"][-1]["pose"], POSE_FISHING_READY)

    def test_f2_complete_enters_real_normal_fallback_path(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        perception["companion"]["companion_fishing_rod_state"] = PROP_STATE_VISIBLE_IN_HAND
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(
                perception,
                resolution,
                policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING),
                transition_queue=TransitionQueue(),
            )

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        self.assertTrue(response["control"]["execute_policy"])
        self.assertNotEqual(response["debug"]["error_code"], AI_ERROR_DISABLED)
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, [primitive["type"] for primitive in response["motion_primitives"]])

    def test_f2_fallback_uses_interaction_area_semantics_only(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(perception, resolution, policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        move = next(primitive for primitive in response["motion_primitives"] if primitive["type"] == PRIMITIVE_MOVE_WITHIN_SCOPE)
        self.assertEqual(move["scope"], SCOPE_INTERACTION_AREA)
        self.assertEqual(move["target"], "interaction_area")
        self.assertNotEqual(move["target"], TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT)

    def test_f2_consecutive_fallback_policies_do_not_force_movement_every_time(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)
        store = ActivityPolicyMemoryStore()

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            first_response = generate_policy_response(perception, resolution, policy_memory=store.get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())
            store.update_from_response(ACTIVITY_ID_FISHING, first_response, perception=perception)
            second_response = generate_policy_response(perception, resolution, policy_memory=store.get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        first_types = [primitive["type"] for primitive in first_response["motion_primitives"]]
        second_types = [primitive["type"] for primitive in second_response["motion_primitives"]]
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, first_types)
        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, second_types)

    def test_f2_fallback_after_recent_movement_can_return_passive_look_and_hold(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)
        store = ActivityPolicyMemoryStore()
        store.update_from_response(
            ACTIVITY_ID_FISHING,
            {
                "control": {"execute_policy": True},
                "state": {"current_state": STATE_F2},
                "policy": {"policy_source": POLICY_SOURCE_FALLBACK, "activity_mode": "co_attention"},
                "motion_primitives": [
                    {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "target": "interaction_area", "scope": SCOPE_INTERACTION_AREA, "duration": 2.0},
                    {"type": PRIMITIVE_LOOK_AT, "target": TARGET_WATER_FOCUS, "duration": 1.5},
                ],
                "debug": {},
            },
            perception=perception,
        )

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(perception, resolution, policy_memory=store.get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        primitive_types = [primitive["type"] for primitive in response["motion_primitives"]]
        self.assertIn(PRIMITIVE_LOOK_AT, primitive_types)
        self.assertIn(PRIMITIVE_HOLD_POSITION, primitive_types)
        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, primitive_types)

    def test_f2_validator_rejects_around_player_normal_movement(self):
        result = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid",
                "motion_primitives": [
                    {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "target": "", "radius_min": 1.2, "radius_max": 2.5, "duration": 2.0}
                ],
            },
            STATE_F2,
            build_allowed_pools(STATE_F2, _base_perception()),
        )
        self.assertFalse(result["valid"])
        self.assertIn("INVALID_SCOPE", [error["code"] for error in result["errors"]])

    def test_f3_incomplete_returns_transition_priority_with_stable_id(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        resolution = _resolved(perception)
        queue = TransitionQueue()

        response = generate_policy_response(
            perception,
            resolution,
            policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING),
            transition_queue=queue,
        )

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_TRANSITION_PRIORITY)
        self.assertEqual(queue.current_task().transition_id, "f3_enter_assigned_fishing_anchor")
        self.assertEqual(queue.current_task().goal_target, TARGET_ASSIGNED_FISHING_ANCHOR)
        self.assertEqual(response["motion_primitives"][1]["target"], TARGET_ASSIGNED_FISHING_CAST_TARGET)

    def test_f3_complete_enters_real_normal_fallback_path(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        perception["companion"]["companion_fishing_rod_state"] = PROP_STATE_VISIBLE_IN_HAND
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(
                perception,
                resolution,
                policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING),
                transition_queue=TransitionQueue(),
            )

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        self.assertTrue(response["control"]["execute_policy"])

    def test_f3_fallback_never_contains_normal_movement(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(perception, resolution, policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, [primitive["type"] for primitive in response["motion_primitives"]])

    def test_f3_validator_allows_one_cast_cycle_only_in_f3(self):
        self.assertEqual(FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS, 20.0)

        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS,
                "intent_summary": "Perform one finite companion Fishing participation cycle.",
                "motion_primitives": [
                    {"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "target": "", "duration": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS},
                ],
            },
            STATE_F3,
            build_allowed_pools(STATE_F3, _base_perception()),
        )
        self.assertTrue(result["valid"])

        too_short_lifetime = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 19.9,
                "intent_summary": "invalid under-budget f3 cast",
                "motion_primitives": [
                    {"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "target": "", "duration": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS},
                ],
            },
            STATE_F3,
            build_allowed_pools(STATE_F3, _base_perception()),
        )
        self.assertFalse(too_short_lifetime["valid"])
        self.assertIn("POLICY_DURATION_TOO_LONG", [error["code"] for error in too_short_lifetime["errors"]])

        not_f3 = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS,
                "intent_summary": "invalid f2 cast",
                "motion_primitives": [
                    {"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "target": "", "duration": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS},
                ],
            },
            STATE_F2,
            build_allowed_pools(STATE_F2, _base_perception()),
        )
        self.assertFalse(not_f3["valid"])

    def test_f3_public_allowed_pose_pool_is_transition_owned(self):
        pools = build_allowed_pools(STATE_F3, _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite"))
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
        self.assertEqual(pools["allowed_poses"], [])
        self.assertNotIn("fishing_cast", pools["allowed_poses"])
        self.assertNotIn("fishing_hold", pools["allowed_poses"])
        self.assertNotIn("fishing_reel", pools["allowed_poses"])
        self.assertNotIn("neutral", pools["allowed_poses"])
        self.assertNotIn("relaxed", pools["allowed_poses"])

    def test_f3_dynamic_fishing_targets_follow_runtime_availability(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        pools = build_allowed_pools(STATE_F3, perception)

        self.assertIn(TARGET_PLAYER_BOBBER, pools["allowed_targets"])
        self.assertIn(TARGET_PLAYER_FISHING_ROD, pools["allowed_targets"])

        perception_without_player_dynamic_targets = copy.deepcopy(perception)
        perception_without_player_dynamic_targets["scene"]["look_targets_available"] = [
            "player_head",
            "neutral_forward",
            "water_focus",
        ]
        filtered_pools = build_allowed_pools(STATE_F3, perception_without_player_dynamic_targets)

        self.assertNotIn(TARGET_PLAYER_BOBBER, filtered_pools["allowed_targets"])
        self.assertNotIn(TARGET_PLAYER_FISHING_ROD, filtered_pools["allowed_targets"])
        self.assertIn(TARGET_PLAYER_HEAD, filtered_pools["allowed_targets"])
        self.assertIn(TARGET_WATER_FOCUS, filtered_pools["allowed_targets"])

    def test_validator_rejects_unavailable_player_bobber_target_from_filtered_pool(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["scene"]["look_targets_available"] = ["player_head", "neutral_forward", "water_focus"]

        result = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 2.0,
                "intent_summary": "invalid unavailable bobber gaze",
                "motion_primitives": [
                    {"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_BOBBER, "duration": 1.0},
                    {"type": PRIMITIVE_HOLD_POSITION, "target": "", "duration": 1.0},
                ],
            },
            STATE_F3,
            build_allowed_pools(STATE_F3, perception),
        )

        self.assertFalse(result["valid"])
        self.assertIn("INVALID_TARGET", [error["code"] for error in result["errors"]])

    def test_f2_public_allowed_pose_pool_is_transition_owned(self):
        pools = build_allowed_pools(STATE_F2, _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady"))
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
        self.assertEqual(pools["allowed_poses"], [])

    def test_validator_rejects_public_f3_fishing_hand_pose_setters(self):
        for forbidden_pose in (POSE_FISHING_READY, "fishing_cast", "fishing_hold", "fishing_reel"):
            result = validate_policy_dict(
                {
                    "activity_mode": "parallel_engage",
                    "policy_lifetime": 2.0,
                    "intent_summary": "invalid internal public pose",
                    "motion_primitives": [
                        {"type": PRIMITIVE_SET_HAND_POSE, "pose": forbidden_pose, "duration": 0.5},
                        {"type": PRIMITIVE_HOLD_POSITION, "target": "", "duration": 1.0},
                    ],
                },
                STATE_F3,
                build_allowed_pools(STATE_F3, _base_perception()),
            )
            self.assertFalse(result["valid"])
            self.assertIn("POLICY_PRIMITIVE_TYPE_INVALID", [error["code"] for error in result["errors"]])

    def test_validator_rejects_multiple_cast_cycles_in_one_policy(self):
        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 3.0,
                "intent_summary": "invalid double cast",
                "motion_primitives": [
                    {"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "target": "", "duration": 1.0},
                    {"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "target": "", "duration": 1.0},
                ],
            },
            STATE_F3,
            build_allowed_pools(STATE_F3, _base_perception()),
        )
        self.assertFalse(result["valid"])
        self.assertIn("FISHING_CAST_CYCLE_REPEAT_FORBIDDEN", [error["code"] for error in result["errors"]])

    def test_public_prompt_does_not_expose_internal_fishing_cast_pose(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["companion"]["companion_fishing_rod_state"] = PROP_STATE_VISIBLE_IN_HAND
        perception["companion"]["fishing_rod_runtime_available"] = True
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY

        prompt_result = build_policy_prompt(perception, STATE_F3, build_allowed_pools(STATE_F3, perception))
        prompt = prompt_result["prompt_text"]
        self.assertIn(PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, prompt)
        self.assertNotIn("Allowed hand poses: relaxed, neutral, fishing_hold, fishing_cast", prompt)
        self.assertIn("Allowed hand poses: none.", prompt)
        self.assertIn('"policy_lifetime":20.0', prompt)
        self.assertIn('"duration":20.0', prompt)
        self.assertIn("companion_inside_fishing_interaction_area", prompt)
        self.assertIn("companion_at_valid_fishing_anchor", prompt)
        self.assertIn("fishing_rod_ready", prompt)
        self.assertIn("assigned_fishing_anchor_id", prompt)
        self.assertIn("assigned_fishing_cast_target_id", prompt)
        self.assertIn("companion_fishing_rod_state", prompt)
        self.assertIn("fishing_rod_runtime_available", prompt)
        self.assertIn("current_hand_pose", prompt)
        self.assertIn("F3 setup is already complete", prompt)
        self.assertIn("Do not emit set_companion_prop fishing_rod visible_in_hand", prompt)
        self.assertIn("Do not emit set_hand_pose fishing_ready", prompt)
        self.assertIn("strongly prefer exactly one perform_fishing_cast_cycle", prompt)

        s2_prompt = build_policy_prompt(
            _with_activity(_base_perception(), ACTIVITY_ID_CAMPFIRE, "ready"),
            STATE_S2,
            build_allowed_pools(STATE_S2, _base_perception()),
        )["prompt_text"]
        self.assertIn("The policy should stay short and state-appropriate.", s2_prompt)
        self.assertNotIn('"policy_lifetime":20.0', s2_prompt)

    def test_f3_prompt_uses_non_cast_followup_after_previous_cast_policy(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        memory_context = {"last_motion_primitive_types": [PRIMITIVE_PERFORM_FISHING_CAST_CYCLE]}

        prompt = build_policy_prompt(
            perception,
            STATE_F3,
            build_allowed_pools(STATE_F3, perception),
            policy_memory_context=memory_context,
        )["prompt_text"]

        self.assertIn("previous accepted Fishing policy already contained perform_fishing_cast_cycle", prompt)
        self.assertIn("Do not immediately cast again.", prompt)
        self.assertIn("Prefer one short non-cast F3 policy", prompt)

    def test_f2_transition_priority_requires_explicit_fishing_ready_pose(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True

        response, _ = evaluate_transition_priority(perception, _resolved(perception), TransitionQueue(), warnings=[])

        self.assertIsNotNone(response)
        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_TRANSITION_PRIORITY)

    def test_f3_internal_cycle_pose_does_not_recreate_entry_transition_for_non_recovery_roles(self):
        for request_role in ("current_policy", "lookahead_next_policy"):
            for pose in ("fishing_cast", "fishing_hold", "fishing_reel"):
                with self.subTest(request_role=request_role, pose=pose):
                    perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
                    perception["frame"]["request_role"] = request_role
                    perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
                    perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
                    perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
                    perception["entry_checks"]["fishing_rod_ready"] = True
                    perception["companion"]["current_hand_pose"] = pose
                    queue = TransitionQueue()

                    response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

                    self.assertIsNone(response)
                    self.assertEqual(warnings, [])
                    self.assertIsNone(queue.current_task())

    def test_f3_explicit_recovery_wins_over_internal_cycle_pose_exception(self):
        for pose in ("fishing_cast", "fishing_hold", "fishing_reel"):
            with self.subTest(pose=pose):
                perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
                perception["frame"]["request_role"] = "state_change_transition"
                perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
                perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
                perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
                perception["entry_checks"]["fishing_rod_ready"] = True
                perception["companion"]["current_hand_pose"] = pose
                queue = TransitionQueue()

                response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

                self.assertEqual(warnings, [])
                self.assertIsNotNone(response)
                self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_TRANSITION_PRIORITY)
                self.assertEqual(queue.current_task().transition_id, "f3_enter_assigned_fishing_anchor")
                self.assertEqual(response["motion_primitives"][-1]["type"], PRIMITIVE_SET_HAND_POSE)
                self.assertEqual(response["motion_primitives"][-1]["pose"], POSE_FISHING_READY)

    def test_f3_internal_cycle_pose_cannot_hide_spatial_failure(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = False
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = "fishing_cast"
        queue = TransitionQueue()

        response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

        self.assertEqual(warnings, [])
        self.assertIsNotNone(response)
        self.assertEqual(queue.current_task().transition_id, "f3_enter_assigned_fishing_anchor")

    def test_s2_transition_priority_requires_ready_pose_and_emits_entry_pose(self):
        perception = _base_perception()
        perception["task"]["campfire_started"] = True
        perception = _with_activity(perception, ACTIVITY_ID_CAMPFIRE, "ready")
        perception["entry_checks"]["companion_inside_interaction_area"] = True
        perception["companion"]["current_hand_pose"] = "neutral"
        queue = TransitionQueue()

        response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

        self.assertEqual(warnings, [])
        self.assertIsNotNone(response)
        self.assertEqual(queue.current_task().transition_id, "s2_entry_transition")
        self.assertIn("current_hand_pose == ready", queue.current_task().completion_check)
        self.assertEqual(response["motion_primitives"][-1]["type"], PRIMITIVE_SET_HAND_POSE)
        self.assertEqual(response["motion_primitives"][-1]["pose"], POSE_READY)

    def test_s3_transition_priority_requires_roast_pose_and_emits_entry_pose(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_CAMPFIRE, "engagement")
        perception["task"]["campfire_started"] = True
        perception["task"]["stick_picked"] = True
        perception["entry_checks"]["companion_at_valid_roasting_anchor"] = True
        perception["companion"]["current_hand_pose"] = "ready"
        queue = TransitionQueue()

        response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

        self.assertEqual(warnings, [])
        self.assertIsNotNone(response)
        self.assertEqual(queue.current_task().transition_id, "s3_entry_transition")
        self.assertIn("current_hand_pose == roast_pose", queue.current_task().completion_check)
        self.assertEqual(response["motion_primitives"][-1]["type"], PRIMITIVE_SET_HAND_POSE)
        self.assertEqual(response["motion_primitives"][-1]["pose"], POSE_ROAST_POSE)

    def test_f3_internal_cycle_pose_cannot_hide_rod_failure(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = False
        perception["companion"]["current_hand_pose"] = "fishing_reel"
        queue = TransitionQueue()

        response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

        self.assertEqual(warnings, [])
        self.assertIsNotNone(response)
        self.assertEqual(queue.current_task().transition_id, "f3_enter_assigned_fishing_anchor")

    def test_f3_stable_ready_pose_does_not_recreate_entry_transition(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY

        response, warnings = evaluate_transition_priority(perception, _resolved(perception), TransitionQueue(), warnings=[])

        self.assertIsNone(response)
        self.assertEqual(warnings, [])

    def test_f3_stale_neutral_pose_requires_entry_recovery(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = "neutral"
        queue = TransitionQueue()

        response, warnings = evaluate_transition_priority(perception, _resolved(perception), queue, warnings=[])

        self.assertEqual(warnings, [])
        self.assertIsNotNone(response)
        self.assertEqual(queue.current_task().transition_id, "f3_enter_assigned_fishing_anchor")

    def test_ai_enabled_fishing_path_returns_real_ai_policy(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        ai_result = {
            "success": True,
            "provider": "mock",
            "model": "mock-model",
            "latency_ms": 10.0,
            "warnings": [],
            "validator_error_codes": [],
            "validator_debug": {},
            "policy": {
                "activity_mode": "co_attention",
                "policy_lifetime": 3.0,
                "intent_summary": "AI fishing ready policy.",
                "motion_primitives": [
                    {"type": PRIMITIVE_LOOK_AT, "target": TARGET_WATER_FOCUS, "duration": 1.5},
                    {"type": PRIMITIVE_HOLD_POSITION, "target": "", "duration": 1.0},
                ],
            },
        }

        with patch("python_companion.policy_layer.config.AI_ENABLED", True), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True), patch(
            "python_companion.policy_layer.generate_ai_policy", return_value=ai_result
        ):
            response = generate_policy_response(perception, resolution, policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_AI)
        self.assertEqual(response["state"]["current_state"], STATE_F2)

    def test_ai_failure_with_fallback_enabled_returns_fallback(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", True), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True), patch(
            "python_companion.policy_layer.generate_ai_policy",
            return_value={"success": False, "error_status": AI_ERROR_PROVIDER_TIMEOUT, "error_message": "timeout", "warnings": [], "validator_error_codes": [], "validator_debug": {}},
        ):
            response = generate_policy_response(perception, resolution, policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)

    def test_ai_failure_with_fallback_disabled_returns_real_policy_error(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        perception["entry_checks"]["companion_inside_fishing_interaction_area"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", True), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", False), patch(
            "python_companion.policy_layer.generate_ai_policy",
            return_value={"success": False, "error_status": AI_ERROR_PROVIDER_TIMEOUT, "error_message": "timeout", "warnings": [], "validator_error_codes": [], "validator_debug": {}},
        ):
            response = generate_policy_response(perception, resolution, policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        self.assertEqual(response["policy"]["policy_source"], POLICY_SOURCE_POLICY_ERROR)
        self.assertFalse(response["control"]["execute_policy"])

    def test_repeated_incomplete_f2_dedups_transition_task(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        resolution = _resolved(perception)
        queue = TransitionQueue()

        evaluate_transition_priority(perception, resolution, queue, warnings=[])
        evaluate_transition_priority(perception, resolution, queue, warnings=[])

        self.assertEqual(queue.snapshot()["queue_length"], 1)
        self.assertEqual(queue.current_task().transition_id, "f2_enter_fishing_interaction_area")
        self.assertEqual(queue.current_task().attempt_count, 1)

    def test_f2_to_f3_cleanup_removes_obsolete_f2_transition(self):
        queue = TransitionQueue()
        f2 = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        evaluate_transition_priority(f2, _resolved(f2), queue, warnings=[])
        self.assertEqual(queue.current_task().transition_id, "f2_enter_fishing_interaction_area")

        f3 = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        f3["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        f3["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        evaluate_transition_priority(f3, _resolved(f3), queue, warnings=[])
        self.assertEqual(queue.current_task().transition_id, "f3_enter_assigned_fishing_anchor")

    def test_fishing_to_common_cleanup_removes_known_fishing_entry_transition(self):
        queue = TransitionQueue()
        fishing = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "ready", detail="BucketReady")
        evaluate_transition_priority(fishing, _resolved(fishing), queue, warnings=[])
        self.assertEqual(queue.current_task().transition_id, "f2_enter_fishing_interaction_area")

        common = _with_activity(_base_perception(), ACTIVITY_ID_COMMON, "free")
        response, warnings = evaluate_transition_priority(common, _resolved(common), queue, warnings=[])
        self.assertIsNone(response)
        self.assertEqual(warnings, [])
        self.assertIsNone(queue.current_task())

    def test_activity_memory_partitions_remain_isolated(self):
        store = ActivityPolicyMemoryStore()
        store.update_from_response(ACTIVITY_ID_FISHING, {"control": {"execute_policy": True}, "state": {"current_state": STATE_F3}, "policy": {"policy_source": POLICY_SOURCE_FALLBACK, "activity_mode": "parallel_engage"}, "motion_primitives": [{"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "duration": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS}], "debug": {}}, perception=_base_perception())
        store.update_from_response(ACTIVITY_ID_CAMPFIRE, {"control": {"execute_policy": True}, "state": {"current_state": STATE_S3}, "policy": {"policy_source": POLICY_SOURCE_FALLBACK, "activity_mode": "parallel_engage"}, "motion_primitives": [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0}], "debug": {}}, perception=_base_perception())

        fishing_prompt = build_policy_prompt(
            _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite"),
            STATE_F3,
            build_allowed_pools(STATE_F3, _base_perception()),
            policy_memory_summary=store.get_summary(ACTIVITY_ID_FISHING),
            policy_memory_context=store.get_prompt_context(ACTIVITY_ID_FISHING),
        )["prompt_text"]
        self.assertIn("last_state=F3_fishing_engagement", fishing_prompt)
        self.assertNotIn("last_state=S3_roasting_engagement", fishing_prompt)

    def test_f3_fallback_uses_twenty_second_cast_duration_and_covers_lifetime(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["companion"]["companion_fishing_rod_state"] = PROP_STATE_VISIBLE_IN_HAND
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(perception, resolution, policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        cast = next(primitive for primitive in response["motion_primitives"] if primitive["type"] == PRIMITIVE_PERFORM_FISHING_CAST_CYCLE)
        total_duration = sum(float(primitive["duration"]) for primitive in response["motion_primitives"])
        self.assertEqual(cast["duration"], FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS)
        self.assertGreaterEqual(response["policy"]["policy_lifetime"], total_duration)

    def test_f3_fallback_avoids_consecutive_cast_spam_when_memory_shows_last_cast(self):
        perception = _with_activity(_base_perception(), ACTIVITY_ID_FISHING, "engagement", detail="WaitingForBite")
        perception["companion"]["assigned_fishing_anchor_id"] = "Anchor_1"
        perception["companion"]["assigned_fishing_cast_target_id"] = "CastTarget_1"
        perception["entry_checks"]["companion_at_valid_fishing_anchor"] = True
        perception["entry_checks"]["fishing_rod_ready"] = True
        perception["companion"]["current_hand_pose"] = POSE_FISHING_READY
        resolution = _resolved(perception)

        store = ActivityPolicyMemoryStore()
        store.update_from_response(
            ACTIVITY_ID_FISHING,
            {
                "control": {"execute_policy": True},
                "state": {"current_state": STATE_F3},
                "policy": {"policy_source": POLICY_SOURCE_FALLBACK, "activity_mode": "parallel_engage"},
                "motion_primitives": [{"type": PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, "duration": FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS}],
                "debug": {},
            },
            perception=perception,
        )

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            response = generate_policy_response(perception, resolution, policy_memory=store.get_memory(ACTIVITY_ID_FISHING), transition_queue=TransitionQueue())

        primitive_types = [primitive["type"] for primitive in response["motion_primitives"]]
        self.assertNotIn(PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, primitive_types)

    def test_f2_and_f3_state_config_are_execution_ready(self):
        self.assertTrue(get_state_config(STATE_F2)["state_metadata"]["execution_ready"])
        self.assertTrue(get_state_config(STATE_F3)["state_metadata"]["execution_ready"])
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, get_state_config(STATE_F2)["motion_primitives"])
        self.assertIn(PRIMITIVE_PERFORM_FISHING_CAST_CYCLE, get_state_config(STATE_F3)["motion_primitives"])

    def test_campfire_s2_and_s3_regression_paths_still_work(self):
        s2 = _base_perception()
        s2["task"]["campfire_started"] = True
        s2 = _with_activity(s2, ACTIVITY_ID_CAMPFIRE, "ready")
        s2["entry_checks"]["companion_inside_interaction_area"] = True
        s2["companion"]["current_hand_pose"] = POSE_READY

        s3 = copy.deepcopy(s2)
        s3["task"]["stick_picked"] = True
        s3["activity"]["phase"] = "engagement"
        s3["entry_checks"]["companion_at_valid_roasting_anchor"] = True
        s3["companion"]["current_hand_pose"] = POSE_ROAST_POSE

        with patch("python_companion.policy_layer.config.AI_ENABLED", False), patch("python_companion.policy_layer.config.FALLBACK_ENABLED", True):
            s2_response = generate_policy_response(s2, _resolved(s2), policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_CAMPFIRE), transition_queue=TransitionQueue())
            s3_response = generate_policy_response(s3, _resolved(s3), policy_memory=ActivityPolicyMemoryStore().get_memory(ACTIVITY_ID_CAMPFIRE), transition_queue=TransitionQueue())

        self.assertEqual(s2_response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        self.assertIn(PRIMITIVE_MOVE_WITHIN_SCOPE, [primitive["type"] for primitive in s2_response["motion_primitives"]])
        self.assertEqual(s3_response["policy"]["policy_source"], POLICY_SOURCE_FALLBACK)
        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, [primitive["type"] for primitive in s3_response["motion_primitives"]])

    def test_companion_runtime_reset_clears_activity_partitions(self):
        runtime = CompanionRuntime()
        runtime.memory_store.update_from_response(ACTIVITY_ID_COMMON, {"control": {"execute_policy": True}, "state": {"current_state": STATE_S1}, "policy": {"policy_source": POLICY_SOURCE_FALLBACK}, "motion_primitives": [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0}], "debug": {}}, perception=_base_perception())
        runtime.memory_store.update_from_response(ACTIVITY_ID_FISHING, {"control": {"execute_policy": True}, "state": {"current_state": STATE_F2}, "policy": {"policy_source": POLICY_SOURCE_FALLBACK}, "motion_primitives": [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_WATER_FOCUS, "duration": 1.0}], "debug": {}}, perception=_base_perception())
        runtime.memory_store.update_from_response(ACTIVITY_ID_CAMPFIRE, {"control": {"execute_policy": True}, "state": {"current_state": STATE_S2}, "policy": {"policy_source": POLICY_SOURCE_FALLBACK}, "motion_primitives": [{"type": PRIMITIVE_LOOK_AT, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0}], "debug": {}}, perception=_base_perception())

        runtime.reset_session_state_for_unity_bootstrap("test_reset")
        self.assertEqual(runtime.memory_store.get_memory(ACTIVITY_ID_COMMON).last_state, "")
        self.assertEqual(runtime.memory_store.get_memory(ACTIVITY_ID_FISHING).last_state, "")
        self.assertEqual(runtime.memory_store.get_memory(ACTIVITY_ID_CAMPFIRE).last_state, "")


if __name__ == "__main__":
    unittest.main()
