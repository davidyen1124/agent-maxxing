# AGENTS.md

This repo is a Unity 6 project. Treat Unity-generated YAML, `.meta` files, imported assets, and Git LFS pointers carefully.

## Project Snapshot

- Unity version: `6000.4.6f1`
- Default scene: `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`
- Build settings scene: `TerrainDemoScene`
- Render pipeline: URP `17.4.0`
- Input: Unity Input System package, not legacy `UnityEngine.Input`
- Local Codex app-server websocket: `ws://127.0.0.1:4500`
- Runtime namespace: `Underwater`
- Main assembly: `Assets/Scripts/Underwater.Runtime.asmdef`
- PlayMode test assembly: `Assets/Tests/PlayMode/Underwater.PlayModeTests.asmdef`

## Current Architecture

`UnderwaterGameDirector` is the central runtime coordinator. It configures terrain mode, creates the player, owns thread pet lifecycle, renders IMGUI overlays, handles realtime voice, and applies realtime atmosphere changes.

`AquariumDirectorBridge` connects to the local Codex app-server and converts app-server thread/archive events into Unity snapshots. It calls `UnderwaterGameDirector.SyncThreadWorld(...)`.

Thread pets are split into behavior and visual layers:

- `ThreadPetAI`: active thread movement, terrain following, target selection, status/phase mapping
- `ArchivedThreadPet`: archived thread idle/hop behavior
- `ThreadPetAnimalVisual`: deterministic 3D animal selection, prefab instantiation, scale/grounding, Animator parameter updates

The old 2D sprite pet system was removed. Do not reintroduce `CodexPetCatalog`, `CodexPetSpriteAnimator`, or `Assets/StreamingAssets/CodexPets`.

3D animal prefabs live in:

```text
Assets/Resources/ThreadPetAnimals
```

Their source package assets live in:

```text
Assets/ithappy/Animals_FREE
```

`ThreadPetAnimalVisual` intentionally disables imported movement/input components such as `MovePlayerInput`, `CreatureMover`, and `CharacterController` at runtime. Our code owns movement.

## Player And Controls

`UnderwaterPlayerController` owns first-person controls:

- `W/A/S/D`: move
- Mouse: look
- `Shift`: sprint
- `Space`: jump
- `E`: spawn Codex work thread
- Hold/release `V`: realtime voice question
- `H`: toggle pet speech bubbles and minimap
- `Esc`: unlock cursor

Do not use legacy `UnityEngine.Input`; this project uses `UnityEngine.InputSystem`.

## UI Notes

HUD rendering is IMGUI in `UnderwaterGameDirector.OnGUI()`.

- `DrawMiniMap()` renders the minimap.
- `DrawThreadNameTags()` renders pet speech bubbles.
- `ToggleThreadHudVisibility()` toggles both.

Pet bubble text comes from:

- `ThreadPetAI.BubbleMessage`
- `ArchivedThreadPet.StatusMessage`

## Terrain And Movement

Terrain mode is detected/configured in `UnderwaterGameDirector`.

Important helpers:

- `UsesSceneTerrain`
- `GetSurfaceY(Vector3 point)`
- `ClampPoint(Vector3 point, float padding)`
- `GetRandomSeafloorPoint(...)`
- `GetRandomMidWaterPoint(...)`

Active pets use terrain-grounded movement when `director.UsesSceneTerrain` is true. Keep movement changes compatible with the terrain scene.

## Voice And Secrets

Optional voice/search settings are loaded from:

```text
UserSettings/UnderwaterApiSettings.json
```

This file may contain API keys and must not be committed. Expected fields are defined in `UnderwaterUserSettings`.

OpenAI realtime voice is handled by `OpenAIRealtimeClient`. Optional external search is handled by `NiaApiClient`.

## Git LFS

The repo uses Git LFS for large terrain and animal assets. Keep `.gitattributes` patterns intact.

Important LFS roots:

- `Assets/TerrainDemoScene_URP/**/*.png`
- `Assets/TerrainDemoScene_URP/**/*.fbx`
- `Assets/TerrainDemoScene_URP/Terrain/Data/*.asset`
- `Assets/ithappy/Animals_FREE/**/*.fbx`
- `Assets/ithappy/Animals_FREE/**/*.png`

If asset files look like tiny pointer files in Unity, run `git lfs pull`.

## Testing

Primary PlayMode tests:

```text
Assets/Tests/PlayMode/UnderwaterBootstrapPlayModeTests.cs
```

Run from Unity Test Runner or from a local Unity executable:

```sh
/Applications/Unity/Hub/Editor/6000.4.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath . \
  -runTests \
  -testPlatform PlayMode \
  -testResults /tmp/underwater-playmode-results.xml \
  -quit
```

Unity batchmode may fail in sandboxed shells if licensing is not initialized. If that happens, say so and ask the user to verify in the editor.

## Commit Conventions

The user asked Codex to coauthor commits going forward. When Codex creates a commit, include:

```text
Co-authored-by: Codex <codex@openai.com>
```

Prefer focused commits. Do not stage unrelated Unity editor churn from `ProjectSettings`, `Packages`, or `UserSettings` unless it is required for the requested change.

## Practical Warnings

- Do not delete `.meta` files for assets that remain in the project.
- Do not commit `UserSettings/UnderwaterApiSettings.json`.
- Do not use destructive git commands unless explicitly requested.
- Keep runtime animal prefabs under `Assets/Resources/ThreadPetAnimals`; `Resources.Load` depends on that path.
- Imported asset YAML often contains trailing whitespace. Avoid broad mechanical whitespace rewrites across imported Unity assets unless the user asks.

