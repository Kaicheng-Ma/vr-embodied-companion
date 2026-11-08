# Interactive VR Environment and Embodied Companion

A Unity interaction prototype with a Python policy service for an embodied virtual companion. The repository focuses on interaction engineering, bounded action selection, and communication between Unity and Python.

## Code map

- `Assets/Test_Assets/Scripts/`: campfire, marshmallow and fishing interactions; companion perception, policy validation, scheduling, motion and visual execution.
- `python_companion/`: context and policy layers, provider adapter, bounded response validation, fallback behavior and tests.
- `Packages/`: Unity package dependencies.
- Scene and visual assets are not distributed in this curated source repository.

## Run the policy service

Use Python 3.11+ from the repository root:

```sh
python -m python_companion.companion_server
```

The service listens on `127.0.0.1:8765`; `GET /health` checks availability and `POST /policy` accepts perception input. See [runtime instructions](python_companion/README.md) and the sample requests under `python_companion/samples`. Provider configuration is read from environment variables; consult `ai_provider_config.py` for supported names and defaults. Never commit `.env` or real API credentials.

## Unity

The current source workspace uses Unity 6000.3.14f. Restore dependencies through Unity Hub/Package Manager and preserve `.meta` files. Open the project scenes and verify inspector bindings before a headset run. This update refreshes authored interaction scripts and the Python runtime; it does not replace all scene bindings or certify a full headset replay. Scene and third-party asset compatibility still requires validation in Unity.

## Scope

The agent chooses constrained nonverbal actions using user/activity context, rather than free-form avatar behavior. Local workflow logs, private settings, recordings and generated build caches should not be published. This is an engineering prototype; it does not establish psychological or clinical outcomes.


## Curated source distribution
This repository contains authored interaction scripts and the Python companion service. Large Unity scenes, third-party art, research documents and experimental records are excluded. It is not a complete drop-in Unity project; scene objects and Inspector references must be configured separately. Original development history remains in a separate private workspace.
