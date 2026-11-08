"""AI-first policy selection for the local Python runtime."""

from __future__ import annotations

from copy import deepcopy

from . import config
from .ai_policy_client import generate_ai_policy
from .models import (
    AI_ERROR_DISABLED,
    AI_ERROR_FORCE_FALLBACK_FOR_TEST,
    AI_ERROR_UNKNOWN,
)
from .response_builder import build_ai_response, build_fallback_response, build_policy_error_response
from .state_config import build_allowed_pools
from .transition_priority import enforce_transition_limits, evaluate_transition_priority


def _base_ai_debug():
    return {
        "ai_enabled": bool(config.AI_ENABLED),
        "fallback_enabled": bool(config.FALLBACK_ENABLED),
        "force_fallback_for_test": bool(config.FORCE_FALLBACK_FOR_TEST),
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
    }


def _copy_transition_queue_snapshot(transition_queue):
    if transition_queue is None:
        return None
    snapshot = transition_queue.snapshot()
    return deepcopy(snapshot) if isinstance(snapshot, dict) else snapshot


def _merge_ai_result_debug(ai_debug, ai_result, ai_status, fallback_reason=""):
    debug = _base_ai_debug()
    debug.update(ai_debug or {})
    debug["ai_status"] = ai_status
    debug["ai_error_status"] = ""
    debug["ai_error_message"] = ""
    debug["provider"] = ai_result.get("provider", "") if isinstance(ai_result, dict) else ""
    debug["model"] = ai_result.get("model", "") if isinstance(ai_result, dict) else ""
    debug["latency_ms"] = ai_result.get("latency_ms", 0.0) if isinstance(ai_result, dict) else 0.0
    debug["validator_error_codes"] = list(ai_result.get("validator_error_codes", [])) if isinstance(ai_result, dict) else []
    debug["validator_warnings"] = list(ai_result.get("warnings", [])) if isinstance(ai_result, dict) else []
    debug["validator_debug"] = deepcopy(ai_result.get("validator_debug", {})) if isinstance(ai_result, dict) and isinstance(ai_result.get("validator_debug"), dict) else {}
    debug["fallback_reason"] = fallback_reason
    return debug


def _merge_ai_failure_debug(ai_debug, ai_result, ai_status, fallback_reason=""):
    debug = _merge_ai_result_debug(ai_debug, ai_result, ai_status, fallback_reason=fallback_reason)
    if isinstance(ai_result, dict):
        debug["ai_error_status"] = ai_result.get("error_status") or AI_ERROR_UNKNOWN
        debug["ai_error_message"] = ai_result.get("error_message") or "AI policy generation failed."
        debug["validator_error_codes"] = list(ai_result.get("validator_error_codes", []))
        debug["validator_warnings"] = list(ai_result.get("warnings", []))
        debug["validator_debug"] = deepcopy(ai_result.get("validator_debug", {})) if isinstance(ai_result.get("validator_debug"), dict) else {}
    return debug


def _build_fallback_reason(ai_result):
    if not isinstance(ai_result, dict):
        return "AI policy generation failed."

    error_message = ai_result.get("error_message") or ""
    error_status = ai_result.get("error_status") or AI_ERROR_UNKNOWN
    if error_message:
        return f"{error_status}: {error_message}"
    return error_status


def _apply_ai_debug_fields(response, ai_debug, ai_status, fallback_reason="", ai_error_status="", ai_error_message=""):
    if not isinstance(response, dict):
        return response

    debug_section = response.get("debug") if isinstance(response.get("debug"), dict) else None
    if debug_section is None:
        return response

    merged = _base_ai_debug()
    merged.update(ai_debug or {})
    merged["ai_status"] = ai_status
    merged["fallback_reason"] = fallback_reason
    merged["ai_error_status"] = ai_error_status
    merged["ai_error_message"] = ai_error_message
    debug_section["ai_enabled"] = merged["ai_enabled"]
    debug_section["fallback_enabled"] = merged["fallback_enabled"]
    debug_section["force_fallback_for_test"] = merged["force_fallback_for_test"]
    debug_section["ai_status"] = merged["ai_status"]
    debug_section["ai_error_status"] = merged["ai_error_status"]
    debug_section["ai_error_message"] = merged["ai_error_message"]
    debug_section["provider"] = merged["provider"]
    debug_section["model"] = merged["model"]
    debug_section["latency_ms"] = merged["latency_ms"]
    debug_section["validator_error_codes"] = list(merged["validator_error_codes"])
    debug_section["validator_warnings"] = list(merged["validator_warnings"])
    debug_section["validator_debug"] = deepcopy(merged["validator_debug"])
    debug_section["fallback_reason"] = merged["fallback_reason"]
    return response


