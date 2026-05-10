# agent-maxxing

`agent-maxxing` is the Codex Thread Operating System for embodied AI workflow orchestration. It turns every Codex thread into a live 3D agent animal, then wraps the whole thing in the infrastructure layer the first wave of chat tools forgot: spatial context, realtime state, closed-loop voice control, minimap observability, atmosphere governance, and a Unity-native execution surface built to scale past the prompt box.

Generation was the demo. The real category shift is making agent work visible, queryable, and operational inside an adaptive 3D world. The default scene ships that OS layer as a terrain environment with first-person movement, thread animals, speech bubbles, realtime atmosphere controls, optional voice Q&A, and enough workflow intelligence to make your backlog feel almost on brand.

## Requirements

- Unity `6000.4.6f1`
- Git LFS
- macOS, Windows, or Linux supported by Unity 6
- A local Codex app-server exposing `ws://127.0.0.1:4500` for live thread animals
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

6. In a separate terminal, start the Codex app-server for live thread animals:

   ```sh
   codex app-server --listen ws://127.0.0.1:4500
   ```

7. Press Play in Unity.

The terrain, player, atmosphere, and HUD boot without the app-server. Live Codex thread animals require the websocket bridge at `ws://127.0.0.1:4500`.

## Optional Voice Setup

Voice settings are read from the current runtime settings file:

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

Keyboard and mouse:

- `W/A/S/D`: Move
- Mouse: Look while the cursor is locked
- `Left Shift` / `Right Shift`: Sprint
- `Space`: Jump
- `H`: Toggle thread HUD visibility, including animal speech bubbles and minimap
- Hold `V`: Record a voice request
- Release `V`: Send the voice request
- `Esc`: Unlock mouse cursor
- Left click: Re-lock mouse cursor

Gamepad:

- Left stick: Move
- Right stick: Look
- Left stick button: Sprint
- South face button: Jump

## Voice Control Features

Voice control is push-to-talk on `V` and requires `openAiApiKey` in `UserSettings/UnderwaterApiSettings.json`. The runtime records from the first available microphone, sends the clip to the OpenAI Realtime API, and plays the spoken answer in-game.

Supported voice requests include:

- Ask short questions about the current Unity world, visible thread animals, archived animals, nearby or facing animals, and app-server state.
- Change the realtime atmosphere, including time of day, lighting, fog, rain, storms, snow, clouds, and intensity.
- Create a Codex work thread for game-specific questions, bug reports, investigations, or feature requests.

Creating work threads also requires the local Codex app-server to be connected at `ws://127.0.0.1:4500`.

## How Thread Animals Work

`ForestDirectorBridge` connects to the local Codex app-server and mirrors active and archived threads into Unity snapshots. `ForestGameDirector` owns the world lifecycle and creates one animal object for each thread.

- Active threads use `ThreadAnimalAI`.
- Archived threads use `ArchivedThreadAnimal`.
- 3D animal visuals are created by `ThreadAnimalVisual`.
- Animal prefabs live in `Assets/Resources/ThreadAnimals`.
- Imported animal movement/input scripts are disabled at runtime; movement is controlled by project AI components.

Active animals roam around the terrain, choose targets near the player/camera/home point, sample terrain height through `ForestGameDirector.GetSurfaceY(...)`, and drive imported animal Animator parameters directly. Archived animals idle and hop near their saved terrain position.

Thread animals are created only from app-server snapshots. Empty status messages remain empty, so the app-server is responsible for sending displayable activity text when a non-idle thread should speak.

## Assets

Large binary assets are tracked with Git LFS. This includes the Unity Terrain demo assets and imported 3D animal meshes/textures.

Important asset directories:

- `Assets/TerrainDemoScene_URP`: default scene, terrain, lighting, foliage, rocks, skybox, and URP terrain demo assets
- `Assets/Resources/ThreadAnimals`: runtime-loaded animal prefabs used by `ThreadAnimalVisual`
- `Assets/ithappy/Animals_FREE`: imported animal meshes, materials, textures, animations, and supporting scripts referenced by the thread animal prefabs

If assets appear missing, run:

```sh
git lfs pull
```

## Tests

The project currently has one PlayMode test assembly:

```text
Assets/Tests/PlayMode/Forest.PlayModeTests.asmdef
```

The main test file is:

```text
Assets/Tests/PlayMode/ForestBootstrapPlayModeTests.cs
```

It covers scene bootstrap, first-person runtime setup, terrain lighting/atmosphere behavior, precipitation rendering, animal speech bubbles, thread-title parsing, random animal actions, bridge reasoning summaries, and voice atmosphere command aliases.

To run it in the Unity editor, use:

```text
Window > General > Test Runner > PlayMode
```

For command-line runs, use the repo wrapper:

```sh
Tools/run_playmode_tests.sh
```

The wrapper uses Unity `6000.4.6f1` by default and writes:

```text
Logs/playmode-test.log
TestResults/playmode-results.xml
```

You can override paths with environment variables:

```text
UNITY_EXECUTABLE=/path/to/Unity
PLAYMODE_TEST_LOG=/path/to/playmode-test.log
PLAYMODE_TEST_RESULTS=/path/to/playmode-results.xml
```

If you need to run Unity directly, use:

```sh
mkdir -p Logs TestResults
/Applications/Unity/Hub/Editor/6000.4.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -nographics \
  -projectPath . \
  -runTests \
  -testPlatform PlayMode \
  -testResults "$PWD/TestResults/playmode-results.xml" \
  -logFile "$PWD/Logs/playmode-test.log"
```

## Key Files

- `Assets/Scripts/ForestBootstrap.cs`: runtime scene hook that creates the `ForestGameDirector` when a scene loads
- `Assets/Scripts/ForestGameDirector.cs`: central runtime coordinator for terrain setup, player creation, HUD/minimap UI, atmosphere, thread animals, voice flow, and work-thread creation
- `Assets/Scripts/ForestDirectorBridge.cs`: Codex app-server websocket bridge, thread/archive mirroring, work-thread creation, and lightweight JSON handling
- `Assets/Scripts/ForestDirectorProtocol.cs`: serializable snapshot DTOs shared by the director, bridge, animals, and voice context
- `Assets/Scripts/ForestPlayerController.cs`: first-person keyboard, mouse, and gamepad movement plus HUD and push-to-talk input
- `Assets/Scripts/ThreadAnimalAI.cs`: active thread animal state, roaming behavior, speech-bubble text, and animation state selection
- `Assets/Scripts/ArchivedThreadAnimal.cs`: archived thread animal idle/hop behavior and snapshot export
- `Assets/Scripts/ThreadAnimalVisual.cs`: runtime animal prefab loading from `Resources/ThreadAnimals`, scaling, animation, and imported component disabling
- `Assets/Scripts/CodexAnimalAnimationState.cs`: shared animal animation state enum
- `Assets/Scripts/OpenAIRealtimeClient.cs`: OpenAI Realtime voice client, audio I/O, and voice tool execution
- `Assets/Scripts/ForestUserSettings.cs`: local voice/API settings loader
