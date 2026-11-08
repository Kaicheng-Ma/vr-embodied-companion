"""AI policy client for Stage 4A-1 foundation work."""

from __future__ import annotations

from .ai_provider_config import get_ai_provider_config
from .models import (
    AI_ERROR_DISABLED,
    AI_ERROR_FORCE_FALLBACK_FOR_TEST,
    AI_ERROR_INVALID_JSON,
    AI_ERROR_UNKNOWN,
    AI_ERROR_VALIDATION_FAILED,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
    PRIMITIVE_SET_COMPANION_PROP,
    PROP_STATE_HIDDEN,
    PROP_STATE_VISIBLE_IN_HAND,
    PROP_STATE_VISIBLE_ON_STICK,
    PROP_MARSHMALLOW,
    PROP_STICK,
    SCOPE_AROUND_PLAYER,
    STATE_S1,
    STATE_S3,
)
from .openai_compatible_provider import request_chat_completion
from .policy_validator import validate_ai_policy
from .prompt_builder import build_policy_prompt

REQUIRED_S1_MOVEMENT_MISSING = "REQUIRED_S1_MOVEMENT_MISSING"
REQUIRED_S3_ROASTING_PROP_SETUP_MISSING = "REQUIRED_S3_ROASTING_PROP_SETUP_MISSING"


def _build_result(
    provider_config,
    *,
    success,
    policy,
    raw_text,
    latency_ms,
    error_status,
    error_message,
    warnings=None,
    errors=None,
    current_state="",
    allowed_pools=None,
    debug=None,
    prompt_debug=None,
):
    merged_debug = dict(debug or {})
    if isinstance(prompt_debug, dict):
        merged_debug.update(prompt_debug)
    return {
        "success": success,
        "policy": policy,
        "provider": provider_config.provider_name if provider_config else "",
        "model": provider_config.model if provider_config else "",
        "latency_ms": latency_ms,
        "raw_text": raw_text,
        "warnings": list(warnings or []),
        "errors": list(errors or []),
        "error_status": error_status,
        "error_message": error_message,
        "current_state": current_state,
        "allowed_pools": dict(allowed_pools or {}),
        "validator_debug": merged_debug,
        "validator_error_codes": [error.get("code", "") for error in (errors or []) if isinstance(error, dict)],
    }


def _extract_validation_status(validation_errors):
    for error in validation_errors or []:
        code = str(error.get("code", "")).upper()
        if code == "POLICY_JSON_INVALID":
            return AI_ERROR_INVALID_JSON
    return AI_ERROR_VALIDATION_FAILED


def _build_prompt_debug(prompt):
    if not isinstance(prompt, dict):
        return {}

    debug = {}
    perception_summary = prompt.get("perception_summary") if isinstance(prompt.get("perception_summary"), dict) else {}
    if "companion_prop_runtime_available" in perception_summary:
        debug["companion_prop_runtime_available"] = bool(perception_summary.get("companion_prop_runtime_available"))
        debug["companion_stick_state"] = perception_summary.get("companion_stick_state", "")
        debug["companion_marshmallow_state"] = perception_summary.get("companion_marshmallow_state", "")

    movement_context = perception_summary.get("spatial_movement_context") if isinstance(perception_summary.get("spatial_movement_context"), dict) else {}
    if not movement_context:
        return debug

    movement_need = movement_context.get("movement_recommendation", "unknown")
    distance = movement_context.get("player_companion_distance_xz")
    debug.update({
        "movement_need_summary": f"movement_need={movement_need} distance_xz={distance if distance is not None else 'unknown'}",
        "player_companion_distance_xz": distance,
        "player_displacement_since_previous_policy_xz": movement_context.get("player_displacement_since_previous_normal_policy_xz"),
        "companion_displacement_since_previous_policy_xz": movement_context.get("companion_displacement_since_previous_normal_policy_xz"),
        "last_policy_had_movement": bool(movement_context.get("last_policy_had_movement")),
        "consecutive_passive_policy_count": int(movement_context.get("consecutive_passive_policy_count") or 0),
        "movement_recommendation": movement_need,
    })
    return debug


def _contains_canonical_s1_around_player_movement(policy):
    if not isinstance(policy, dict):
        return False

    motion_primitives = policy.get("motion_primitives")
    if not isinstance(motion_primitives, list):
        return False

    for primitive in motion_primitives:
        if not isinstance(primitive, dict):
            continue
        if (
            str(primitive.get("type", "")).strip() == PRIMITIVE_MOVE_WITHIN_SCOPE
            and str(primitive.get("scope", "")).strip() == SCOPE_AROUND_PLAYER
        ):
            return True

    return False


def _validate_required_s1_movement_quality(policy, current_state, prompt_debug):
    if current_state != STATE_S1:
        return True, None
    if not isinstance(prompt_debug, dict) or prompt_debug.get("movement_recommendation") != "required":
        return True, None
    if _contains_canonical_s1_around_player_movement(policy):
        return True, None

    return False, {
        "code": REQUIRED_S1_MOVEMENT_MISSING,
        "message": "S1 movement was required, but the validated AI policy did not include around_player movement.",
    }


