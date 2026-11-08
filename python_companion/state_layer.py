"""Rule-based state resolution for the local Python runtime."""

from __future__ import annotations

from dataclasses import dataclass

from .activity_context import parse_activity_context
from .models import (
    ACTIVITY_ID_CAMPFIRE,
    ACTIVITY_ID_COMMON,
    ACTIVITY_ID_FISHING,
    ACTIVITY_PHASE_ENGAGEMENT,
    ACTIVITY_PHASE_READY,
    ERROR_INVALID_ACTIVITY_CONTEXT,
    ERROR_MISSING_REQUIRED_FIELDS,
    STATE_F2,
    STATE_F3,
    STATE_S1,
    STATE_S2,
    STATE_S3,
    STATE_UNKNOWN,
)


@dataclass
class StateResolution:
    current_state: str
    internal_state: str
    activity_id: str
    previous_state: str
    state_changed: bool
    state_reason: str
    activity_context: object | None = None
    is_valid: bool = True
    error_code: str = ""
    error_message: str = ""


def _invalid_resolution(message, error_code=ERROR_MISSING_REQUIRED_FIELDS):
    return StateResolution(
        current_state=STATE_UNKNOWN,
        internal_state=STATE_UNKNOWN,
        activity_id="",
        previous_state="",
        state_changed=False,
        state_reason=message,
        is_valid=False,
        error_code=error_code,
        error_message=message,
    )


def _resolve_legacy_campfire_state(task):
    campfire_started = bool(task.get("campfire_started"))
    stick_picked = bool(task.get("stick_picked"))

    if not campfire_started:
        return STATE_S1, ACTIVITY_ID_COMMON, "Common free-companion state is active."
    if stick_picked:
        return STATE_S3, ACTIVITY_ID_CAMPFIRE, "Campfire has started and the stick is picked."
    return STATE_S2, ACTIVITY_ID_CAMPFIRE, "Campfire has started and the stick is not picked yet."


def _resolve_activity_state(activity_context, task):
    activity_id = activity_context.activity_id
    activity_phase = activity_context.activity_phase
    campfire_started = bool(task.get("campfire_started"))
    stick_picked = bool(task.get("stick_picked"))

    if activity_id == ACTIVITY_ID_COMMON:
        if campfire_started:
            return _invalid_resolution(
                "Activity context reported common/free while task.campfire_started is true.",
                error_code=ERROR_INVALID_ACTIVITY_CONTEXT,
            )
        return StateResolution(
            current_state=STATE_S1,
            internal_state=STATE_S1,
            activity_id=activity_id,
            previous_state="",
            state_changed=False,
            state_reason="Common free-companion state is active.",
            activity_context=activity_context,
        )

    if activity_id == ACTIVITY_ID_FISHING:
        internal_state = STATE_F2 if activity_phase == ACTIVITY_PHASE_READY else STATE_F3
        executable_state = internal_state
        reason = f"Fishing activity context resolved successfully ({activity_phase})."
        return StateResolution(
            current_state=executable_state,
            internal_state=internal_state,
            activity_id=activity_id,
            previous_state="",
            state_changed=False,
            state_reason=reason,
            activity_context=activity_context,
        )

    if not campfire_started:
        return _invalid_resolution(
            "Activity context reported campfire while task.campfire_started is false.",
            error_code=ERROR_INVALID_ACTIVITY_CONTEXT,
        )
    if activity_phase == ACTIVITY_PHASE_READY and stick_picked:
        return _invalid_resolution(
            "Campfire ready activity is inconsistent with task.stick_picked = true.",
            error_code=ERROR_INVALID_ACTIVITY_CONTEXT,
        )
    if activity_phase == ACTIVITY_PHASE_ENGAGEMENT and not stick_picked:
        return _invalid_resolution(
            "Campfire engagement activity is inconsistent with task.stick_picked = false.",
            error_code=ERROR_INVALID_ACTIVITY_CONTEXT,
        )

    current_state = STATE_S2 if activity_phase == ACTIVITY_PHASE_READY else STATE_S3
    reason = "Campfire activity context resolved successfully."
    return StateResolution(
        current_state=current_state,
        internal_state=current_state,
        activity_id=activity_id,
        previous_state="",
        state_changed=False,
        state_reason=reason,
        activity_context=activity_context,
    )


def resolve_state(perception, memory=None):
    task = perception.get("task") if isinstance(perception, dict) else None
    companion = perception.get("companion") if isinstance(perception, dict) else None

    if not isinstance(task, dict):
        return _invalid_resolution("Perception task section is missing.")

    if "campfire_started" not in task or "stick_picked" not in task:
        return _invalid_resolution("Perception task section is missing required fields.")

    activity_context = parse_activity_context(perception)
    if activity_context.is_present:
        if not activity_context.is_valid:
            return _invalid_resolution(
                activity_context.error_message or "Activity context is invalid.",
                error_code=activity_context.error_code or ERROR_INVALID_ACTIVITY_CONTEXT,
            )
        resolution = _resolve_activity_state(activity_context, task)
        if not resolution.is_valid:
            return resolution
        current_state = resolution.current_state
        internal_state = resolution.internal_state
        activity_id = resolution.activity_id
        reason = resolution.state_reason
    else:
        current_state, activity_id, reason = _resolve_legacy_campfire_state(task)
        internal_state = current_state

    previous_state = ""
    if isinstance(companion, dict) and isinstance(companion.get("previous_state"), str):
        previous_state = companion.get("previous_state") or ""
    elif memory is not None and getattr(memory, "last_state", ""):
        previous_state = memory.last_state
    else:
        previous_state = current_state

    state_changed = previous_state != current_state
    return StateResolution(
        current_state=current_state,
        internal_state=internal_state,
        activity_id=activity_id,
        previous_state=previous_state,
        state_changed=state_changed,
        state_reason=reason,
        activity_context=activity_context if activity_context.is_present else None,
    )
