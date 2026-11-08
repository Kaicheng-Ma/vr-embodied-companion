"""Short-term policy memory shared by fallback, transition, and AI logic."""

import math
from dataclasses import dataclass, field
from typing import List, Dict, Any

from .models import (
    POLICY_SOURCE_AI,
    POLICY_SOURCE_FALLBACK,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_MOVE_WITHIN_SCOPE,
)


PASSIVE_NORMAL_PRIMITIVES = {
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_FACE_TOWARD,
    PRIMITIVE_HOLD_POSITION,
}

GAZE_PRIMITIVES = {
    PRIMITIVE_LOOK_AT,
    PRIMITIVE_FACE_TOWARD,
}

MAX_RECENT_GAZE_TARGETS = 3


@dataclass
class PolicyMemory:
    last_state: str = ""
    last_policy_source: str = ""
    last_activity_mode: str = ""
    last_motion_primitives: List[Dict[str, Any]] = field(default_factory=list)
    last_error_code: str = ""
    last_error_message: str = ""
    last_ai_enabled: bool = False
    last_fallback_enabled: bool = True
    last_force_fallback_for_test: bool = False
    last_ai_status: str = ""
    last_ai_error_status: str = ""
    last_ai_error_message: str = ""
    last_provider: str = ""
    last_model: str = ""
    last_latency_ms: float = 0.0
    last_fallback_reason: str = ""
    last_validator_error_codes: List[str] = field(default_factory=list)
    last_validator_warnings: List[str] = field(default_factory=list)
    last_validator_debug: Dict[str, Any] = field(default_factory=dict)
    last_policy_had_movement: bool = False
    consecutive_passive_policy_count: int = 0
    last_player_position_xz: Dict[str, float] = field(default_factory=dict)
    last_companion_position_xz: Dict[str, float] = field(default_factory=dict)
    last_movement_scope: str = ""
    recent_gaze_state: str = ""
    recent_normal_gaze_targets: List[str] = field(default_factory=list)
    last_motion_primitive_types: List[str] = field(default_factory=list)

    def update_from_response(self, response, perception=None):
        if not isinstance(response, dict):
            return

        state_section = response.get("state") if isinstance(response.get("state"), dict) else {}
        policy_section = response.get("policy") if isinstance(response.get("policy"), dict) else {}
        debug_section = response.get("debug") if isinstance(response.get("debug"), dict) else {}
        motion_primitives = response.get("motion_primitives") if isinstance(response.get("motion_primitives"), list) else []

        execute_policy = bool(response.get("control", {}).get("execute_policy")) if isinstance(response.get("control"), dict) else False
        self.last_state = state_section.get("current_state", self.last_state)
        self.last_policy_source = policy_section.get("policy_source", self.last_policy_source)
        self._clear_recent_gaze_history_on_state_change(self.last_state)
        is_accepted_normal_policy = execute_policy and self.last_policy_source in {POLICY_SOURCE_AI, POLICY_SOURCE_FALLBACK}
        if is_accepted_normal_policy:
            self.last_activity_mode = policy_section.get("activity_mode", self.last_activity_mode)
            self.last_motion_primitives = list(motion_primitives)
            self._update_passive_policy_memory(motion_primitives, perception)
        if execute_policy and self.last_policy_source == POLICY_SOURCE_AI:
            self._update_recent_gaze_history(self.last_state, motion_primitives)
        self.last_error_code = debug_section.get("error_code", self.last_error_code)
        self.last_error_message = debug_section.get("error_message", self.last_error_message)
        self.last_ai_enabled = bool(debug_section.get("ai_enabled", self.last_ai_enabled))
        self.last_fallback_enabled = bool(debug_section.get("fallback_enabled", self.last_fallback_enabled))
        self.last_force_fallback_for_test = bool(debug_section.get("force_fallback_for_test", self.last_force_fallback_for_test))
        self.last_ai_status = debug_section.get("ai_status", self.last_ai_status)
        self.last_ai_error_status = debug_section.get("ai_error_status", self.last_ai_error_status)
        self.last_ai_error_message = debug_section.get("ai_error_message", self.last_ai_error_message)
        self.last_provider = debug_section.get("provider", self.last_provider)
        self.last_model = debug_section.get("model", self.last_model)

        latency_ms = debug_section.get("latency_ms")
        if isinstance(latency_ms, (int, float)):
            self.last_latency_ms = float(latency_ms)

        self.last_fallback_reason = debug_section.get("fallback_reason", self.last_fallback_reason)

        validator_error_codes = debug_section.get("validator_error_codes")
        if isinstance(validator_error_codes, list):
            self.last_validator_error_codes = [str(code) for code in validator_error_codes if str(code)]

        validator_warnings = debug_section.get("validator_warnings")
        if isinstance(validator_warnings, list):
            self.last_validator_warnings = [str(warning) for warning in validator_warnings if str(warning)]

        validator_debug = debug_section.get("validator_debug")
        if isinstance(validator_debug, dict):
            self.last_validator_debug = dict(validator_debug)

    def _update_passive_policy_memory(self, motion_primitives, perception):
        primitive_types = [
            str(primitive.get("type", "")).strip()
            for primitive in motion_primitives
            if isinstance(primitive, dict)
        ]
        self.last_motion_primitive_types = [primitive_type for primitive_type in primitive_types if primitive_type]
        movement_primitives = [
            primitive
            for primitive in motion_primitives
            if isinstance(primitive, dict) and str(primitive.get("type", "")).strip() == PRIMITIVE_MOVE_WITHIN_SCOPE
        ]
        self.last_policy_had_movement = bool(movement_primitives)
        if movement_primitives:
            self.last_movement_scope = str(movement_primitives[-1].get("scope", "") or "")
            self.consecutive_passive_policy_count = 0
        elif primitive_types and all(primitive_type in PASSIVE_NORMAL_PRIMITIVES for primitive_type in primitive_types):
            self.consecutive_passive_policy_count += 1
        else:
            self.consecutive_passive_policy_count = 0

        player_position = self._extract_xz_position(perception, "player")
        companion_position = self._extract_xz_position(perception, "companion")
        if player_position:
            self.last_player_position_xz = player_position
        if companion_position:
            self.last_companion_position_xz = companion_position

    def _clear_recent_gaze_history_on_state_change(self, current_state):
        current_state = str(current_state or "")
        if not current_state:
            return
        if self.recent_gaze_state and self.recent_gaze_state != current_state:
            self.recent_normal_gaze_targets = []
        self.recent_gaze_state = current_state

    def _update_recent_gaze_history(self, current_state, motion_primitives):
        current_state = str(current_state or "")
        if not current_state:
            return

        representative_target = ""
        for primitive in motion_primitives or []:
            if not isinstance(primitive, dict):
                continue
            primitive_type = str(primitive.get("type", "")).strip()
            if primitive_type not in GAZE_PRIMITIVES:
                continue
            target = str(primitive.get("target", "")).strip()
            if target:
                # The final explicit gaze primitive is the visible target left by the policy.
                representative_target = target

        if not representative_target:
            return

        self._clear_recent_gaze_history_on_state_change(current_state)
        self.recent_normal_gaze_targets.append(representative_target)
        if len(self.recent_normal_gaze_targets) > MAX_RECENT_GAZE_TARGETS:
            self.recent_normal_gaze_targets = self.recent_normal_gaze_targets[-MAX_RECENT_GAZE_TARGETS:]

    def _extract_xz_position(self, perception, section_name):
        if not isinstance(perception, dict):
            return {}
        section = perception.get(section_name)
        if not isinstance(section, dict):
            return {}
        position = section.get("position")
        if not isinstance(position, dict):
            return {}
        try:
            x = float(position.get("x"))
            z = float(position.get("z"))
        except (TypeError, ValueError):
            return {}
        if not math.isfinite(x) or not math.isfinite(z):
            return {}
        return {"x": x, "z": z}

    def build_prompt_context(self):
        return {
            "last_state": self.last_state,
            "last_policy_had_movement": self.last_policy_had_movement,
            "consecutive_passive_policy_count": self.consecutive_passive_policy_count,
            "last_player_position_xz": dict(self.last_player_position_xz),
            "last_companion_position_xz": dict(self.last_companion_position_xz),
            "last_movement_scope": self.last_movement_scope,
            "recent_gaze_state": self.recent_gaze_state,
            "recent_normal_gaze_targets": list(self.recent_normal_gaze_targets),
            "last_motion_primitive_types": list(self.last_motion_primitive_types),
        }

    def build_summary(self, max_motion_primitives=3):
        motion_types = [primitive.get("type", "") for primitive in self.last_motion_primitives[:max_motion_primitives] if isinstance(primitive, dict)]
        motion_types_text = ", ".join([motion_type for motion_type in motion_types if motion_type]) or "none"
        validator_errors_text = ", ".join(self.last_validator_error_codes) or "none"
        validator_warnings_text = ", ".join(self.last_validator_warnings) or "none"
        validator_debug_state = self.last_validator_debug.get("current_state", "")
        return (
            f"last_state={self.last_state}; "
            f"last_policy_source={self.last_policy_source}; "
            f"last_activity_mode={self.last_activity_mode}; "
            f"last_motion_primitives={motion_types_text}; "
            f"last_error_code={self.last_error_code}; "
            f"last_ai_enabled={self.last_ai_enabled}; "
            f"last_fallback_enabled={self.last_fallback_enabled}; "
            f"last_force_fallback_for_test={self.last_force_fallback_for_test}; "
            f"last_ai_status={self.last_ai_status}; "
            f"last_ai_error_status={self.last_ai_error_status}; "
            f"last_ai_error_message={self.last_ai_error_message}; "
            f"last_provider={self.last_provider}; "
            f"last_model={self.last_model}; "
            f"last_latency_ms={self.last_latency_ms:.0f}; "
            f"last_fallback_reason={self.last_fallback_reason}; "
            f"last_policy_had_movement={self.last_policy_had_movement}; "
            f"consecutive_passive_policy_count={self.consecutive_passive_policy_count}; "
            f"last_movement_scope={self.last_movement_scope or 'none'}; "
            f"validator_debug_state={validator_debug_state or 'none'}; "
            f"validator_errors={validator_errors_text}; "
            f"validator_warnings={validator_warnings_text}"
        )
