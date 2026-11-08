"""Local HTTP server for the Stage 4A AI-first Python runtime."""

from __future__ import annotations

import argparse
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import config
from .activity_policy_memory import ActivityPolicyMemoryStore
from .models import (
    ACTIVITY_ID_CAMPFIRE,
    ACTIVITY_ID_COMMON,
    ERROR_EMPTY_REQUEST,
    ERROR_MALFORMED_PERCEPTION,
    ERROR_MISSING_REQUIRED_FIELDS,
    ERROR_SERVER_UNAVAILABLE,
    ERROR_TRANSITION_TASK_MAX_ATTEMPTS,
    POLICY_SOURCE_TRANSITION_PRIORITY,
    STATE_S1,
    STATE_S2,
    STATE_S3,
)
from .policy_layer import generate_policy_response
from .response_builder import build_policy_error_response, build_shutdown_response
from .state_layer import resolve_state
from .transition_queue import TransitionQueue


class CompanionRuntime:
    def __init__(self):
        self.memory_store = ActivityPolicyMemoryStore()
        self.transition_queue = TransitionQueue()
        self.lock = threading.Lock()

    def reset_session_state_for_unity_bootstrap(self, reason=""):
        # Reset per-session runtime state when Unity starts a fresh Play session.
        # This intentionally clears memory and transition bookkeeping together so
        # stale S2/S3 transition exhaustion cannot leak into a new S1 bootstrap.
        self.memory_store.reset_all()
        self.transition_queue = TransitionQueue()
        return reason

    def clear_obsolete_transition_state_for_s1(self, reason=""):
        # S1 should never inherit stale S2/S3 entry transition state. Clear only
        # transition-related leftovers and keep the rest of the request flow intact.
        current_task = self.transition_queue.current_task()
        has_obsolete_transition_task = (
            current_task is not None
            and current_task.transition_id in {"s2_entry_transition", "s3_entry_transition"}
        )
        campfire_memory = self.memory_store.get_memory(ACTIVITY_ID_CAMPFIRE)
        has_obsolete_transition_memory = (
            campfire_memory.last_state in {STATE_S2, STATE_S3}
            and (
                campfire_memory.last_policy_source == POLICY_SOURCE_TRANSITION_PRIORITY
                or campfire_memory.last_error_code == ERROR_TRANSITION_TASK_MAX_ATTEMPTS
            )
        )

        if not has_obsolete_transition_task and not has_obsolete_transition_memory:
            return False

        self.transition_queue.clear()
        self.memory_store.reset_activity(ACTIVITY_ID_CAMPFIRE)
        return True


runtime = CompanionRuntime()


def _extract_request_context(perception):
    frame = perception.get("frame") if isinstance(perception, dict) else {}
    companion = perception.get("companion") if isinstance(perception, dict) else {}

    request_role = str(frame.get("request_role", "") or "") if isinstance(frame, dict) else ""
    current_state = str(companion.get("current_state", "") or "") if isinstance(companion, dict) else ""

    try:
        source_state_epoch = int(frame.get("current_state_epoch")) if isinstance(frame, dict) else -1
    except (TypeError, ValueError):
        source_state_epoch = -1

    try:
        source_policy_index = int(frame.get("policy_index")) if isinstance(frame, dict) else -1
    except (TypeError, ValueError):
        source_policy_index = -1

    return {
        "request_role": request_role,
        "source_state_epoch": source_state_epoch,
        "source_policy_index": source_policy_index,
        "raw_current_state": current_state,
    }


def _is_fresh_unity_bootstrap(request_context):
    if not isinstance(request_context, dict):
        return False

    return (
        request_context.get("request_role") == "initial_policy"
        and request_context.get("source_state_epoch") == 0
        and request_context.get("source_policy_index") == 0
        and request_context.get("raw_current_state") == STATE_S1
    )


def _parse_json_body(body_bytes):
    if not body_bytes:
        return None, ERROR_EMPTY_REQUEST, "Request body is empty."

    try:
        text = body_bytes.decode("utf-8")
    except UnicodeDecodeError as exc:
        return None, ERROR_MALFORMED_PERCEPTION, f"Request body is not valid UTF-8: {exc}"

    try:
        return json.loads(text), "", ""
    except json.JSONDecodeError as exc:
        return None, ERROR_MALFORMED_PERCEPTION, f"JSON parse failed: {exc}"