def generate_policy_response(perception, state_resolution, policy_memory=None, transition_queue=None):
    """Resolve transition priority, then AI, then fallback, then policy_error."""

    current_state = state_resolution.current_state if state_resolution else ""
    current_activity_id = state_resolution.activity_id if state_resolution else ""
    allowed_pools = build_allowed_pools(current_state, perception)
    policy_memory_summary = policy_memory.build_summary() if policy_memory is not None else ""
    policy_memory_context = policy_memory.build_prompt_context() if policy_memory is not None else {}
    ai_debug_base = _base_ai_debug()

    transition_response, transition_warnings = evaluate_transition_priority(
        perception,
        state_resolution,
        transition_queue,
        warnings=[],
    )
    limit_response, limit_warnings = enforce_transition_limits(
        perception,
        state_resolution,
        transition_queue,
        warnings=transition_warnings,
    )
    all_warnings = list(limit_warnings or [])

    if limit_response is not None:
        _apply_ai_debug_fields(
            limit_response,
            ai_debug_base,
            "transition_limit_exceeded",
            fallback_reason="Transition task exceeded max attempts.",
            ai_error_status=limit_response.get("debug", {}).get("error_code", "") if isinstance(limit_response.get("debug"), dict) else "",
            ai_error_message=limit_response.get("debug", {}).get("error_message", "") if isinstance(limit_response.get("debug"), dict) else "",
        )
        return limit_response

    if transition_response is not None:
        _apply_ai_debug_fields(
            transition_response,
            ai_debug_base,
            "skipped_transition_priority",
            fallback_reason="transition_priority",
        )
        return transition_response

    queue_snapshot = _copy_transition_queue_snapshot(transition_queue)

    if config.FORCE_FALLBACK_FOR_TEST:
        ai_debug = _merge_ai_failure_debug(
            ai_debug_base,
            {
                "error_status": AI_ERROR_FORCE_FALLBACK_FOR_TEST,
                "error_message": "FORCE_FALLBACK_FOR_TEST is enabled.",
                "validator_error_codes": [],
                "warnings": [],
                "validator_debug": {},
            },
            "force_fallback_for_test",
            fallback_reason="FORCE_FALLBACK_FOR_TEST is enabled.",
        )
        if config.FALLBACK_ENABLED:
            return build_fallback_response(
                perception,
                state_resolution,
                queue_state=queue_snapshot,
                warnings=all_warnings,
                ai_debug=ai_debug,
                policy_memory_context=policy_memory_context,
            )
        return build_policy_error_response(
            perception,
            state_resolution,
            AI_ERROR_FORCE_FALLBACK_FOR_TEST,
            "FORCE_FALLBACK_FOR_TEST is enabled and fallback is disabled.",
            warnings=all_warnings if all_warnings else ["FORCE_FALLBACK_FOR_TEST is enabled and fallback is disabled."],
            ai_debug=ai_debug,
        )

    if config.AI_ENABLED:
        ai_result = generate_ai_policy(
            perception,
            current_state,
            allowed_pools,
            policy_memory_summary=policy_memory_summary,
            policy_memory_context=policy_memory_context,
        )
        if ai_result.get("success"):
            ai_debug = _merge_ai_result_debug(ai_debug_base, ai_result, "success")
            return build_ai_response(
                perception,
                state_resolution,
                ai_result,
                queue_state=queue_snapshot,
                warnings=list(all_warnings) + list(ai_result.get("warnings", [])),
                ai_debug=ai_debug,
            )

        fallback_reason = _build_fallback_reason(ai_result)
        ai_debug = _merge_ai_failure_debug(ai_debug_base, ai_result, "failed", fallback_reason=fallback_reason)
        if isinstance(ai_debug.get("validator_debug"), dict):
            ai_debug["validator_debug"]["fallback_replaced_invalid_ai_policy"] = bool(config.FALLBACK_ENABLED)

        if config.FALLBACK_ENABLED:
            return build_fallback_response(
                perception,
                state_resolution,
                queue_state=queue_snapshot,
                warnings=list(all_warnings) + list(ai_result.get("warnings", [])),
                ai_debug=ai_debug,
                policy_memory_context=policy_memory_context,
            )

        return build_policy_error_response(
            perception,
            state_resolution,
            ai_result.get("error_status") or AI_ERROR_UNKNOWN,
            ai_result.get("error_message") or "AI policy generation failed and fallback is disabled.",
            warnings=list(all_warnings) + list(ai_result.get("warnings", [])),
            ai_debug=ai_debug,
        )

    ai_debug = _merge_ai_failure_debug(
        ai_debug_base,
        {
            "error_status": AI_ERROR_DISABLED,
            "error_message": "AI is disabled in configuration.",
            "validator_error_codes": [],
            "warnings": [],
            "validator_debug": {},
        },
        "disabled",
        fallback_reason="AI is disabled in configuration.",
    )

    if config.FALLBACK_ENABLED:
        return build_fallback_response(
            perception,
            state_resolution,
            queue_state=queue_snapshot,
            warnings=all_warnings,
            ai_debug=ai_debug,
            policy_memory_context=policy_memory_context,
        )

    return build_policy_error_response(
        perception,
        state_resolution,
        AI_ERROR_DISABLED,
        "AI is disabled in configuration and fallback is disabled.",
        warnings=all_warnings if all_warnings else ["AI is disabled in configuration and fallback is disabled."],
        ai_debug=ai_debug,
    )
