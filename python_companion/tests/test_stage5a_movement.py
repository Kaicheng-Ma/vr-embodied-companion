import unittest

from python_companion.models import (
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_IDLE_MOTION,
    PRIMITIVE_LOOK_AT,
    SCOPE_AROUND_PLAYER,
    SCOPE_INTERACTION_AREA,
    SCOPE_TRANSITION_ONLY,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_PLAY_GESTURE,
    PRIMITIVE_SET_COMPANION_PROP,
    PRIMITIVE_SET_HAND_POSE,
    PROP_STATE_HIDDEN,
    PROP_STATE_VISIBLE_IN_HAND,
    PROP_STATE_VISIBLE_ON_STICK,
    STATE_S1,
    STATE_S2,
    STATE_S3,
    TARGET_CAMPFIRE_CENTER,
    TARGET_COMPANION_MARSHMALLOW,
    TARGET_INTERACTION_AREA,
    TARGET_NEAREST_FREE_ROASTING_ANCHOR,
    TARGET_NEAREST_VALID_INTERACTION_AREA_POINT,
    TARGET_NEUTRAL_FORWARD,
    TARGET_PLAYER_HEAD,
)
from python_companion.policy_validator import validate_policy_dict
from python_companion.response_builder import build_fallback_response
from python_companion.state_config import build_allowed_pools
from python_companion.state_layer import StateResolution
from python_companion.transition_priority import evaluate_transition_priority
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


def _base_perception():
    return {
        "frame": {"request_id": "test_request", "request_role": "current_policy", "current_state_epoch": 3, "policy_index": 7},
        "task": {"campfire_started": True, "stick_picked": False},
        "entry_checks": {
            "companion_inside_interaction_area": False,
            "companion_at_valid_roasting_anchor": False,
            "nearest_free_roasting_anchor_id": "Anchor_1",
            "nearest_valid_interaction_area_point_id": "interaction_area_entry",
        },
        "companion": {
            "current_state": STATE_S2,
            "previous_state": STATE_S1,
            "hand_pose_runtime_available": False,
            "companion_prop_runtime_available": False,
            "current_hand_pose": "neutral",
            "companion_stick_state": PROP_STATE_HIDDEN,
            "companion_marshmallow_state": PROP_STATE_HIDDEN,
            "has_companion_marshmallow": False,
        },
        "scene": {"campfire_center_available": True, "look_targets_available": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD]},
    }


