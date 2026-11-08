"""OpenAI-compatible provider transport for Stage 4A-1."""

from __future__ import annotations

import json
import socket
import time
import urllib.error
import urllib.request

from .ai_provider_config import get_ai_provider_config
from .models import (
    AI_ERROR_PROVIDER_AUTH,
    AI_ERROR_PROVIDER_BAD_REQUEST,
    AI_ERROR_PROVIDER_CONNECTION,
    AI_ERROR_EMPTY_RESPONSE,
    AI_ERROR_INVALID_JSON,
    AI_ERROR_PROVIDER_HTTP,
    AI_ERROR_PROVIDER_RATE_LIMIT,
    AI_ERROR_PROVIDER_RESPONSE_SCHEMA,
    AI_ERROR_RESPONSE_FORMAT_UNSUPPORTED,
    AI_ERROR_PROVIDER_TIMEOUT,
    AI_ERROR_UNKNOWN,
)


def _build_failure_result(provider_config, error_status, error_message, latency_ms):
    return {
        "success": False,
        "raw_text": None,
        "parsed_json_text": None,
        "provider": provider_config.provider_name if provider_config else "",
        "model": provider_config.model if provider_config else "",
        "latency_ms": latency_ms,
        "error_status": error_status,
        "error_message": error_message,
    }


def _build_chat_completions_url(base_url):
    base_url = (base_url or "").strip().rstrip("/")
    if not base_url:
        return ""
    if base_url.endswith("/chat/completions"):
        return base_url
    return f"{base_url}/chat/completions"


def _extract_message_content(response_payload):
    if not isinstance(response_payload, dict):
        return ""

    choices = response_payload.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""

    first_choice = choices[0]
    if not isinstance(first_choice, dict):
        return ""

    message = first_choice.get("message")
    if isinstance(message, dict):
        content = message.get("content")
        if isinstance(content, str):
            return content.strip()
        if content is not None:
            return str(content).strip()

    text = first_choice.get("text")
    if isinstance(text, str):
        return text.strip()

    return ""


def _maybe_normalize_json_text(raw_text):
    if not isinstance(raw_text, str) or not raw_text.strip():
        return None

    try:
        parsed = json.loads(raw_text)
    except json.JSONDecodeError:
        return None

    return json.dumps(parsed, ensure_ascii=False)


def request_chat_completion(messages, provider_config=None):
    """Send an OpenAI-compatible chat completion request."""

    provider_config = provider_config or get_ai_provider_config()
    start_time = time.monotonic()

    if not provider_config.base_url:
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            AI_ERROR_PROVIDER_HTTP,
            "Missing provider base URL.",
            latency_ms,
        )

    if not provider_config.api_key:
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            AI_ERROR_PROVIDER_AUTH,
            f"Missing API key for env var {provider_config.api_key_env_var}.",
            latency_ms,
        )

    endpoint_url = _build_chat_completions_url(provider_config.base_url)
    if not endpoint_url:
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            AI_ERROR_PROVIDER_HTTP,
            "Unable to resolve provider endpoint URL.",
            latency_ms,
        )

    payload = provider_config.build_request_payload(messages)
    payload_bytes = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    headers = provider_config.build_headers()
    request = urllib.request.Request(endpoint_url, data=payload_bytes, headers=headers, method="POST")

    try:
        with urllib.request.urlopen(request, timeout=provider_config.timeout_seconds) as response:
            response_body = response.read()
            latency_ms = int((time.monotonic() - start_time) * 1000)
    except urllib.error.HTTPError as exc:
        response_body = exc.read() if hasattr(exc, "read") else b""
        response_text = response_body.decode("utf-8", errors="replace") if response_body else ""
        error_status = AI_ERROR_PROVIDER_HTTP
        error_message = f"HTTP {exc.code} from provider."
        lowered_body = response_text.lower()
        if exc.code == 400:
            error_status = AI_ERROR_PROVIDER_BAD_REQUEST
            if "response_format" in lowered_body or "json_object" in lowered_body:
                error_status = AI_ERROR_RESPONSE_FORMAT_UNSUPPORTED
        elif exc.code in {401, 403}:
            error_status = AI_ERROR_PROVIDER_AUTH
        elif exc.code == 429:
            error_status = AI_ERROR_PROVIDER_RATE_LIMIT
        if response_text:
            error_message = f"{error_message} {response_text[:500]}"
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            error_status,
            error_message,
            latency_ms,
        )
    except (socket.timeout, TimeoutError) as exc:
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            AI_ERROR_PROVIDER_TIMEOUT,
            f"Provider request timed out or could not connect: {exc}",
            latency_ms,
        )
    except urllib.error.URLError as exc:
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            AI_ERROR_PROVIDER_CONNECTION,
            f"Provider connection failed: {exc}",
            latency_ms,
        )
    except Exception as exc:  # pragma: no cover - defensive guard for transport failures
        latency_ms = int((time.monotonic() - start_time) * 1000)
        return _build_failure_result(
            provider_config,
            AI_ERROR_UNKNOWN,
            f"Unexpected provider transport error: {exc}",
            latency_ms,
        )

    if not response_body:
        return _build_failure_result(
            provider_config,
            AI_ERROR_EMPTY_RESPONSE,
            "Provider returned an empty body.",
            latency_ms,
        )

    try:
        response_payload = json.loads(response_body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        return _build_failure_result(
            provider_config,
            AI_ERROR_INVALID_JSON,
            f"Provider response body is not valid JSON: {exc}",
            latency_ms,
        )

    raw_text = _extract_message_content(response_payload)
    if not raw_text:
        return _build_failure_result(
            provider_config,
            AI_ERROR_PROVIDER_RESPONSE_SCHEMA,
            "Provider response did not contain usable message content.",
            latency_ms,
        )

    return {
        "success": True,
        "raw_text": raw_text,
        "parsed_json_text": _maybe_normalize_json_text(raw_text),
        "provider": provider_config.provider_name,
        "model": provider_config.model,
        "latency_ms": latency_ms,
        "error_status": None,
        "error_message": None,
    }
