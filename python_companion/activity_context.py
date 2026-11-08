"""Unity activity-context parsing and validation helpers."""

from __future__ import annotations

from dataclasses import dataclass

from .models import (
    ACTIVITY_ID_CAMPFIRE,
    ACTIVITY_ID_COMMON,
    ACTIVITY_ID_FISHING,
    ACTIVITY_PHASE_ENGAGEMENT,
    ACTIVITY_PHASE_FREE,
    ACTIVITY_PHASE_READY,
    ERROR_INVALID_ACTIVITY_CONTEXT,
)


VALID_ACTIVITY_PHASES = {
    ACTIVITY_ID_COMMON: {ACTIVITY_PHASE_FREE},
    ACTIVITY_ID_FISHING: {ACTIVITY_PHASE_READY, ACTIVITY_PHASE_ENGAGEMENT},
    ACTIVITY_ID_CAMPFIRE: {ACTIVITY_PHASE_READY, ACTIVITY_PHASE_ENGAGEMENT},
}


@dataclass
class ActivityContext:
    activity_id: str = ""
    activity_phase: str = ""
    detail_phase: str = "none"
    fishing_completed: bool = False
    context_key: str = ""
    context_epoch: int = -1
    is_present: bool = False
    is_valid: bool = False
    error_code: str = ""
    error_message: str = ""


def _invalid(message):
    return ActivityContext(
        is_present=True,
        is_valid=False,
        error_code=ERROR_INVALID_ACTIVITY_CONTEXT,
        error_message=message,
    )


def parse_activity_context(perception):
    if not isinstance(perception, dict):
        return ActivityContext()

    activity = perception.get("activity")
    if not isinstance(activity, dict):
        return ActivityContext()

    activity_id = str(activity.get("id", "") or "").strip().lower()
    activity_phase = str(activity.get("phase", "") or "").strip().lower()
    detail_phase = str(activity.get("detail_phase", "none") or "none").strip()
    fishing_completed = bool(activity.get("fishing_completed"))
    context_key = str(activity.get("context_key", "") or "").strip()

    try:
        context_epoch = int(activity.get("context_epoch"))
    except (TypeError, ValueError):
        context_epoch = -1

    if activity_id not in VALID_ACTIVITY_PHASES:
        return _invalid(f"Unknown activity id '{activity_id}'.")

    if activity_phase not in VALID_ACTIVITY_PHASES[activity_id]:
        return _invalid(f"Invalid activity phase '{activity_phase}' for activity '{activity_id}'.")

    if activity_id == ACTIVITY_ID_FISHING and fishing_completed:
        return _invalid("Fishing activity cannot remain active when fishing_completed is true.")

    return ActivityContext(
        activity_id=activity_id,
        activity_phase=activity_phase,
        detail_phase=detail_phase or "none",
        fishing_completed=fishing_completed,
        context_key=context_key,
        context_epoch=context_epoch,
        is_present=True,
        is_valid=True,
    )