def _normalize_prop_state(value):
    normalized = str(value or "").strip().lower()
    return normalized if normalized in {PROP_STATE_VISIBLE_IN_HAND, PROP_STATE_VISIBLE_ON_STICK, PROP_STATE_HIDDEN} else PROP_STATE_HIDDEN


def _project_s3_prop_state(policy, prompt_debug):
    projected_stick_state = _normalize_prop_state((prompt_debug or {}).get("companion_stick_state"))
    projected_marshmallow_state = _normalize_prop_state((prompt_debug or {}).get("companion_marshmallow_state"))
    projection_steps = []
    ordering_valid = True
    ordering_failure_index = -1

    motion_primitives = policy.get("motion_primitives") if isinstance(policy, dict) else []
    for primitive_index, primitive in enumerate(motion_primitives if isinstance(motion_primitives, list) else []):
        if not isinstance(primitive, dict):
            continue
        if str(primitive.get("type", "")).strip() != PRIMITIVE_SET_COMPANION_PROP:
            continue

        prop = str(primitive.get("prop", "")).strip()
        state_value = str(primitive.get("state", "")).strip()
        if prop == PROP_STICK and state_value == PROP_STATE_VISIBLE_IN_HAND:
            projected_stick_state = PROP_STATE_VISIBLE_IN_HAND
        elif prop == PROP_STICK and state_value == PROP_STATE_HIDDEN:
            projected_stick_state = PROP_STATE_HIDDEN
            projected_marshmallow_state = PROP_STATE_HIDDEN
        elif prop == PROP_MARSHMALLOW and state_value == PROP_STATE_VISIBLE_ON_STICK:
            if projected_stick_state != PROP_STATE_VISIBLE_IN_HAND:
                ordering_valid = False
                ordering_failure_index = primitive_index
            projected_marshmallow_state = PROP_STATE_VISIBLE_ON_STICK
        elif prop == PROP_MARSHMALLOW and state_value == PROP_STATE_HIDDEN:
            projected_marshmallow_state = PROP_STATE_HIDDEN

        projection_steps.append(
            {
                "primitive_index": primitive_index,
                "prop": prop,
                "state": state_value,
                "projected_stick_state": projected_stick_state,
                "projected_marshmallow_state": projected_marshmallow_state,
            }
        )

    return {
        "initial_stick_state": _normalize_prop_state((prompt_debug or {}).get("companion_stick_state")),
        "initial_marshmallow_state": _normalize_prop_state((prompt_debug or {}).get("companion_marshmallow_state")),
        "projected_stick_state": projected_stick_state,
        "projected_marshmallow_state": projected_marshmallow_state,
        "projection_steps": projection_steps,
        "ordering_valid": ordering_valid,
        "ordering_failure_index": ordering_failure_index,
    }


def _validate_required_s3_roasting_prop_quality(policy, current_state, prompt_debug):
    if current_state != STATE_S3:
        return True, None, {}
    if not isinstance(prompt_debug, dict) or not bool(prompt_debug.get("companion_prop_runtime_available")):
        return True, None, {}

    projection = _project_s3_prop_state(policy, prompt_debug)
    motion_primitives = policy.get("motion_primitives") if isinstance(policy, dict) else []
    first_primitive = motion_primitives[0] if isinstance(motion_primitives, list) and len(motion_primitives) > 0 and isinstance(motion_primitives[0], dict) else {}
    second_primitive = motion_primitives[1] if isinstance(motion_primitives, list) and len(motion_primitives) > 1 and isinstance(motion_primitives[1], dict) else {}
    required_prefix_valid = (
        str(first_primitive.get("type", "")).strip() == PRIMITIVE_SET_COMPANION_PROP
        and str(first_primitive.get("prop", "")).strip() == PROP_STICK
        and str(first_primitive.get("state", "")).strip() == PROP_STATE_VISIBLE_IN_HAND
        and str(second_primitive.get("type", "")).strip() == PRIMITIVE_SET_COMPANION_PROP
        and str(second_primitive.get("prop", "")).strip() == PROP_MARSHMALLOW
        and str(second_primitive.get("state", "")).strip() == PROP_STATE_VISIBLE_ON_STICK
    )
    projection["required_setup_prefix_valid"] = required_prefix_valid

    final_state_valid = (
        projection["projected_stick_state"] == PROP_STATE_VISIBLE_IN_HAND
        and projection["projected_marshmallow_state"] == PROP_STATE_VISIBLE_ON_STICK
    )
    if projection["ordering_valid"] and required_prefix_valid and final_state_valid:
        return True, None, projection

    return False, {
        "code": REQUIRED_S3_ROASTING_PROP_SETUP_MISSING,
        "message": "S3 executable policies must begin with stick visible_in_hand then marshmallow visible_on_stick.",
    }, projection


