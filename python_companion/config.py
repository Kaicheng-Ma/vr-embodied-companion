"""Configuration for the local Python fallback runtime."""

from __future__ import annotations

import os
from pathlib import Path


def _parse_bool_env(name, default=False):
    raw_value = os.getenv(name)
    if raw_value is None:
        return bool(default)

    normalized = raw_value.strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    return bool(default)


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


def _strip_simple_quotes(value):
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
        return value[1:-1]
    return value


def _load_env_file(path):
    if not path or not path.is_file():
        return False

    loaded_any = False
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return False

    for line in lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#") or "=" not in stripped:
            continue

        key, value = stripped.split("=", 1)
        key = key.strip()
        value = _strip_simple_quotes(value.strip())
        if not key or key in os.environ:
            continue
        os.environ[key] = value
        loaded_any = True

    return loaded_any


def _load_project_env():
    config_dir = Path(__file__).resolve().parent
    candidates = [
        Path.cwd() / ".env",
        config_dir.parent / ".env",
    ]
    for candidate in candidates:
        _load_env_file(candidate)


_load_project_env()


HOST = "127.0.0.1"
PORT = 18765
POLICY_ENDPOINT = "/policy"
HEALTH_ENDPOINT = "/health"
SCHEMA_VERSION = "version_A_1.0.1"
DEFAULT_POLICY_LIFETIME = 4.0
TRANSITION_POLICY_LIFETIME = 1.0
FISHING_CAST_CYCLE_SEMANTIC_DURATION_SECONDS = 20.0
REQUEST_TIMEOUT_SECONDS = _parse_float_env("REQUEST_TIMEOUT_SECONDS", 5.0)
SERVER_NAME = "python_companion"
SERVER_VERSION = "stage4a_ai_first_runtime"

AI_ENABLED = _parse_bool_env("AI_ENABLED", False)
FALLBACK_ENABLED = _parse_bool_env("FALLBACK_ENABLED", True)
FORCE_FALLBACK_FOR_TEST = _parse_bool_env("FORCE_FALLBACK_FOR_TEST", False)
AI_PROVIDER = os.getenv("AI_PROVIDER", "qwen").strip().lower() or "qwen"

QWEN_API_KEY = os.getenv("QWEN_API_KEY", "").strip()
QWEN_BASE_URL = os.getenv("QWEN_BASE_URL", "").strip()
QWEN_MODEL = os.getenv("QWEN_MODEL", "qwen3.7-plus").strip() or "qwen3.7-plus"
QWEN_ENABLE_THINKING = _parse_bool_env("QWEN_ENABLE_THINKING", False)
QWEN_TEMPERATURE = _parse_float_env("QWEN_TEMPERATURE", 0.5)
QWEN_MAX_TOKENS = _parse_int_env("QWEN_MAX_TOKENS", 600)
_raw_qwen_response_format = os.environ.get("QWEN_RESPONSE_FORMAT")
if _raw_qwen_response_format is None:
    QWEN_RESPONSE_FORMAT = "json_object"
else:
    QWEN_RESPONSE_FORMAT = _raw_qwen_response_format.strip().lower()
