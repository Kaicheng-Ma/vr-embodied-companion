"""AI provider configuration helpers for Stage 4A-1."""

from __future__ import annotations

import os
from copy import deepcopy
from dataclasses import dataclass, field
from typing import Any, Dict

from . import config as runtime_config
from .models import AI_PROVIDER_QWEN


def parse_bool_env(name, default=False):
    """Parse a boolean-like environment variable."""

    raw_value = os.getenv(name)
    if raw_value is None:
        return bool(default)

    normalized = raw_value.strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    return bool(default)


def mask_secret(value):
    """Mask a secret so debug output can be reviewed safely."""

    if not value:
        return ""

    text = str(value)
    if len(text) <= 4:
        return "*" * len(text)
    return f"{text[:2]}***{text[-2:]}"


def _parse_float_env(name, default):
    raw_value = os.getenv(name)
    if raw_value is None or raw_value.strip() == "":
        return float(default)
    try:
        return float(raw_value)
    except (TypeError, ValueError):
        return float(default)


def _parse_int_env(name, default):
    raw_value = os.getenv(name)
    if raw_value is None or raw_value.strip() == "":
        return int(default)
    try:
        return int(raw_value)
    except (TypeError, ValueError):
        return int(default)


def _parse_response_format(value):
    normalized = (value or "").strip().lower()
    if normalized in {"", "none", "disabled", "false", "off"}:
        return ""
    return normalized


@dataclass
class AIProviderConfig:
    """Lightweight provider config for OpenAI-compatible chat APIs."""

    provider_name: str
    model: str
    base_url: str
    api_key_env_var: str
    api_key: str
    timeout_seconds: float
    temperature: float
    max_tokens: int
    response_format_preference: str = ""
    extra_body: Dict[str, Any] = field(default_factory=dict)
    ai_enabled: bool = False
    fallback_enabled: bool = True
    force_fallback_for_test: bool = False

    def build_request_payload(self, messages):
        """Build the request body sent to the provider."""

        payload = {
            "model": self.model,
            "messages": list(messages),
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
            "stream": False,
        }

        if self.response_format_preference:
            payload["response_format"] = {"type": self.response_format_preference}

        if self.extra_body:
            payload.update(deepcopy(self.extra_body))

        return payload

    def build_headers(self):
        headers = {
            "Content-Type": "application/json",
        }
        if self.api_key:
            headers["Authorization"] = f"Bearer {self.api_key}"
        return headers

    def to_debug_dict(self):
        return {
            "provider_name": self.provider_name,
            "model": self.model,
            "base_url": self.base_url,
            "api_key_env_var": self.api_key_env_var,
            "api_key_masked": mask_secret(self.api_key),
            "timeout_seconds": self.timeout_seconds,
            "temperature": self.temperature,
            "max_tokens": self.max_tokens,
            "response_format_preference": self.response_format_preference,
            "extra_body": deepcopy(self.extra_body),
            "ai_enabled": self.ai_enabled,
            "fallback_enabled": self.fallback_enabled,
            "force_fallback_for_test": self.force_fallback_for_test,
        }


def _build_qwen_config():
    response_format_preference = _parse_response_format(runtime_config.QWEN_RESPONSE_FORMAT)
    return AIProviderConfig(
        provider_name=AI_PROVIDER_QWEN,
        model=runtime_config.QWEN_MODEL,
        base_url=runtime_config.QWEN_BASE_URL,
        api_key_env_var="QWEN_API_KEY",
        api_key=runtime_config.QWEN_API_KEY,
        timeout_seconds=runtime_config.REQUEST_TIMEOUT_SECONDS,
        temperature=runtime_config.QWEN_TEMPERATURE,
        max_tokens=runtime_config.QWEN_MAX_TOKENS,
        response_format_preference=response_format_preference,
        extra_body={
            "enable_thinking": runtime_config.QWEN_ENABLE_THINKING,
        },
        ai_enabled=runtime_config.AI_ENABLED,
        fallback_enabled=runtime_config.FALLBACK_ENABLED,
        force_fallback_for_test=runtime_config.FORCE_FALLBACK_FOR_TEST,
    )


def _build_generic_openai_compatible_config(provider_name):
    normalized_provider = (provider_name or "openai_compatible").strip().lower() or "openai_compatible"
    api_key_env_var = "AI_API_KEY"
    base_url_env_var = "AI_BASE_URL"
    model_env_var = "AI_MODEL"

    return AIProviderConfig(
        provider_name=normalized_provider,
        model=os.getenv(model_env_var, "gpt-4o-mini").strip() or "gpt-4o-mini",
        base_url=os.getenv(base_url_env_var, "").strip(),
        api_key_env_var=api_key_env_var,
        api_key=os.getenv(api_key_env_var, "").strip(),
        timeout_seconds=runtime_config.REQUEST_TIMEOUT_SECONDS,
        temperature=_parse_float_env("AI_TEMPERATURE", 0.5),
        max_tokens=_parse_int_env("AI_MAX_TOKENS", 600),
        response_format_preference=_parse_response_format(os.getenv("AI_RESPONSE_FORMAT", "")),
        extra_body={},
        ai_enabled=runtime_config.AI_ENABLED,
        fallback_enabled=runtime_config.FALLBACK_ENABLED,
        force_fallback_for_test=runtime_config.FORCE_FALLBACK_FOR_TEST,
    )


def get_qwen_provider_config():
    """Return the current Qwen provider config."""

    return _build_qwen_config()


def get_ai_provider_config():
    """Return the active provider config."""

    provider_name = runtime_config.AI_PROVIDER
    if provider_name == AI_PROVIDER_QWEN:
        return get_qwen_provider_config()
    return _build_generic_openai_compatible_config(provider_name)
