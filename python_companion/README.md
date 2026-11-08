# Python Companion Stage 4A

This folder contains the local Python policy runtime for Stage 4A.

Stage 4A adds a Qwen AI-first policy path on top of the Stage 3 local fallback runtime.

When AI succeeds, the Python runtime returns `policy_source=ai`.
When AI is unavailable or invalid and fallback is enabled, it returns `policy_source=fallback`.
When AI is unavailable or invalid and fallback is disabled, it returns `policy_source=policy_error`.
`policy_error` does not shut down the companion loop. It sets `execute_policy=false` while keeping `companion_should_run=true`, so Unity can keep polling and show the error in DebugView.

## Run the server

From the repository root:

```bash
python -m python_companion.companion_server
```

The default server listens on:

- `http://127.0.0.1:8765`

Endpoints:

- `POST /policy`
- `GET /health`

## Manual policy test

You can POST one of the sample perception files to `/policy` with any HTTP client.

Example using PowerShell:

```powershell
$body = Get-Content python_companion/samples/sample_s1_perception.json -Raw
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8765/policy -ContentType 'application/json' -Body $body
```

Sample files are for standalone Python testing only.
They do not replace Unity-generated perception.

## Repeated blocked transition test

To verify the transition task max-attempt limit, POST the same blocked S2 or S3 sample several times in a row.
After the transition task exceeds its maximum attempts, the server should return a `policy_error` response with:
- `control.execute_policy = false`
- `control.policy_error = true`
- `policy.policy_source = policy_error`
- `debug.error_code = TRANSITION_TASK_MAX_ATTEMPTS`

## Version-control note

Python cache folders and bytecode files are generated locally during testing.
They are ignored by `.gitignore` and should not be committed.
