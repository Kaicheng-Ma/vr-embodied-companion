"""Fallback policy generation."""

from .response_builder import build_fallback_response


def generate_fallback_policy(perception, state_resolution, queue_state=None, warnings=None, policy_memory_context=None):
    return build_fallback_response(
        perception,
        state_resolution,
        queue_state=queue_state,
        warnings=warnings,
        policy_memory_context=policy_memory_context,
    )
