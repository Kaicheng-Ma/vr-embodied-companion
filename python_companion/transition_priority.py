"""Transition-priority response generation."""

from .config import TRANSITION_POLICY_LIFETIME
from .models import (
    ACTIVITY_ID_FISHING,
    ERROR_TRANSITION_TASK_MAX_ATTEMPTS,
    POSE_READY,
    POSE_ROAST_POSE,
    STATE_F2,
    STATE_F3,
    TARGET_ASSIGNED_FISHING_ANCHOR,
    TARGET_NEAREST_FREE_ROASTING_ANCHOR,
    TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT,
    TARGET_NEAREST_VALID_INTERACTION_AREA_POINT,
    STATE_S2,
    STATE_S3,
)
from .response_builder import build_policy_error_response, build_transition_priority_response
from .transition_queue import TransitionTask

KNOWN_ENTRY_TRANSITION_IDS = {
    "f2_enter_fishing_interaction_area",
    "f3_enter_assigned_fishing_anchor",
    "s2_entry_transition",
    "s3_entry_transition",
}


def _valid_entry_transition_ids_for_state(state_resolution):
    internal_state = state_resolution.internal_state if state_resolution else ""
    activity_id = state_resolution.activity_id if state_resolution else ""
    current_state = state_resolution.current_state if state_resolution else ""

    if activity_id == ACTIVITY_ID_FISHING:
        if internal_state == STATE_F2:
            return {"f2_enter_fishing_interaction_area"}
        if internal_state == STATE_F3:
            return {"f3_enter_assigned_fishing_anchor"}
        return set()

    if current_state == STATE_S2:
        return {"s2_entry_transition"}
    if current_state == STATE_S3:
        return {"s3_entry_transition"}
    return set()


def reconcile_obsolete_entry_transitions(state_resolution, transition_queue):
    if transition_queue is None:
        return
    valid_transition_ids = _valid_entry_transition_ids_for_state(state_resolution)
    transition_queue.remove_known_obsolete_entry_transitions(valid_transition_ids, KNOWN_ENTRY_TRANSITION_IDS)