def _build_response(perception):
    request_context = _extract_request_context(perception)
    if _is_fresh_unity_bootstrap(request_context):
        runtime.reset_session_state_for_unity_bootstrap("fresh_unity_bootstrap")

    default_memory = runtime.memory_store.get_memory(ACTIVITY_ID_COMMON)
    state_resolution = resolve_state(perception, memory=default_memory)
    active_activity_id = state_resolution.activity_id or ACTIVITY_ID_COMMON
    active_memory = runtime.memory_store.get_memory(active_activity_id)
    if active_memory is not default_memory:
        state_resolution = resolve_state(perception, memory=active_memory)
        active_activity_id = state_resolution.activity_id or ACTIVITY_ID_COMMON

    if not state_resolution.is_valid:
        return build_policy_error_response(
            perception,
            state_resolution,
            state_resolution.error_code or ERROR_MISSING_REQUIRED_FIELDS,
            state_resolution.error_message or "Perception validation failed.",
            warnings=[state_resolution.error_message or "Perception validation failed."],
        )

    if state_resolution.current_state == STATE_S1:
        runtime.clear_obsolete_transition_state_for_s1("authoritative_state_s1")

    control = perception.get("control") if isinstance(perception, dict) else {}
    if isinstance(control, dict) and bool(control.get("shutdown_signal")):
        response = build_shutdown_response(perception, state_resolution, warnings=["Shutdown signal was received from Unity."])
        runtime.transition_queue.clear()
        runtime.memory_store.update_from_response(active_activity_id, response, perception=perception)
        return response

    response = generate_policy_response(
        perception,
        state_resolution,
        policy_memory=active_memory,
        transition_queue=runtime.transition_queue,
    )
    runtime.memory_store.update_from_response(active_activity_id, response, perception=perception)
    return response


class CompanionRequestHandler(BaseHTTPRequestHandler):
    server_version = f"{config.SERVER_NAME}/{config.SERVER_VERSION}"

    def log_message(self, format, *args):
        return

    def _write_json(self, status_code, payload):
        body = json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == config.HEALTH_ENDPOINT:
            self._write_json(200, {"status": "ok", "server": config.SERVER_NAME, "version": config.SERVER_VERSION})
            return
        self.send_error(404, "Not Found")

    def do_POST(self):
        if self.path != config.POLICY_ENDPOINT:
            self.send_error(404, "Not Found")
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        raw_body = self.rfile.read(content_length) if content_length > 0 else b""

        perception, error_code, error_message = _parse_json_body(raw_body)
        if perception is None:
            response = build_policy_error_response(
                {},
                type("StateResolutionFallback", (), {
                    "current_state": "",
                    "previous_state": "",
                    "state_changed": False,
                    "state_reason": "",
                })(),
                error_code,
                error_message,
                warnings=[error_message],
            )
            print(
                f"Sending response: "
                f"policy_source={response.get('policy', {}).get('policy_source')}"
            )

            self._write_json(200, response)
            return

        with runtime.lock:
            try:
                response = _build_response(perception)
            except Exception as exc:
                response = build_policy_error_response(
                    perception,
                    type("StateResolutionFallback", (), {
                        "current_state": "",
                        "previous_state": "",
                        "state_changed": False,
                        "state_reason": "",
                    })(),
                    ERROR_SERVER_UNAVAILABLE,
                    f"Server internal error: {exc}",
                    warnings=[f"Server internal error: {exc}"],
                )

        self._write_json(200, response)


def serve(host=None, port=None):
    host = host or config.HOST
    port = port or config.PORT
    server = ThreadingHTTPServer((host, port), CompanionRequestHandler)
    print(f"Python companion Stage 4A AI-first server listening on http://{host}:{port}")
    print(f"Policy endpoint: POST {config.POLICY_ENDPOINT}")
    print(f"Health endpoint: GET {config.HEALTH_ENDPOINT}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Python companion Stage 4A AI-first server stopped.")
    finally:
        server.server_close()


def main():
    parser = argparse.ArgumentParser(description="Stage 4A AI-first local Python server")
    parser.add_argument("--host", default=config.HOST)
    parser.add_argument("--port", type=int, default=config.PORT)
    args = parser.parse_args()
    serve(args.host, args.port)


if __name__ == "__main__":
    main()
