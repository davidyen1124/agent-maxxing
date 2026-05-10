# Underwater

Underwater is a Unity 6 project that turns Codex threads into a live 3D world. The current default scene is a terrain demo environment with first-person movement, 3D thread animals, speech bubbles above animals, a minimap, realtime atmosphere controls, and optional voice Q&A.

## Requirements

- Unity `6000.4.6f1`
- Git LFS
- macOS, Windows, or Linux supported by Unity 6
- Optional: a local Codex app-server that exposes `ws://127.0.0.1:4500`
- Optional: OpenAI API key for realtime voice Q&A

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

6. Optional: in a separate terminal, start the Codex app-server for live thread pets:

   ```sh
   codex app-server --listen ws://127.0.0.1:4500
   ```

7. Press Play.

The scene works without the Codex app-server. If the bridge does not receive a thread sync shortly after Play starts, Unity spawns local demo thread pets so the terrain scene still has animals. Live Codex thread data requires the bridge to connect to `ws://127.0.0.1:4500`.

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
  "voiceSampleRate": 24000,
  "voiceMaxCaptureSeconds": 8
}
```

`openAiApiKey` enables voice Q&A.

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

## How Thread Animals Work

`ForestDirectorBridge` connects to the local Codex app-server and mirrors active and archived threads into Unity snapshots. `ForestGameDirector` owns the world lifecycle and creates one animal object for each thread.

- Active threads use `ThreadAnimalAI`.
- Archived threads use `ArchivedThreadAnimal`.
- 3D animal visuals are created by `ThreadAnimalVisual`.
- Animal prefabs live in `Assets/Resources/ThreadAnimals`.
- Imported animal movement/input scripts are disabled at runtime; movement is controlled by our own AI components.

Active animals roam around the terrain, choose targets near the player/camera/home point, sample terrain height through `ForestGameDirector.GetSurfaceY(...)`, and drive imported animal Animator parameters directly. Archived animals idle and hop near their saved terrain position.

When the Codex app-server is not running, `ForestGameDirector` creates a small local demo thread set after a short fallback delay. Once the app-server sends a real sync, the real thread IDs replace the demo animals.

## Assets

Large binary assets are tracked with Git LFS. This includes the Unity Terrain demo assets and imported 3D animal meshes/textures.

Important runtime asset roots:

- `Assets/TerrainDemoScene_URP`
- `Assets/ithappy/Animals_FREE`
- `Assets/Resources/ThreadAnimals`

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
Assets/Tests/PlayMode/ForestBootstrapPlayModeTests.cs
```

For command-line runs, use a local Unity executable for version `6000.4.6f1`, for example:

```sh
/Applications/Unity/Hub/Editor/6000.4.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath . \
  -runTests \
  -testPlatform PlayMode \
  -testResults /tmp/forest-playmode-results.xml \
  -quit
```

## Key Files

- `Assets/Scripts/ForestGameDirector.cs`: world lifecycle, terrain setup, player creation, UI, minimap, thread spawning, voice flow
- `Assets/Scripts/ForestDirectorBridge.cs`: Codex app-server websocket bridge
- `Assets/Scripts/ForestPlayerController.cs`: first-person movement and keyboard shortcuts
- `Assets/Scripts/ThreadAnimalAI.cs`: active thread animal behavior
- `Assets/Scripts/ArchivedThreadAnimal.cs`: archived thread animal behavior
- `Assets/Scripts/ThreadAnimalVisual.cs`: 3D animal visual loading, scaling, animation, and imported component disabling
- `Assets/Scripts/OpenAIRealtimeClient.cs`: realtime voice client and tool handling
- `Assets/Scripts/ForestUserSettings.cs`: local user settings loader