def generate_ai_policy(perception, current_state, allowed_pools, policy_memory_summary=None, policy_memory_context=None):
    """Generate an AI policy attempt without falling back."""

    provider_config = get_ai_provider_config()

    if provider_config.force_fallback_for_test:
        return _build_result(
            provider_config,
            success=False,
            policy=None,
            raw_text=None,
            latency_ms=0,
            error_status=AI_ERROR_FORCE_FALLBACK_FOR_TEST,
            error_message="FORCE_FALLBACK_FOR_TEST is enabled.",
            warnings=[],
            errors=[{"code": AI_ERROR_FORCE_FALLBACK_FOR_TEST, "message": "FORCE_FALLBACK_FOR_TEST is enabled."}],
            current_state=current_state,
            allowed_pools=allowed_pools,
        )

    if not provider_config.ai_enabled:
        return _build_result(
            provider_config,
            success=False,
            policy=None,
            raw_text=None,
            latency_ms=0,
            error_status=AI_ERROR_DISABLED,
            error_message="AI is disabled in configuration.",
            warnings=[],
            errors=[{"code": AI_ERROR_DISABLED, "message": "AI is disabled in configuration."}],
            current_state=current_state,
            allowed_pools=allowed_pools,
        )

    prompt = build_policy_prompt(
        perception,
        current_state,
        allowed_pools,
        policy_memory_summary=policy_memory_summary,
        policy_memory_context=policy_memory_context,
    )
    prompt_debug = _build_prompt_debug(prompt)
    provider_result = request_chat_completion(prompt["messages"], provider_config=provider_config)

    if not provider_result.get("success"):
        return _build_result(
            provider_config,
            success=False,
            policy=None,
            raw_text=provider_result.get("raw_text"),
            latency_ms=provider_result.get("latency_ms", 0),
            error_status=provider_result.get("error_status") or AI_ERROR_UNKNOWN,
            error_message=provider_result.get("error_message") or "Provider request failed.",
            warnings=[],
            errors=[
                {
                    "code": provider_result.get("error_status") or AI_ERROR_UNKNOWN,
                    "message": provider_result.get("error_message") or "Provider request failed.",
                }
            ],
            current_state=current_state,
            allowed_pools=allowed_pools,
            prompt_debug=prompt_debug,
        )

    raw_text = provider_result.get("parsed_json_text") or provider_result.get("raw_text") or ""
    validation_result = validate_ai_policy(raw_text, current_state, allowed_pools)
    if not validation_result.get("valid"):
        error_status = _extract_validation_status(validation_result.get("errors"))
        return _build_result(
            provider_config,
            success=False,
            policy=None,
            raw_text=raw_text,
            latency_ms=provider_result.get("latency_ms", 0),
            error_status=error_status,
            error_message="AI policy validation failed.",
            warnings=validation_result.get("warnings", []),
            errors=validation_result.get("errors", []),
            current_state=current_state,
            allowed_pools=allowed_pools,
            debug=validation_result.get("debug", {}),
            prompt_debug=prompt_debug,
        )

    clean_policy = validation_result.get("policy")
    quality_valid, quality_error = _validate_required_s1_movement_quality(clean_policy, current_state, prompt_debug)
    if not quality_valid:
        quality_debug = dict(validation_result.get("debug", {}))
        quality_debug["required_s1_movement_gate"] = "failed"
        quality_debug["required_s1_movement_missing"] = True
        return _build_result(
            provider_config,
            success=False,
            policy=None,
            raw_text=raw_text,
            latency_ms=provider_result.get("latency_ms", 0),
            error_status=AI_ERROR_VALIDATION_FAILED,
            error_message=quality_error["message"],
            warnings=validation_result.get("warnings", []),
            errors=[quality_error],
            current_state=current_state,
            allowed_pools=allowed_pools,
            debug=quality_debug,
            prompt_debug=prompt_debug,
        )

    s3_quality_valid, s3_quality_error, s3_projection = _validate_required_s3_roasting_prop_quality(clean_policy, current_state, prompt_debug)
    if not s3_quality_valid:
        quality_debug = dict(validation_result.get("debug", {}))
        quality_debug["required_s3_roasting_prop_gate"] = "failed"
        quality_debug["required_s3_roasting_prop_setup_missing"] = True
        quality_debug["s3_prop_projection"] = s3_projection
        return _build_result(
            provider_config,
            success=False,
            policy=None,
            raw_text=raw_text,
            latency_ms=provider_result.get("latency_ms", 0),
            error_status=AI_ERROR_VALIDATION_FAILED,
            error_message=s3_quality_error["message"],
            warnings=validation_result.get("warnings", []),
            errors=[s3_quality_error],
            current_state=current_state,
            allowed_pools=allowed_pools,
            debug=quality_debug,
            prompt_debug=prompt_debug,
        )

    return _build_result(
        provider_config,
        success=True,
        policy=clean_policy,
        raw_text=raw_text,
        latency_ms=provider_result.get("latency_ms", 0),
        error_status=None,
        error_message=None,
        warnings=validation_result.get("warnings", []),
        errors=[],
        current_state=current_state,
        allowed_pools=allowed_pools,
        debug=validation_result.get("debug", {}),
        prompt_debug=prompt_debug,
    )