class Stage5AMovementTests(unittest.TestCase):
    def test_stage5b1_state_pools_exclude_deferred_primitives(self):
        expected = {
            STATE_S1: [PRIMITIVE_LOOK_AT, PRIMITIVE_FACE_TOWARD, PRIMITIVE_MOVE_WITHIN_SCOPE, PRIMITIVE_HOLD_POSITION],
            STATE_S2: [PRIMITIVE_LOOK_AT, PRIMITIVE_FACE_TOWARD, PRIMITIVE_MOVE_WITHIN_SCOPE, PRIMITIVE_HOLD_POSITION],
            STATE_S3: [PRIMITIVE_LOOK_AT, PRIMITIVE_FACE_TOWARD, PRIMITIVE_HOLD_POSITION],
        }

        for state_id, expected_primitives in expected.items():
            with self.subTest(state_id=state_id):
                pools = build_allowed_pools(state_id)
                self.assertEqual(pools["motion_primitives"], expected_primitives)
                self.assertEqual(pools["allowed_poses"], [])
                self.assertEqual(pools["allowed_props"], [])
                self.assertEqual(pools["allowed_prop_states"], {})
                self.assertEqual(pools["allowed_gestures"], [])
                self.assertNotIn(PRIMITIVE_IDLE_MOTION, pools["motion_primitives"])
                self.assertNotIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
                self.assertNotIn(PRIMITIVE_SET_COMPANION_PROP, pools["motion_primitives"])
                self.assertNotIn(PRIMITIVE_PLAY_GESTURE, pools["motion_primitives"])

    def test_stage5b2_s1_effective_pool_when_hand_capability_ready(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S1
        perception["companion"]["hand_pose_runtime_available"] = True
        pools = build_allowed_pools(STATE_S1, perception)
        self.assertIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
        self.assertEqual(pools["allowed_poses"], ["relaxed", "neutral"])
        self.assertNotIn(PRIMITIVE_SET_COMPANION_PROP, pools["motion_primitives"])

    def test_stage5b2_capability_unavailable_keeps_hand_and_prop_primitives_absent(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        pools = build_allowed_pools(STATE_S3, perception)
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
        self.assertNotIn(PRIMITIVE_SET_COMPANION_PROP, pools["motion_primitives"])
        self.assertEqual(pools["allowed_poses"], [])
        self.assertEqual(pools["allowed_props"], [])

    def test_stage5b2_s2_effective_pool_is_capability_gated(self):
        perception = _base_perception()
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        pools = build_allowed_pools(STATE_S2, perception)
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
        self.assertIn(PRIMITIVE_SET_COMPANION_PROP, pools["motion_primitives"])
        self.assertEqual(pools["allowed_poses"], [])
        self.assertEqual(pools["allowed_props"], ["stick"])
        self.assertEqual(pools["allowed_prop_states"]["stick"], [PROP_STATE_VISIBLE_IN_HAND, PROP_STATE_HIDDEN])
        self.assertNotIn(TARGET_COMPANION_MARSHMALLOW, pools["allowed_targets"])

    def test_stage5b2_s3_effective_pool_adds_companion_marshmallow_target_only_when_visible(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        perception["companion"]["has_companion_marshmallow"] = True
        perception["companion"]["companion_marshmallow_state"] = PROP_STATE_VISIBLE_ON_STICK
        perception["scene"]["look_targets_available"].append(TARGET_COMPANION_MARSHMALLOW)
        pools = build_allowed_pools(STATE_S3, perception)
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, pools["motion_primitives"])
        self.assertIn(PRIMITIVE_SET_COMPANION_PROP, pools["motion_primitives"])
        self.assertEqual(pools["allowed_poses"], [])
        self.assertIn(TARGET_COMPANION_MARSHMALLOW, pools["allowed_targets"])
        self.assertEqual(pools["allowed_prop_states"]["stick"], [PROP_STATE_VISIBLE_IN_HAND])
        self.assertEqual(pools["allowed_prop_states"]["marshmallow"], [PROP_STATE_VISIBLE_ON_STICK])

    def test_stage5b2_s3_effective_pool_filters_stale_companion_marshmallow_target(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        perception["scene"]["look_targets_available"].append(TARGET_COMPANION_MARSHMALLOW)
        pools = build_allowed_pools(STATE_S3, perception)
        self.assertNotIn(TARGET_COMPANION_MARSHMALLOW, pools["allowed_targets"])

    def test_s2_transition_uses_transition_only_target(self):
        queue = TransitionQueue()
        perception = _base_perception()
        response, warnings = evaluate_transition_priority(perception, _state_resolution(STATE_S2), queue, warnings=[])
        self.assertIsNotNone(response)
        self.assertEqual(response["policy"]["policy_source"], "transition_priority")
        primitive = response["motion_primitives"][0]
        self.assertEqual(primitive["type"], "move_within_scope")
        self.assertEqual(primitive["scope"], SCOPE_TRANSITION_ONLY)
        self.assertEqual(primitive["target"], TARGET_NEAREST_VALID_INTERACTION_AREA_POINT)
        pose = response["motion_primitives"][-1]
        self.assertEqual(pose["type"], PRIMITIVE_SET_HAND_POSE)
        self.assertEqual(pose["pose"], "ready")
        self.assertEqual(queue.current_task().goal_target, TARGET_NEAREST_VALID_INTERACTION_AREA_POINT)
        self.assertEqual(warnings, [])

    def test_s3_transition_uses_transition_only_anchor_target(self):
        queue = TransitionQueue()
        perception = _base_perception()
        perception["task"]["stick_picked"] = True
        perception["companion"]["current_state"] = STATE_S3
        response, warnings = evaluate_transition_priority(perception, _state_resolution(STATE_S3), queue, warnings=[])
        self.assertIsNotNone(response)
        primitive = response["motion_primitives"][0]
        self.assertEqual(primitive["scope"], SCOPE_TRANSITION_ONLY)
        self.assertEqual(primitive["target"], TARGET_NEAREST_FREE_ROASTING_ANCHOR)
        pose = response["motion_primitives"][-1]
        self.assertEqual(pose["type"], PRIMITIVE_SET_HAND_POSE)
        self.assertEqual(pose["pose"], "roast_pose")
        self.assertEqual(queue.current_task().goal_target, TARGET_NEAREST_FREE_ROASTING_ANCHOR)
        self.assertEqual(warnings, [])

    def test_s3_normal_validator_rejects_move_within_scope(self):
        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid s3 move",
                "motion_primitives": [
                    {"type": "move_within_scope", "scope": SCOPE_INTERACTION_AREA, "target": TARGET_INTERACTION_AREA, "duration": 1.5}
                ],
            },
            STATE_S3,
            build_allowed_pools(STATE_S3),
        )
        self.assertFalse(result["valid"])
        self.assertTrue(
            any(code in {"S3_MOVE_FORBIDDEN", "FORBIDDEN_PRIMITIVE_TYPE"} for code in [error["code"] for error in result["errors"]])
        )

    def test_s2_normal_validator_rejects_anchor_transition_target(self):
        result = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid s2 anchor target",
                "motion_primitives": [
                    {"type": "move_within_scope", "scope": SCOPE_INTERACTION_AREA, "target": TARGET_NEAREST_FREE_ROASTING_ANCHOR, "duration": 1.5}
                ],
            },
            STATE_S2,
            build_allowed_pools(STATE_S2),
        )
        self.assertFalse(result["valid"])
        self.assertIn("INVALID_TARGET", [error["code"] for error in result["errors"]])

    def test_transition_only_rejected_for_normal_policy(self):
        result = validate_policy_dict(
            {
                "activity_mode": "co_attention",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid transition_only usage",
                "motion_primitives": [
                    {
                        "type": "move_within_scope",
                        "scope": SCOPE_TRANSITION_ONLY,
                        "target": TARGET_NEAREST_VALID_INTERACTION_AREA_POINT,
                        "duration": 1.5,
                    }
                ],
            },
            STATE_S2,
            build_allowed_pools(STATE_S2),
        )
        self.assertFalse(result["valid"])
        self.assertIn("INVALID_SCOPE", [error["code"] for error in result["errors"]])

    def test_radius_validation_rejects_inverted_range(self):
        result = validate_policy_dict(
            {
                "activity_mode": "idle_presence",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid radius",
                "motion_primitives": [
                    {
                        "type": "move_within_scope",
                        "scope": SCOPE_AROUND_PLAYER,
                        "target": "player_head",
                        "radius_min": 2.0,
                        "radius_max": 1.0,
                        "duration": 1.5,
                    }
                ],
            },
            STATE_S1,
            build_allowed_pools(STATE_S1),
        )
        self.assertFalse(result["valid"])
        self.assertIn("INVALID_RADIUS", [error["code"] for error in result["errors"]])

    def test_fallback_s1_can_emit_stage5a_movement(self):
        response = build_fallback_response(_base_perception(), _state_resolution(STATE_S1))
        primitives = response["motion_primitives"]
        self.assertEqual(primitives[0]["type"], "move_within_scope")
        self.assertEqual(primitives[0]["scope"], SCOPE_AROUND_PLAYER)
        self.assertEqual(primitives[1]["type"], "look_at")
        self.assertEqual(primitives[1]["target"], TARGET_PLAYER_HEAD)

    def test_fallback_s3_normal_does_not_move(self):
        response = build_fallback_response(_base_perception(), _state_resolution(STATE_S3))
        primitive_types = [primitive["type"] for primitive in response["motion_primitives"]]
        self.assertNotIn("move_within_scope", primitive_types)

    def test_stage5b1_validator_rejects_deferred_primitives(self):
        for state_id, primitive in (
            (STATE_S1, {"type": PRIMITIVE_IDLE_MOTION, "duration": 1.0}),
            (STATE_S2, {"type": PRIMITIVE_SET_HAND_POSE, "pose": "ready", "duration": 1.0}),
            (STATE_S2, {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": "visible_in_hand", "duration": 1.0}),
            (STATE_S3, {"type": PRIMITIVE_PLAY_GESTURE, "gesture": "small_nod", "duration": 1.0}),
        ):
            with self.subTest(state_id=state_id, primitive_type=primitive["type"]):
                result = validate_policy_dict(
                    {
                        "activity_mode": "idle_presence",
                        "policy_lifetime": 4.0,
                        "intent_summary": "deferred primitive should fail",
                        "motion_primitives": [primitive],
                    },
                    state_id,
                    build_allowed_pools(state_id),
                )
                self.assertFalse(result["valid"])
                self.assertIn("POLICY_PRIMITIVE_TYPE_INVALID", [error["code"] for error in result["errors"]])

    def test_stage5b2_validator_accepts_valid_prop_commands_without_public_hand_pose(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        pools = build_allowed_pools(STATE_S3, perception)
        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "valid stage5b2 setup",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.2},
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.2},
                    {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.0},
                ],
            },
            STATE_S3,
            pools,
        )
        self.assertTrue(result["valid"], msg=result["errors"])

    def test_stage5b2_validator_rejects_public_s3_set_hand_pose(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["hand_pose_runtime_available"] = True
        pools = build_allowed_pools(STATE_S3, perception)
        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "missing pose should fail",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_HAND_POSE, "pose": "roast_pose", "duration": 0.25}
                ],
            },
            STATE_S3,
            pools,
        )
        self.assertFalse(result["valid"])
        self.assertIn("POLICY_PRIMITIVE_TYPE_INVALID", [error["code"] for error in result["errors"]])

    def test_stage5b2_validator_rejects_missing_state_for_set_companion_prop(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["companion_prop_runtime_available"] = True
        pools = build_allowed_pools(STATE_S3, perception)
        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "missing prop state should fail",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "duration": 0.25}
                ],
            },
            STATE_S3,
            pools,
        )
        self.assertFalse(result["valid"])
        self.assertIn("INVALID_PROP_STATE", [error["code"] for error in result["errors"]])

    def test_stage5b2_validator_rejects_s2_marshmallow_and_roast_pose(self):
        perception = _base_perception()
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        pools = build_allowed_pools(STATE_S2, perception)
        prop_result = validate_policy_dict(
            {
                "activity_mode": "self_prepare",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid s2 marshmallow",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.2}
                ],
            },
            STATE_S2,
            pools,
        )
        self.assertFalse(prop_result["valid"])
        hand_result = validate_policy_dict(
            {
                "activity_mode": "self_prepare",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid s2 roast pose",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_HAND_POSE, "pose": "roast_pose", "duration": 0.2}
                ],
            },
            STATE_S2,
            pools,
        )
        self.assertFalse(hand_result["valid"])

    def test_stage5b2_validator_rejects_invalid_prop_state_combination(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["companion_prop_runtime_available"] = True
        pools = build_allowed_pools(STATE_S3, perception)
        result = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "invalid prop state",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_ON_STICK, "duration": 0.2}
                ],
            },
            STATE_S3,
            pools,
        )
        self.assertFalse(result["valid"])

    def test_stage5b2_validator_accepts_s2_hidden_stick_but_rejects_s3_hidden_prop_setters(self):
        s2_perception = _base_perception()
        s2_perception["companion"]["companion_prop_runtime_available"] = True
        s2_pools = build_allowed_pools(STATE_S2, s2_perception)
        s2_result = validate_policy_dict(
            {
                "activity_mode": "self_prepare",
                "policy_lifetime": 4.0,
                "intent_summary": "hide stick in s2",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_HIDDEN, "duration": 0.25}
                ],
            },
            STATE_S2,
            s2_pools,
        )
        self.assertTrue(s2_result["valid"], msg=s2_result["errors"])

        s3_perception = _base_perception()
        s3_perception["companion"]["current_state"] = STATE_S3
        s3_perception["companion"]["companion_prop_runtime_available"] = True
        s3_pools = build_allowed_pools(STATE_S3, s3_perception)
        s3_stick_hidden = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "hide stick in s3",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_HIDDEN, "duration": 0.25}
                ],
            },
            STATE_S3,
            s3_pools,
        )
        self.assertFalse(s3_stick_hidden["valid"])
        self.assertIn("INVALID_PROP_STATE", [error["code"] for error in s3_stick_hidden["errors"]])

        s3_marshmallow_hidden = validate_policy_dict(
            {
                "activity_mode": "parallel_engage",
                "policy_lifetime": 4.0,
                "intent_summary": "hide marshmallow in s3",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "marshmallow", "state": PROP_STATE_HIDDEN, "duration": 0.25}
                ],
            },
            STATE_S3,
            s3_pools,
        )
        self.assertFalse(s3_marshmallow_hidden["valid"])
        self.assertIn("INVALID_PROP_STATE", [error["code"] for error in s3_marshmallow_hidden["errors"]])

    def test_stage5b2_validator_preserves_visible_in_hand_prop_state_token(self):
        perception = _base_perception()
        perception["companion"]["companion_prop_runtime_available"] = True
        pools = build_allowed_pools(STATE_S2, perception)
        result = validate_policy_dict(
            {
                "activity_mode": "self_prepare",
                "policy_lifetime": 4.0,
                "intent_summary": "preserve stick state token",
                "motion_primitives": [
                    {"type": PRIMITIVE_SET_COMPANION_PROP, "prop": "stick", "state": PROP_STATE_VISIBLE_IN_HAND, "duration": 0.25}
                ],
            },
            STATE_S2,
            pools,
        )
        self.assertTrue(result["valid"], msg=result["errors"])
        self.assertEqual(result["policy"]["motion_primitives"][0]["state"], PROP_STATE_VISIBLE_IN_HAND)

    def test_stage5b2_fallback_always_prefixes_s3_roasting_prop_setup(self):
        perception = _base_perception()
        perception["companion"]["current_state"] = STATE_S3
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        response = build_fallback_response(perception, _state_resolution(STATE_S3))
        primitive_types = [primitive["type"] for primitive in response["motion_primitives"]]
        self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, primitive_types)
        self.assertEqual(response["motion_primitives"][0]["type"], PRIMITIVE_SET_COMPANION_PROP)
        self.assertEqual(response["motion_primitives"][0]["prop"], "stick")
        self.assertEqual(response["motion_primitives"][0]["state"], PROP_STATE_VISIBLE_IN_HAND)
        self.assertEqual(response["motion_primitives"][1]["type"], PRIMITIVE_SET_COMPANION_PROP)
        self.assertEqual(response["motion_primitives"][1]["prop"], "marshmallow")
        self.assertEqual(response["motion_primitives"][1]["state"], PROP_STATE_VISIBLE_ON_STICK)
        total_duration = sum(float(primitive.get("duration", 0.0)) for primitive in response["motion_primitives"])
        self.assertLessEqual(total_duration, response["policy"]["policy_lifetime"])

        perception["companion"]["current_hand_pose"] = "roast_pose"
        perception["companion"]["companion_stick_state"] = PROP_STATE_VISIBLE_IN_HAND
        perception["companion"]["companion_marshmallow_state"] = PROP_STATE_VISIBLE_ON_STICK
        response_no_duplicates = build_fallback_response(perception, _state_resolution(STATE_S3))
        duplicate_types = [primitive["type"] for primitive in response_no_duplicates["motion_primitives"]]
        self.assertNotIn(PRIMITIVE_SET_HAND_POSE, duplicate_types)
        self.assertEqual(response_no_duplicates["motion_primitives"][0]["prop"], "stick")
        self.assertEqual(response_no_duplicates["motion_primitives"][1]["prop"], "marshmallow")
        self.assertEqual(duplicate_types.count(PRIMITIVE_FACE_TOWARD), 1)
        face_index = next(i for i, primitive in enumerate(response_no_duplicates["motion_primitives"]) if primitive["type"] == PRIMITIVE_FACE_TOWARD)
        look_index = next(i for i, primitive in enumerate(response_no_duplicates["motion_primitives"]) if primitive["type"] == PRIMITIVE_LOOK_AT and primitive["target"] == TARGET_CAMPFIRE_CENTER)
        hold_index = next(i for i, primitive in enumerate(response_no_duplicates["motion_primitives"]) if primitive["type"] == PRIMITIVE_HOLD_POSITION)
        self.assertEqual(response_no_duplicates["motion_primitives"][face_index]["target"], TARGET_CAMPFIRE_CENTER)
        self.assertLess(hold_index, face_index)
        self.assertLess(face_index, look_index)

    def test_stage5b2_s3_fallback_gaze_cadence_uses_available_targets_without_movement(self):
        cases = [
            {"policy_index": 0, "look_targets": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD], "has_marshmallow": False, "marshmallow_state": PROP_STATE_HIDDEN, "expected_target": TARGET_CAMPFIRE_CENTER},
            {"policy_index": 1, "look_targets": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD], "has_marshmallow": False, "marshmallow_state": PROP_STATE_HIDDEN, "expected_target": TARGET_CAMPFIRE_CENTER},
            {"policy_index": 2, "look_targets": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD], "has_marshmallow": False, "marshmallow_state": PROP_STATE_HIDDEN, "expected_target": TARGET_PLAYER_HEAD},
            {"policy_index": 2, "look_targets": [TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD], "has_marshmallow": False, "marshmallow_state": PROP_STATE_HIDDEN, "expected_target": TARGET_PLAYER_HEAD},
            {"policy_index": 3, "look_targets": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD, TARGET_COMPANION_MARSHMALLOW], "has_marshmallow": True, "marshmallow_state": PROP_STATE_VISIBLE_ON_STICK, "expected_target": TARGET_COMPANION_MARSHMALLOW},
            {"policy_index": 3, "look_targets": [TARGET_PLAYER_HEAD, TARGET_CAMPFIRE_CENTER, TARGET_NEUTRAL_FORWARD, TARGET_COMPANION_MARSHMALLOW], "has_marshmallow": False, "marshmallow_state": PROP_STATE_HIDDEN, "expected_target": TARGET_CAMPFIRE_CENTER},
        ]

        for case in cases:
            with self.subTest(policy_index=case["policy_index"], expected_target=case["expected_target"]):
                perception = _base_perception()
                perception["frame"]["policy_index"] = case["policy_index"]
                perception["companion"]["current_state"] = STATE_S3
                perception["companion"]["hand_pose_runtime_available"] = True
                perception["companion"]["companion_prop_runtime_available"] = True
                perception["companion"]["has_companion_marshmallow"] = case["has_marshmallow"]
                perception["companion"]["companion_marshmallow_state"] = case["marshmallow_state"]
                perception["scene"]["look_targets_available"] = list(case["look_targets"])

                response = build_fallback_response(perception, _state_resolution(STATE_S3))
                primitives = response["motion_primitives"]
                primitive_types = [primitive["type"] for primitive in primitives]
                look_primitives = [primitive for primitive in primitives if primitive["type"] == PRIMITIVE_LOOK_AT]
                face_primitives = [primitive for primitive in primitives if primitive["type"] == PRIMITIVE_FACE_TOWARD]
                hold_primitives = [primitive for primitive in primitives if primitive["type"] == PRIMITIVE_HOLD_POSITION]

                self.assertEqual(len(look_primitives), 1)
                self.assertEqual(len(face_primitives), 1)
                self.assertEqual(len(hold_primitives), 1)
                self.assertEqual(look_primitives[0]["target"], case["expected_target"])
                self.assertEqual(face_primitives[0]["target"], TARGET_CAMPFIRE_CENTER)
                self.assertNotIn(PRIMITIVE_MOVE_WITHIN_SCOPE, primitive_types)
                self.assertEqual(primitives[0]["type"], PRIMITIVE_SET_COMPANION_PROP)
                self.assertEqual(primitives[1]["type"], PRIMITIVE_SET_COMPANION_PROP)

                validation = validate_policy_dict(
                    {
                        "activity_mode": response["policy"]["activity_mode"],
                        "policy_lifetime": response["policy"]["policy_lifetime"],
                        "intent_summary": response["policy"]["intent_summary"],
                        "motion_primitives": response["motion_primitives"],
                    },
                    STATE_S3,
                    build_allowed_pools(STATE_S3, perception),
                )
                self.assertTrue(validation["valid"], msg=validation["errors"])

    def test_stage5b1_validator_accepts_supported_normal_primitives(self):
        cases = (
            (
                STATE_S1,
                "co_attention",
                {"type": PRIMITIVE_LOOK_AT, "target": TARGET_PLAYER_HEAD, "duration": 1.0},
            ),
            (
                STATE_S1,
                "co_attention",
                {"type": PRIMITIVE_LOOK_AT, "target": TARGET_NEUTRAL_FORWARD, "duration": 1.0},
            ),
            (
                STATE_S2,
                "co_attention",
                {"type": PRIMITIVE_FACE_TOWARD, "target": TARGET_CAMPFIRE_CENTER, "duration": 1.0},
            ),
            (
                STATE_S1,
                "idle_presence",
                {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_AROUND_PLAYER, "target": "", "radius_min": 1.2, "radius_max": 2.5, "duration": 1.5},
            ),
            (
                STATE_S2,
                "co_attention",
                {"type": PRIMITIVE_MOVE_WITHIN_SCOPE, "scope": SCOPE_INTERACTION_AREA, "target": TARGET_INTERACTION_AREA, "duration": 1.5},
            ),
            (
                STATE_S3,
                "parallel_engage",
                {"type": PRIMITIVE_HOLD_POSITION, "duration": 1.5},
            ),
        )

        for state_id, activity_mode, primitive in cases:
            with self.subTest(state_id=state_id, primitive_type=primitive["type"]):
                result = validate_policy_dict(
                    {
                        "activity_mode": activity_mode,
                        "policy_lifetime": 4.0,
                        "intent_summary": "supported primitive should pass",
                        "motion_primitives": [primitive],
                    },
                    state_id,
                    build_allowed_pools(state_id),
                )
                self.assertTrue(result["valid"], msg=result["errors"])

    def test_stage5b1_fallback_excludes_deferred_primitives(self):
        for state_id in (STATE_S1, STATE_S2, STATE_S3):
            with self.subTest(state_id=state_id):
                response = build_fallback_response(_base_perception(), _state_resolution(state_id))
                primitive_types = [primitive["type"] for primitive in response["motion_primitives"]]
                self.assertNotIn(PRIMITIVE_IDLE_MOTION, primitive_types)
                self.assertNotIn(PRIMITIVE_SET_HAND_POSE, primitive_types)
                self.assertNotIn(PRIMITIVE_SET_COMPANION_PROP, primitive_types)
                self.assertNotIn(PRIMITIVE_PLAY_GESTURE, primitive_types)

    def test_stage5b2_s2_fallback_uses_longer_campfire_dwell_without_s3_payloads(self):
        perception = _base_perception()
        perception["companion"]["hand_pose_runtime_available"] = True
        perception["companion"]["companion_prop_runtime_available"] = True
        response = build_fallback_response(perception, _state_resolution(STATE_S2))
        primitives = response["motion_primitives"]
        move_primitives = [primitive for primitive in primitives if primitive["type"] == PRIMITIVE_MOVE_WITHIN_SCOPE]
        look_primitives = [
            primitive
            for primitive in primitives
            if primitive["type"] == PRIMITIVE_LOOK_AT and primitive.get("target") == TARGET_CAMPFIRE_CENTER
        ]

        self.assertEqual(len(move_primitives), 1)
        self.assertEqual(move_primitives[0]["scope"], SCOPE_INTERACTION_AREA)
        self.assertEqual(move_primitives[0]["target"], TARGET_INTERACTION_AREA)
        self.assertEqual(move_primitives[0]["duration"], 2.0)
        self.assertEqual(len(look_primitives), 1)
        self.assertEqual(look_primitives[0]["duration"], 5.0)
        total_duration = sum(float(primitive.get("duration", 0.0)) for primitive in primitives)
        self.assertLessEqual(total_duration, response["policy"]["policy_lifetime"])
        self.assertTrue(
            any(
                primitive["type"] == PRIMITIVE_SET_COMPANION_PROP
                and primitive.get("prop") == "stick"
                and primitive.get("state") == PROP_STATE_VISIBLE_IN_HAND
                for primitive in primitives
            )
        )

        for primitive in primitives:
            self.assertFalse(primitive["type"] == PRIMITIVE_SET_COMPANION_PROP and primitive.get("prop") == "marshmallow")
            self.assertFalse(primitive["type"] == PRIMITIVE_SET_HAND_POSE and primitive.get("pose") == "roast_pose")


if __name__ == "__main__":
    unittest.main()