def evaluate_transition_priority(perception, state_resolution, transition_queue, warnings=None):
    warnings = list(warnings or [])
    current_state = state_resolution.current_state if state_resolution else ""
    internal_state = state_resolution.internal_state if state_resolution else ""
    activity_id = state_resolution.activity_id if state_resolution else ""
    entry_checks = perception.get("entry_checks") if isinstance(perception, dict) else {}
    frame = perception.get("frame") if isinstance(perception, dict) else {}
    request_role = str(frame.get("request_role", "") or "").strip().lower() if isinstance(frame, dict) else ""

    reconcile_obsolete_entry_transitions(state_resolution, transition_queue)

    if activity_id == ACTIVITY_ID_FISHING and internal_state == STATE_F2:
        inside_fishing_area = bool(entry_checks.get("companion_inside_fishing_interaction_area")) if isinstance(entry_checks, dict) else False
        fishing_rod_ready = bool(entry_checks.get("fishing_rod_ready")) if isinstance(entry_checks, dict) else False
        companion = perception.get("companion") if isinstance(perception, dict) else {}
        current_hand_pose = str(companion.get("current_hand_pose", "") or "").strip().lower() if isinstance(companion, dict) else ""
        fishing_ready = current_hand_pose == "fishing_ready"
        entry_ready = inside_fishing_area and fishing_rod_ready and fishing_ready
        if entry_ready:
            transition_queue.mark_completed("f2_enter_fishing_interaction_area")
            return None, warnings

        transition_reason = (
            "entry_checks.companion_inside_fishing_interaction_area == false or "
            "entry_checks.fishing_rod_ready == false or "
            "companion.current_hand_pose != fishing_ready."
        )
        transition_task = TransitionTask(
            transition_id="f2_enter_fishing_interaction_area",
            goal_type="enter_fishing_interaction_area",
            goal_target=TARGET_NEAREST_VALID_FISHING_INTERACTION_AREA_POINT,
            status="running",
            completion_check="entry_checks.companion_inside_fishing_interaction_area == true && entry_checks.fishing_rod_ready == true && companion.current_hand_pose == fishing_ready",
            entry_block_reason=transition_reason,
            notes="Stage D1 transition task: enter the Fishing interaction area and finish visible Fishing-ready preparation.",
        )
        transition_queue.upsert_task(transition_task)
        return build_transition_priority_response(
            perception,
            state_resolution,
            transition_queue.snapshot(),
            transition_reason,
            warnings=warnings,
        ), warnings

    if activity_id == ACTIVITY_ID_FISHING and internal_state == STATE_F3:
        companion = perception.get("companion") if isinstance(perception, dict) else {}
        assigned_fishing_anchor_id = companion.get("assigned_fishing_anchor_id", "") if isinstance(companion, dict) else ""
        at_valid_anchor = bool(entry_checks.get("companion_at_valid_fishing_anchor")) if isinstance(entry_checks, dict) else False
        fishing_rod_ready = bool(entry_checks.get("fishing_rod_ready")) if isinstance(entry_checks, dict) else False
        current_hand_pose = str(companion.get("current_hand_pose", "") or "").strip().lower() if isinstance(companion, dict) else ""
        base_ready = bool(assigned_fishing_anchor_id) and at_valid_anchor and fishing_rod_ready
        fishing_ready = current_hand_pose == "fishing_ready"
        internal_cycle_pose = current_hand_pose in {"fishing_cast", "fishing_hold", "fishing_reel"}
        explicit_transition_recovery = request_role == "state_change_transition"
        entry_ready = base_ready and fishing_ready

        if not assigned_fishing_anchor_id:
            return None, warnings

        if entry_ready:
            transition_queue.mark_completed("f3_enter_assigned_fishing_anchor")
            return None, warnings

        # Python cannot see MotionExecutor ownership. Current/lookahead requests
        # may treat internal cycle poses as an active already-entered F3 runtime,
        # but an explicit Scheduler transition recovery must still be honored.
        if base_ready and internal_cycle_pose and not explicit_transition_recovery:
            transition_queue.mark_completed("f3_enter_assigned_fishing_anchor")
            return None, warnings

        transition_reason = (
            "entry_checks.companion_at_valid_fishing_anchor == false or "
            "entry_checks.fishing_rod_ready == false or "
            "companion.current_hand_pose != fishing_ready."
        )
        transition_task = TransitionTask(
            transition_id="f3_enter_assigned_fishing_anchor",
            goal_type="enter_assigned_fishing_anchor",
            goal_target=TARGET_ASSIGNED_FISHING_ANCHOR,
            status="running",
            completion_check="companion.assigned_fishing_anchor_id != '' && entry_checks.companion_at_valid_fishing_anchor == true && entry_checks.fishing_rod_ready == true && companion.current_hand_pose == fishing_ready",
            entry_block_reason=transition_reason,
            notes="Stage D1 transition task: move to the assigned Fishing anchor and finish visible Fishing-ready preparation.",
        )
        transition_queue.upsert_task(transition_task)
        return build_transition_priority_response(
            perception,
            state_resolution,
            transition_queue.snapshot(),
            transition_reason,
            warnings=warnings,
        ), warnings

    if current_state in {STATE_F2, STATE_F3}:
        return None, warnings

    if current_state == STATE_S2:
        companion = perception.get("companion") if isinstance(perception, dict) else {}
        current_hand_pose = str(companion.get("current_hand_pose", "") or "").strip().lower() if isinstance(companion, dict) else ""
        inside_area = bool(entry_checks.get("companion_inside_interaction_area")) if isinstance(entry_checks, dict) else False
        ready_pose = current_hand_pose == POSE_READY
        entry_ready = inside_area and ready_pose
        if entry_ready:
            transition_queue.mark_completed("s2_entry_transition")
            return None, warnings

        transition_reason = "companion_inside_interaction_area is false or companion.current_hand_pose != ready."
        transition_task = TransitionTask(
            transition_id="s2_entry_transition",
            goal_type="enter_interaction_area",
            goal_target=TARGET_NEAREST_VALID_INTERACTION_AREA_POINT,
            status="running",
            completion_check="entry_checks.companion_inside_interaction_area == true && companion.current_hand_pose == ready",
            entry_block_reason=transition_reason,
            notes="Stage 5A transition task: move to the nearest valid interaction-area entry point and finish ready hand pose.",
        )
        transition_queue.upsert_task(transition_task)
        return build_transition_priority_response(
            perception,
            state_resolution,
            transition_queue.snapshot(),
            transition_reason,
            warnings=warnings,
        ), warnings

    if current_state == STATE_S3:
        companion = perception.get("companion") if isinstance(perception, dict) else {}
        current_hand_pose = str(companion.get("current_hand_pose", "") or "").strip().lower() if isinstance(companion, dict) else ""
        at_valid_anchor = bool(entry_checks.get("companion_at_valid_roasting_anchor")) if isinstance(entry_checks, dict) else False
        roast_pose = current_hand_pose == POSE_ROAST_POSE
        entry_ready = at_valid_anchor and roast_pose
        if entry_ready:
            transition_queue.mark_completed("s3_entry_transition")
            return None, warnings

        transition_reason = "companion_at_valid_roasting_anchor is false or companion.current_hand_pose != roast_pose."
        transition_task = TransitionTask(
            transition_id="s3_entry_transition",
            goal_type="enter_roasting_anchor",
            goal_target=TARGET_NEAREST_FREE_ROASTING_ANCHOR,
            status="running",
            completion_check="entry_checks.companion_at_valid_roasting_anchor == true && companion.current_hand_pose == roast_pose",
            entry_block_reason=transition_reason,
            notes="Stage 5A transition task: move to the nearest free roasting anchor and finish roast_pose hand pose.",
        )
        transition_queue.upsert_task(transition_task)
        return build_transition_priority_response(
            perception,
            state_resolution,
            transition_queue.snapshot(),
            transition_reason,
            warnings=warnings,
        ), warnings

    return None, warnings


def enforce_transition_limits(perception, state_resolution, transition_queue, warnings=None):
    warnings = list(warnings or [])
    task = transition_queue.current_task()
    if task is None:
        return None, warnings

    if task.attempt_count >= task.max_attempts:
        error_message = "Transition task exceeded max attempts."
        warnings.append(error_message)
        return build_policy_error_response(
            perception,
            state_resolution,
            ERROR_TRANSITION_TASK_MAX_ATTEMPTS,
            error_message,
            warnings=warnings,
        ), warnings

    return None, warnings
