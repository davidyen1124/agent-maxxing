# Underwater

Underwater is a Unity 6 project that turns Codex threads into a live 3D world. The current default scene is a terrain demo environment with first-person movement, 3D animal thread pets, speech bubbles above pets, a minimap, realtime atmosphere controls, and optional voice Q&A.

## Requirements

- Unity `6000.4.6f1`
- Git LFS
- macOS, Windows, or Linux supported by Unity 6
- Optional: a local Codex app-server that exposes `ws://127.0.0.1:4500`
- Optional: OpenAI API key for realtime voice Q&A
- Optional: Nia API key for external search from realtime voice answers

## Install

1. Clone the repo:

   ```sh
   git clone https://github.com/davidyen1124/agent-maxxing.git
   cd agent-maxxing
   ```

2. Install and pull Git LFS assets:

   ```sh
   git lfs install
   git lfs pull
   ```

3. Open the project in Unity Hub using Unity `6000.4.6f1`.

4. Let Unity restore packages from `Packages/manifest.json`.

5. Open the default scene:

   ```text
   Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity
   ```

6. Press Play.

The scene works without the Codex app-server, but live thread pets require the bridge to connect to `ws://127.0.0.1:4500`.

## Optional Voice Setup

Voice settings are read from:

```text
UserSettings/UnderwaterApiSettings.json
```

This file is local user state and should not be committed. Example:

```json
{
  "openAiApiKey": "sk-...",
  "openAiRealtimeModel": "gpt-realtime-2",
  "openAiRealtimeVoice": "marin",
  "niaApiKey": "",
  "niaBaseUrl": "https://apigcp.trynia.ai/v2",
  "niaDefaultSearchMode": "universal",
  "niaMaxTokens": 1200,
  "voiceSampleRate": 24000,
  "voiceMaxCaptureSeconds": 8
}
```

`openAiApiKey` enables voice Q&A. `niaApiKey` is optional and only needed when realtime voice answers should search external knowledge.

## Controls

- `W/A/S/D`: Move
- Mouse: Look
- `Left Shift` / `Right Shift`: Sprint
- `Space`: Jump
- `E`: Spawn a Codex work thread from the world
- Hold `V`: Record a realtime voice question
- Release `V`: Send the voice question
- `H`: Toggle thread HUD visibility, including pet speech bubbles and minimap
- `Esc`: Unlock mouse cursor
- Left click: Re-lock mouse cursor

## How Thread Pets Work

`AquariumDirectorBridge` connects to the local Codex app-server and mirrors active and archived threads into Unity snapshots. `UnderwaterGameDirector` owns the world lifecycle and creates one pet object for each thread.

- Active threads use `ThreadPetAI`.
- Archived threads use `ArchivedThreadPet`.
- 3D animal visuals are created by `ThreadPetAnimalVisual`.
- Animal prefabs live in `Assets/Resources/ThreadPetAnimals`.
- Imported animal movement/input scripts are disabled at runtime; movement is controlled by our own AI components.

Active pets roam around the terrain, choose targets near the player/camera/home point, sample terrain height through `UnderwaterGameDirector.GetSurfaceY(...)`, and drive imported animal Animator parameters directly. Archived pets idle and hop near their saved terrain position.

## Assets

Large binary assets are tracked with Git LFS. This includes the Unity Terrain demo assets and imported 3D animal meshes/textures.

Important runtime asset roots:

- `Assets/TerrainDemoScene_URP`
- `Assets/ithappy/Animals_FREE`
- `Assets/Resources/ThreadPetAnimals`

If assets appear missing, run:

```sh
git lfs pull
```

## Tests

Use Unity Test Runner for PlayMode tests:

```text
Window > General > Test Runner > PlayMode
```

The main PlayMode test file is:

```text
Assets/Tests/PlayMode/UnderwaterBootstrapPlayModeTests.cs
```

For command-line runs, use a local Unity executable for version `6000.4.6f1`, for example:

```sh
/Applications/Unity/Hub/Editor/6000.4.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath . \
  -runTests \
  -testPlatform PlayMode \
  -testResults /tmp/underwater-playmode-results.xml \
  -quit
```

## Key Files

- `Assets/Scripts/UnderwaterGameDirector.cs`: world lifecycle, terrain setup, player creation, UI, minimap, thread spawning, voice flow
- `Assets/Scripts/AquariumDirectorBridge.cs`: Codex app-server websocket bridge
- `Assets/Scripts/UnderwaterPlayerController.cs`: first-person movement and keyboard shortcuts
- `Assets/Scripts/ThreadPetAI.cs`: active thread pet behavior
- `Assets/Scripts/ArchivedThreadPet.cs`: archived thread pet behavior
- `Assets/Scripts/ThreadPetAnimalVisual.cs`: 3D animal visual loading, scaling, animation, and imported component disabling
- `Assets/Scripts/OpenAIRealtimeClient.cs`: realtime voice client and tool handling
- `Assets/Scripts/NiaApiClient.cs`: optional Nia search client
- `Assets/Scripts/UnderwaterUserSettings.cs`: local user settings loader

