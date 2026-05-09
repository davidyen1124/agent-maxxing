using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

namespace Forest
{
    public sealed class ForestGameDirector : MonoBehaviour
    {
        private const string ForestTaskTitlePrefix = "Forest task ";
        private const string LegacyForestTaskTitlePrefix = "Game task ";
        private const string ForestTaskCounterPrefsKey = "Forest.ForestTask.NextThreadNumber";
        private const float TerrainModeMaxCameraFarClip = 2200f;
        private const float TerrainModeMinCameraFarClip = 900f;
        private const float TerrainModeShadowDistance = 90f;
        private const float TerrainModeTreeDistance = 520f;
        private const float TerrainModeTreeBillboardDistance = 90f;
        private const float TerrainModeDetailDistance = 70f;
        private const float TerrainModeDetailDensity = 0.42f;
        private const float TerrainModeHeightmapPixelError = 9f;
        private const float TerrainModeBasemapDistance = 180f;
        private const float TerrainThreadInitialMinRadius = 28f;
        private const float TerrainThreadInitialMaxRadius = 86f;
        private const float TerrainModeDaySunIntensity = 5f;
        private const float TerrainModeNightSunIntensity = 1.45f;
        private const float DefaultAtmosphereIntensity = 0.55f;

        [SerializeField] private string defaultOpenAiRealtimeModel = "gpt-realtime-2";
        [SerializeField] private string defaultOpenAiRealtimeVoice = "marin";
        [SerializeField] private string defaultNiaBaseUrl = "https://apigcp.trynia.ai/v2";
        [SerializeField] private string defaultNiaSearchMode = NiaApiClient.DefaultSearchMode;
        [SerializeField] private int defaultNiaMaxTokens = 1200;
        [SerializeField] private int defaultVoiceSampleRate = 24000;
        [SerializeField] private float defaultVoiceMaxCaptureSeconds = 8f;

        private readonly Dictionary<string, ThreadAnimalAI> activeThreads = new Dictionary<string, ThreadAnimalAI>();
        private readonly Dictionary<string, ArchivedThreadAnimal> archivedAnimals = new Dictionary<string, ArchivedThreadAnimal>();
        private readonly ConcurrentQueue<AtmosphereCommand> pendingAtmosphereCommands = new ConcurrentQueue<AtmosphereCommand>();
        private readonly ConcurrentQueue<WorkThreadCommand> pendingWorkThreadCommands = new ConcurrentQueue<WorkThreadCommand>();

        private GUIStyle labelStyle;
        private GUIStyle threadTagStyle;
        private GUIStyle speechBubbleStyle;
        private GUIStyle speechBubbleShadowStyle;
        private GUIStyle loadingTitleStyle;
        private GUIStyle loadingStatusStyle;
        private GUIStyle miniMapPanelStyle;
        private Texture2D miniMapPlayerDotTexture;
        private Texture2D miniMapActiveAnimalDotTexture;
        private Texture2D miniMapArchivedAnimalDotTexture;
        private bool threadHudVisible = true;

        private Material groundMaterial;
        private Material foliageMaterial;
        private Material precipitationMaterial;
        private Material sparkleMaterial;
        private ForestDirectorBridge forestBridge;
        private Terrain[] sceneTerrains = Array.Empty<Terrain>();
        private Light atmosphereSun;
        private GameObject atmosphereRoot;
        private ParticleSystem precipitationParticles;
        private ParticleSystem sparkleParticles;
        private Bloom atmosphereBloom;
        private Vignette atmosphereVignette;
        private ColorAdjustments atmosphereColorAdjustments;
        private AudioSource niaVoiceAudioSource;
        private ForestUserSettings apiSettings;
        private OpenAIRealtimeClient realtimeClient;
        private Coroutine worldSyncRoutine;
        private Coroutine niaVoiceCaptureRoutine;
        private QueuedWorldSync queuedWorldSync;
        private int snapshotSequence;
        private string bridgeState = "offline";
        private string directorStatusLine = "Scanning Codex threads";
        private string nearestThreadTitle = "No active threads";
        private string nearestThreadPhase = "idle";
        private bool workThreadSpawnInFlight;
        private int spawnedWorkThreadCount;
        private string workThreadStatusLine = "Realtime work thread tool ready";
        private bool niaVoiceInFlight;
        private bool niaVoiceStopRequested;
        private string niaVoiceDeviceName;
        private string niaVoiceStatusLine = "Voice warming up";
        private string lastAgentActionLine = "Waiting for player action";
        private bool worldSyncLoading;
        private float worldSyncProgress;
        private string worldSyncStatus = "Loading thread animals";
        private bool usingSceneTerrain;
        private string atmosphereTimeOfDay = "day";
        private string atmosphereWeather = "clear";
        private float atmosphereIntensity = DefaultAtmosphereIntensity;
        private string atmosphereMood = "calm";

        private sealed class QueuedWorldSync
        {
            public List<ForestThreadSnapshot> threads;
            public List<ForestArchivedThreadSnapshot> archivedAnimals;
            public string detail;
        }

        private sealed class FacingAnimalContext
        {
            public string kind;
            public string animalName;
            public string title;
            public string phase;
            public float distance;
            public float angle;
        }

        private sealed class AtmosphereCommand
        {
            public string timeOfDay;
            public string weather;
            public float intensity;
            public string mood;
        }

        private sealed class WorkThreadCommand
        {
            public string request;
            public string title;
        }

        public static ForestGameDirector Instance { get; private set; }

        public Bounds PlayBounds { get; private set; }

        public float GroundY => PlayBounds.min.y + 0.5f;

        public bool UsesSceneTerrain => usingSceneTerrain;

        public ForestPlayerController Player { get; private set; }

        public int ActiveThreadCount => activeThreads.Count;

        public int ArchivedAnimalCount => archivedAnimals.Count;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            PlayBounds = new Bounds(new Vector3(0f, 8f, 0f), new Vector3(92f, 18f, 92f));
            CreateSharedMaterials();
        }

        private void Start()
        {
            usingSceneTerrain = TryConfigureSceneTerrainWorld();

            if (usingSceneTerrain)
            {
                PrepareImportedTerrainScene();
            }
            else
            {
                ConfigureLighting();
                ConfigurePostProcessing();
                BuildArena();
            }

            ConfigureAtmosphereController();
            ApplyAtmosphereProfile();
            CreatePlayer();
            ReloadApiSettings();
            _ = WarmRealtimeVoiceSessionAsync();
            AttachForestBridge(true);
        }

        private void Update()
        {
            DrainAtmosphereCommands();
            DrainWorkThreadCommands();
            UpdateAtmosphereEmitterPosition();
            UpdateNearestThreadStatus();
        }

        private void OnDestroy()
        {
            if (!string.IsNullOrEmpty(niaVoiceDeviceName) && Microphone.IsRecording(niaVoiceDeviceName))
            {
                Microphone.End(niaVoiceDeviceName);
            }

            if (realtimeClient != null)
            {
                _ = realtimeClient.CloseAsync();
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnGUI()
        {
            if (Player == null)
            {
                return;
            }

            EnsureGuiStyles();

            if (threadHudVisible)
            {
                DrawMiniMap();
            }

            if (worldSyncLoading)
            {
                DrawLoadingOverlay();
                return;
            }

            if (threadHudVisible)
            {
                DrawThreadNameTags();
            }
        }

        public void ToggleThreadHudVisibility()
        {
            threadHudVisible = !threadHudVisible;
        }

        public void SyncThreadWorld(IReadOnlyList<ForestThreadSnapshot> threads, IReadOnlyList<ForestArchivedThreadSnapshot> syncedArchivedAnimals, string detail)
        {
            QueuedWorldSync sync = new QueuedWorldSync
            {
                threads = threads != null ? new List<ForestThreadSnapshot>(threads) : new List<ForestThreadSnapshot>(),
                archivedAnimals = syncedArchivedAnimals != null ? new List<ForestArchivedThreadSnapshot>(syncedArchivedAnimals) : new List<ForestArchivedThreadSnapshot>(),
                detail = detail
            };

            if (worldSyncRoutine != null)
            {
                queuedWorldSync = sync;
                return;
            }

            worldSyncRoutine = StartCoroutine(RunQueuedThreadWorldSync(sync));
        }

        private IEnumerator RunQueuedThreadWorldSync(QueuedWorldSync initialSync)
        {
            QueuedWorldSync sync = initialSync;

            while (sync != null)
            {
                queuedWorldSync = null;
                yield return ApplyThreadWorldSync(sync);
                sync = queuedWorldSync;
            }

            worldSyncLoading = false;
            worldSyncRoutine = null;
        }

        private IEnumerator ApplyThreadWorldSync(QueuedWorldSync sync)
        {
            int totalWork = sync.threads.Count + sync.archivedAnimals.Count + activeThreads.Count + archivedAnimals.Count;
            int completedWork = 0;
            worldSyncLoading = CountWorldSyncMutations(sync) > 8;
            SetWorldSyncProgress(0, Mathf.Max(1, totalWork), "Loading thread animals");

            HashSet<string> liveIds = new HashSet<string>();

            if (sync.threads != null)
            {
                for (int i = 0; i < sync.threads.Count; i++)
                {
                    ForestThreadSnapshot snapshot = sync.threads[i];
                    completedWork++;

                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        SetWorldSyncProgress(completedWork, totalWork, "Loading thread animals");
                        yield return null;
                        continue;
                    }

                    liveIds.Add(snapshot.id);

                    if (activeThreads.TryGetValue(snapshot.id, out ThreadAnimalAI existing))
                    {
                        existing.ApplySnapshot(snapshot);
                    }
                    else
                    {
                        GameObject threadObject = new GameObject($"Thread {snapshot.id}");
                        ThreadAnimalAI threadAnimal = threadObject.AddComponent<ThreadAnimalAI>();

                        if (threadAnimal.Initialize(this, snapshot))
                        {
                            activeThreads[snapshot.id] = threadAnimal;
                        }
                        else
                        {
                            Destroy(threadObject);
                        }
                    }

                    SetWorldSyncProgress(completedWork, totalWork, $"Loading thread animals {completedWork}/{totalWork}");
                    yield return null;
                }
            }

            List<string> staleActiveIds = new List<string>();

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                if (!liveIds.Contains(pair.Key))
                {
                    staleActiveIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleActiveIds.Count; i++)
            {
                string id = staleActiveIds[i];
                completedWork++;

                if (activeThreads.TryGetValue(id, out ThreadAnimalAI threadAnimal))
                {
                    if (threadAnimal != null)
                    {
                        Destroy(threadAnimal.gameObject);
                    }

                    activeThreads.Remove(id);
                }

                SetWorldSyncProgress(completedWork, totalWork, $"Cleaning up thread animals {completedWork}/{totalWork}");
                yield return null;
            }

            HashSet<string> archivedIds = new HashSet<string>();

            if (sync.archivedAnimals != null)
            {
                for (int i = 0; i < sync.archivedAnimals.Count; i++)
                {
                    ForestArchivedThreadSnapshot snapshot = sync.archivedAnimals[i];
                    completedWork++;

                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        SetWorldSyncProgress(completedWork, totalWork, "Loading archived animals");
                        yield return null;
                        continue;
                    }

                    archivedIds.Add(snapshot.id);

                    if (archivedAnimals.ContainsKey(snapshot.id))
                    {
                        SetWorldSyncProgress(completedWork, totalWork, $"Loading archived animals {completedWork}/{totalWork}");
                        yield return null;
                        continue;
                    }

                    GameObject archivedAnimalObject = new GameObject($"Archived Animal {snapshot.id}");
                    ArchivedThreadAnimal archivedAnimal = archivedAnimalObject.AddComponent<ArchivedThreadAnimal>();

                    if (archivedAnimal.Initialize(this, snapshot))
                    {
                        archivedAnimals[snapshot.id] = archivedAnimal;
                    }
                    else
                    {
                        Destroy(archivedAnimalObject);
                    }

                    SetWorldSyncProgress(completedWork, totalWork, $"Loading archived animals {completedWork}/{totalWork}");
                    yield return null;
                }
            }

            List<string> staleArchivedIds = new List<string>();

            foreach (KeyValuePair<string, ArchivedThreadAnimal> pair in archivedAnimals)
            {
                if (!archivedIds.Contains(pair.Key))
                {
                    staleArchivedIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleArchivedIds.Count; i++)
            {
                string id = staleArchivedIds[i];
                completedWork++;

                if (archivedAnimals.TryGetValue(id, out ArchivedThreadAnimal archivedAnimal))
                {
                    if (archivedAnimal != null)
                    {
                        Destroy(archivedAnimal.gameObject);
                    }

                    archivedAnimals.Remove(id);
                }

                SetWorldSyncProgress(completedWork, totalWork, $"Cleaning up archived animals {completedWork}/{totalWork}");
                yield return null;
            }

            directorStatusLine = string.IsNullOrWhiteSpace(sync.detail)
                ? $"Synced {ActiveThreadCount} roaming threads and {ArchivedAnimalCount} archived animals."
                : sync.detail;
            PersistNextWorkThreadNumber(FindHighestExistingForestTaskNumber() + 1);
            UpdateNearestThreadStatus();
            SetWorldSyncProgress(totalWork, Mathf.Max(1, totalWork), "Thread animals ready");
            worldSyncLoading = false;
        }

        private void SetWorldSyncProgress(int completedWork, int totalWork, string status)
        {
            worldSyncProgress = totalWork <= 0 ? 1f : Mathf.Clamp01((float)completedWork / totalWork);

            if (!string.IsNullOrWhiteSpace(status))
            {
                worldSyncStatus = status;
            }
        }

        private int CountWorldSyncMutations(QueuedWorldSync sync)
        {
            HashSet<string> incomingActiveIds = new HashSet<string>();
            HashSet<string> incomingArchivedIds = new HashSet<string>();

            if (sync.threads != null)
            {
                for (int i = 0; i < sync.threads.Count; i++)
                {
                    ForestThreadSnapshot snapshot = sync.threads[i];

                    if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        incomingActiveIds.Add(snapshot.id);
                    }
                }
            }

            if (sync.archivedAnimals != null)
            {
                for (int i = 0; i < sync.archivedAnimals.Count; i++)
                {
                    ForestArchivedThreadSnapshot snapshot = sync.archivedAnimals[i];

                    if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        incomingArchivedIds.Add(snapshot.id);
                    }
                }
            }

            int mutationCount = 0;

            foreach (string id in incomingActiveIds)
            {
                if (!activeThreads.ContainsKey(id))
                {
                    mutationCount++;
                }
            }

            foreach (string id in activeThreads.Keys)
            {
                if (!incomingActiveIds.Contains(id))
                {
                    mutationCount++;
                }
            }

            foreach (string id in incomingArchivedIds)
            {
                if (!archivedAnimals.ContainsKey(id))
                {
                    mutationCount++;
                }
            }

            foreach (string id in archivedAnimals.Keys)
            {
                if (!incomingArchivedIds.Contains(id))
                {
                    mutationCount++;
                }
            }

            return mutationCount;
        }

        public void UpdateBridgeState(string state, string detail)
        {
            bridgeState = string.IsNullOrWhiteSpace(state) ? "offline" : state;

            if (!string.IsNullOrWhiteSpace(detail))
            {
                directorStatusLine = detail;
            }
        }

        public void BeginRealtimeVoiceQuestionFromPlayer()
        {
            PauseRealtimeVoicePlayback();

            if (niaVoiceCaptureRoutine != null && !string.IsNullOrEmpty(niaVoiceDeviceName) && Microphone.IsRecording(niaVoiceDeviceName))
            {
                LogRealtimeVoice("Start ignored because microphone capture is already recording.");
                SetNiaVoiceStatus("Already recording.");
                return;
            }

            if (niaVoiceInFlight)
            {
                LogRealtimeVoice("Start ignored because a realtime voice request is already in flight.");
                SetNiaVoiceStatus("Voice request already in flight.");
                return;
            }

            ReloadApiSettings();
            bool openAiKeySet = realtimeClient != null && realtimeClient.HasApiKey;
            LogRealtimeVoice($"Start requested. openAiKeySet={openAiKeySet}, niaKeySet={apiSettings != null && !string.IsNullOrWhiteSpace(apiSettings.niaApiKey)}");

            if (!openAiKeySet)
            {
                string message = $"Missing OpenAI API key. Set openAiApiKey in {ForestUserSettings.RelativePath}.";
                SetNiaVoiceStatus(message);
                Debug.LogWarning(message);
                return;
            }

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                SetNiaVoiceStatus("No microphone device is available.");
                Debug.LogWarning("No microphone device is available.");
                return;
            }

            niaVoiceStopRequested = false;
            niaVoiceCaptureRoutine = StartCoroutine(CaptureRealtimeVoiceQuestion());
            SetNiaVoiceStatus("Recording request...");
            LogRealtimeVoice("Microphone capture started.");
        }

        public void EndRealtimeVoiceQuestionFromPlayer()
        {
            if (niaVoiceCaptureRoutine == null || string.IsNullOrEmpty(niaVoiceDeviceName) || !Microphone.IsRecording(niaVoiceDeviceName))
            {
                LogRealtimeVoice("Stop ignored because microphone capture is not recording.");
                SetNiaVoiceStatus("Not recording.");
                return;
            }

            niaVoiceStopRequested = true;
            Microphone.End(niaVoiceDeviceName);
            SetNiaVoiceStatus("Processing request...");
            LogRealtimeVoice("Microphone capture stopped by player.");
        }

        private void PauseRealtimeVoicePlayback()
        {
            if (niaVoiceAudioSource == null || !niaVoiceAudioSource.isPlaying)
            {
                return;
            }

            niaVoiceAudioSource.Pause();
        }

        private void ReloadApiSettings()
        {
            apiSettings = ForestUserSettings.Load();
            string openAiRealtimeModel = apiSettings.OpenAiRealtimeModelOr(defaultOpenAiRealtimeModel);
            NiaApiClient configuredNiaClient = CreateNiaClient(apiSettings);

            if (realtimeClient == null || !realtimeClient.Matches(apiSettings.openAiApiKey, openAiRealtimeModel))
            {
                if (realtimeClient != null)
                {
                    _ = realtimeClient.CloseAsync();
                }

                realtimeClient = new OpenAIRealtimeClient(
                    apiSettings.openAiApiKey,
                    openAiRealtimeModel,
                    configuredNiaClient,
                    HandleRealtimeWorldCommand,
                    HandleRealtimeWorkThreadCommand);
            }
            else
            {
                realtimeClient.SetNiaClient(configuredNiaClient);
            }

            UpdateNiaVoiceReadinessStatus();
        }

        private NiaApiClient CreateNiaClient(ForestUserSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            return new NiaApiClient(
                settings.NiaBaseUrlOr(defaultNiaBaseUrl),
                settings.niaApiKey,
                settings.NiaDefaultSearchModeOr(defaultNiaSearchMode),
                settings.niaRepositories,
                settings.niaDataSources,
                settings.NiaMaxTokensOr(defaultNiaMaxTokens));
        }

        private async Task WarmRealtimeVoiceSessionAsync()
        {
            if (realtimeClient == null || !realtimeClient.HasApiKey)
            {
                return;
            }

            try
            {
                string voice = apiSettings != null
                    ? apiSettings.OpenAiRealtimeVoiceOr(defaultOpenAiRealtimeVoice)
                    : defaultOpenAiRealtimeVoice;
                await realtimeClient.WarmUpAnswerSessionAsync(voice, CancellationToken.None);
            }
            catch (Exception ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                Debug.LogWarning($"Realtime voice warm-up unavailable: {Shorten(message, 96)}");
            }
        }

        public ForestDirectorSnapshot CreateSnapshot()
        {
            List<ForestThreadSnapshot> threadSnapshots = new List<ForestThreadSnapshot>(activeThreads.Count);

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                if (pair.Value != null)
                {
                    threadSnapshots.Add(pair.Value.CreateSnapshot());
                }
            }

            List<ForestArchivedThreadSnapshot> archivedAnimalSnapshots = new List<ForestArchivedThreadSnapshot>(archivedAnimals.Count);

            foreach (KeyValuePair<string, ArchivedThreadAnimal> pair in archivedAnimals)
            {
                if (pair.Value != null)
                {
                    archivedAnimalSnapshots.Add(pair.Value.CreateSnapshot());
                }
            }

            return new ForestDirectorSnapshot
            {
                sequence = ++snapshotSequence,
                capturedAtUtc = DateTime.UtcNow.ToString("o"),
                summary = BuildWorldSummary(),
                metrics = new ForestDirectorMetrics
                {
                    activeThreads = ActiveThreadCount,
                    archivedAnimals = ArchivedAnimalCount,
                    bridgeState = bridgeState
                },
                player = new ForestPlayerSnapshot
                {
                    position = SerializableVector3.FromVector3(Player != null ? Player.transform.position : Vector3.zero),
                    forward = SerializableVector3.FromVector3(Player != null ? Player.transform.forward : Vector3.forward),
                    boostNormalized = Player != null ? Player.SprintEnergyNormalized : 0f,
                    hasPointerLock = Player != null && Player.HasPointerLock
                },
                threads = threadSnapshots.ToArray(),
                archivedAnimals = archivedAnimalSnapshots.ToArray()
            };
        }

        public Vector3 GetRandomPoint(float margin = 4f)
        {
            Vector3 min = PlayBounds.min + Vector3.one * margin;
            Vector3 max = PlayBounds.max - Vector3.one * margin;

            return new Vector3(
                Random.Range(min.x, max.x),
                Random.Range(min.y, max.y),
                Random.Range(min.z, max.z));
        }

        public Vector3 GetRandomRoamingPoint(float margin = 8f)
        {
            Vector3 point = usingSceneTerrain
                ? GetRandomTerrainPointNearPlayer(margin, TerrainThreadInitialMinRadius, TerrainThreadInitialMaxRadius)
                : GetRandomPoint(margin);

            if (usingSceneTerrain)
            {
                point.y = Mathf.Min(PlayBounds.max.y - 2f, GetSurfaceY(point) + Random.Range(3.5f, 11f));
            }
            else
            {
                point.y = Random.Range(GroundY + 4f, PlayBounds.max.y - 2f);
            }

            return point;
        }

        public Vector3 GetRandomGroundPoint(float margin = 5f)
        {
            Vector3 point = usingSceneTerrain
                ? GetRandomTerrainPointNearPlayer(margin, 9f, 34f)
                : GetRandomPoint(margin);
            point.y = usingSceneTerrain
                ? GetSurfaceY(point) + Random.Range(0.45f, 1.15f)
                : GroundY + Random.Range(0.45f, 1.15f);
            return point;
        }

        private Vector3 GetRandomTerrainPointNearPlayer(float margin, float minRadius, float maxRadius)
        {
            Vector3 origin = Player != null
                ? Player.transform.position
                : Camera.main != null
                    ? Camera.main.transform.position
                    : PlayBounds.center;
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float radius = Random.Range(minRadius, maxRadius);
            Vector3 point = origin + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            return ClampPoint(point, margin);
        }

        public float GetSurfaceY(Vector3 point)
        {
            Terrain terrain = FindTerrainAt(point);

            if (terrain == null)
            {
                return GroundY;
            }

            return terrain.SampleHeight(point) + terrain.transform.position.y;
        }

        public Vector3 ClampPoint(Vector3 point, float padding = 1f)
        {
            Vector3 min = PlayBounds.min + Vector3.one * padding;
            Vector3 max = PlayBounds.max - Vector3.one * padding;

            point.x = Mathf.Clamp(point.x, min.x, max.x);
            point.y = Mathf.Clamp(point.y, min.y, max.y);
            point.z = Mathf.Clamp(point.z, min.z, max.z);
            return point;
        }

        private void EnsureGuiStyles()
        {
            if (labelStyle != null)
            {
                return;
            }

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.92f, 0.98f, 1f) },
                fontSize = 13
            };

            threadTagStyle = new GUIStyle(labelStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Clip,
                wordWrap = true,
                padding = new RectOffset(14, 14, 8, 8),
                normal = { textColor = new Color(0.82f, 0.98f, 1f) }
            };

            speechBubbleStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(16, 16, 9, 9),
                border = new RectOffset(14, 14, 14, 14)
            };
            speechBubbleStyle.normal.background = CreateRoundedRectTexture(
                40,
                40,
                14f,
                new Color(0.02f, 0.14f, 0.18f, 0.86f),
                new Color(0.27f, 0.84f, 0.9f, 0.78f),
                2f);

            speechBubbleShadowStyle = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(16, 16, 16, 16)
            };
            speechBubbleShadowStyle.normal.background = CreateRoundedRectTexture(
                44,
                44,
                16f,
                new Color(0f, 0.02f, 0.03f, 0.38f),
                new Color(0f, 0.02f, 0.03f, 0f),
                0f);

            loadingTitleStyle = new GUIStyle(labelStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 1f, 1f) }
            };

            loadingStatusStyle = new GUIStyle(labelStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(0.76f, 0.95f, 1f) }
            };

            miniMapPanelStyle = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(18, 18, 18, 18),
                padding = new RectOffset(10, 10, 10, 10)
            };
            miniMapPanelStyle.normal.background = CreateRoundedRectTexture(
                48,
                48,
                18f,
                new Color(0.01f, 0.05f, 0.07f, 0.76f),
                new Color(0.18f, 0.86f, 0.9f, 0.5f),
                1f);

            miniMapPlayerDotTexture = CreateCircleTexture(18, new Color(0.95f, 1f, 0.92f, 1f), new Color(0.12f, 0.25f, 0.23f, 1f), 2f);
            miniMapActiveAnimalDotTexture = CreateCircleTexture(14, new Color(0.2f, 0.95f, 1f, 0.96f), new Color(0.78f, 1f, 1f, 0.82f), 1f);
            miniMapArchivedAnimalDotTexture = CreateCircleTexture(12, new Color(1f, 0.72f, 0.32f, 0.95f), new Color(1f, 0.92f, 0.64f, 0.78f), 1f);
        }

        private void DrawMiniMap()
        {
            const float mapRangeMeters = 90f;
            float size = Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) * 0.19f, 118f, 158f);
            Rect panelRect = new Rect(Screen.width - size - 12f, 12f, size, size);
            Rect mapRect = new Rect(panelRect.x + 10f, panelRect.y + 10f, panelRect.width - 20f, panelRect.height - 20f);
            Vector2 center = mapRect.center;
            float radius = Mathf.Min(mapRect.width, mapRect.height) * 0.5f;

            GUI.Box(panelRect, GUIContent.none, miniMapPanelStyle);

            Color previousColor = GUI.color;
            GUI.color = new Color(0.1f, 0.42f, 0.48f, 0.24f);
            GUI.DrawTexture(new Rect(center.x - 0.5f, mapRect.y + 2f, 1f, mapRect.height - 4f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(mapRect.x + 2f, center.y - 0.5f, mapRect.width - 4f, 1f), Texture2D.whiteTexture);
            GUI.color = previousColor;

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                if (pair.Value != null)
                {
                    DrawMiniMapDot(pair.Value.transform.position, center, radius, mapRangeMeters, miniMapActiveAnimalDotTexture, 8f);
                }
            }

            foreach (KeyValuePair<string, ArchivedThreadAnimal> pair in archivedAnimals)
            {
                if (pair.Value != null)
                {
                    DrawMiniMapDot(pair.Value.transform.position, center, radius, mapRangeMeters, miniMapArchivedAnimalDotTexture, 7f);
                }
            }

            DrawTextureCentered(center, miniMapPlayerDotTexture, 12f);
        }

        private void DrawMiniMapDot(Vector3 worldPosition, Vector2 center, float radius, float mapRangeMeters, Texture2D texture, float size)
        {
            if (Player == null)
            {
                return;
            }

            Vector3 offset = worldPosition - Player.transform.position;
            Vector2 mapOffset = new Vector2(offset.x, -offset.z) / Mathf.Max(1f, mapRangeMeters) * radius;

            if (mapOffset.sqrMagnitude > radius * radius)
            {
                mapOffset = mapOffset.normalized * radius;
            }

            DrawTextureCentered(center + mapOffset, texture, size);
        }

        private static void DrawTextureCentered(Vector2 center, Texture2D texture, float size)
        {
            if (texture == null)
            {
                return;
            }

            GUI.DrawTexture(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size), texture);
        }

        private static Texture2D CreateRoundedRectTexture(int width, int height, float radius, Color fillColor, Color borderColor, float borderWidth)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Forest Speech Bubble"
            };
            Color clear = new Color(1f, 1f, 1f, 0f);
            float maxX = width - 1f;
            float maxY = height - 1f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float distanceFromEdge = Mathf.Min(Mathf.Min(x, maxX - x), Mathf.Min(y, maxY - y));
                    float cornerX = x < radius ? radius : maxX - x < radius ? maxX - radius : x;
                    float cornerY = y < radius ? radius : maxY - y < radius ? maxY - radius : y;
                    float cornerDistance = Vector2.Distance(new Vector2(x, y), new Vector2(cornerX, cornerY));

                    if (cornerDistance > radius)
                    {
                        texture.SetPixel(x, y, clear);
                    }
                    else if (borderWidth > 0f && (distanceFromEdge < borderWidth || cornerDistance > radius - borderWidth))
                    {
                        texture.SetPixel(x, y, borderColor);
                    }
                    else
                    {
                        texture.SetPixel(x, y, fillColor);
                    }
                }
            }

            texture.Apply();
            return texture;
        }

        private static Texture2D CreateCircleTexture(int size, Color fillColor, Color borderColor, float borderWidth)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Forest Mini Map Dot"
            };
            Color clear = new Color(1f, 1f, 1f, 0f);
            float center = (size - 1f) * 0.5f;
            float radius = center;
            float innerRadius = Mathf.Max(0f, radius - borderWidth);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));

                    if (distance > radius)
                    {
                        texture.SetPixel(x, y, clear);
                    }
                    else if (distance >= innerRadius)
                    {
                        texture.SetPixel(x, y, borderColor);
                    }
                    else
                    {
                        texture.SetPixel(x, y, fillColor);
                    }
                }
            }

            texture.Apply();
            return texture;
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text) || maxLength < 4)
            {
                return string.Empty;
            }

            string trimmed = text.Trim();

            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, maxLength - 3) + "...";
        }

        private void DrawThreadNameTags()
        {
            Camera camera = Camera.main;

            if (camera == null)
            {
                return;
            }

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                ThreadAnimalAI thread = pair.Value;

                if (thread == null)
                {
                    continue;
                }

                string message = Shorten(thread.BubbleMessage, 64);
                DrawNameTag(camera, message, thread.BubbleAnchorWorldPosition);
            }

            foreach (KeyValuePair<string, ArchivedThreadAnimal> pair in archivedAnimals)
            {
                ArchivedThreadAnimal archivedAnimal = pair.Value;

                if (archivedAnimal == null)
                {
                    continue;
                }

                string message = Shorten(archivedAnimal.StatusMessage, 64);
                DrawNameTag(camera, message, archivedAnimal.transform.position + Vector3.up * 1.05f);
            }
        }

        private void DrawNameTag(Camera camera, string message, Vector3 worldAnchor)
        {
            if (camera == null || !ShouldDrawBubbleMessage(message))
            {
                return;
            }

            Vector3 screenPoint = camera.WorldToScreenPoint(worldAnchor);

            if (screenPoint.z <= 0f)
            {
                return;
            }

            if (screenPoint.x < 0f || screenPoint.x > Screen.width || screenPoint.y < 0f || screenPoint.y > Screen.height)
            {
                return;
            }

            GUIContent content = new GUIContent(message);
            float width = Mathf.Clamp(threadTagStyle.CalcSize(content).x + 32f, 150f, 300f);
            float textHeight = threadTagStyle.CalcHeight(content, width - 28f);
            float height = Mathf.Clamp(textHeight + 18f, 42f, 92f);
            float x = screenPoint.x - (width * 0.5f);
            float y = Screen.height - screenPoint.y - height - 18f;
            Rect tagRect = new Rect(x, y, width, height);
            Rect shadowRect = new Rect(tagRect.x + 2f, tagRect.y + 3f, tagRect.width, tagRect.height);

            GUI.color = Color.white;
            GUI.Box(shadowRect, GUIContent.none, speechBubbleShadowStyle);
            GUI.Box(tagRect, GUIContent.none, speechBubbleStyle);
            GUI.Label(tagRect, content, threadTagStyle);
        }

        private static bool ShouldDrawBubbleMessage(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && !string.Equals(message.Trim(), "Idle", StringComparison.OrdinalIgnoreCase);
        }

        private void DrawLoadingOverlay()
        {
            float progress = worldSyncProgress;
            string title = "Syncing threads";
            string status = worldSyncStatus;
            float width = Mathf.Clamp(Screen.width - 48f, 280f, 520f);
            float height = 118f;
            float x = (Screen.width - width) * 0.5f;
            float y = Mathf.Max(28f, (Screen.height - height) * 0.5f);
            Rect panelRect = new Rect(x, y, width, height);
            Rect titleRect = new Rect(x + 18f, y + 15f, width - 36f, 28f);
            Rect barRect = new Rect(x + 34f, y + 58f, width - 68f, 16f);
            Rect fillRect = new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(progress), barRect.height);
            Rect statusRect = new Rect(x + 22f, y + 84f, width - 44f, 22f);
            Color previousColor = GUI.color;

            GUI.color = new Color(0.01f, 0.07f, 0.1f, 0.82f);
            GUI.DrawTexture(panelRect, Texture2D.whiteTexture);

            GUI.color = new Color(0.2f, 0.95f, 1f, 0.18f);
            GUI.DrawTexture(new Rect(panelRect.x, panelRect.yMax - 2f, panelRect.width, 2f), Texture2D.whiteTexture);

            GUI.color = new Color(0.01f, 0.19f, 0.23f, 0.95f);
            GUI.DrawTexture(barRect, Texture2D.whiteTexture);

            GUI.color = new Color(0.3f, 0.95f, 0.85f, 0.96f);
            GUI.DrawTexture(fillRect, Texture2D.whiteTexture);

            GUI.color = previousColor;
            GUI.Label(titleRect, title, loadingTitleStyle);
            GUI.Label(statusRect, status, loadingStatusStyle);
        }

        private void UpdateNearestThreadStatus()
        {
            if (Player == null || activeThreads.Count == 0)
            {
                nearestThreadTitle = "No active threads";
                nearestThreadPhase = "idle";
                return;
            }

            ThreadAnimalAI nearest = null;
            float nearestDistance = float.MaxValue;
            Vector3 playerPosition = Player.transform.position;

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                ThreadAnimalAI thread = pair.Value;

                if (thread == null)
                {
                    continue;
                }

                float distance = Vector3.SqrMagnitude(thread.transform.position - playerPosition);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = thread;
                }
            }

            if (nearest == null)
            {
                nearestThreadTitle = "No active threads";
                nearestThreadPhase = "idle";
                return;
            }

            nearestThreadTitle = nearest.Title;
            nearestThreadPhase = string.IsNullOrWhiteSpace(nearest.Phase) ? "unknown" : nearest.Phase;
        }

        private string BuildWorldSummary()
        {
            StringBuilder summary = new StringBuilder();
            Vector3 playerPosition = Player != null ? Player.transform.position : Vector3.zero;
            summary.Append("Player at ");
            summary.Append(FormatVector(playerPosition));
            summary.Append(". ");
            summary.Append(ActiveThreadCount);
            summary.Append(" active thread animals roaming, ");
            summary.Append(ArchivedAnimalCount);
            summary.Append(" archived animal companions resting on the ground. ");
            summary.Append("Atmosphere is ");
            summary.Append(BuildAtmosphereSummary());
            summary.Append(". ");

            ThreadAnimalAI nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                ThreadAnimalAI thread = pair.Value;

                if (thread == null)
                {
                    continue;
                }

                float distance = Vector3.SqrMagnitude(thread.transform.position - playerPosition);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = thread;
                }
            }

            if (nearest != null)
            {
                summary.Append("Nearest thread is '");
                summary.Append(nearest.Title);
                summary.Append("' in phase ");
                summary.Append(nearest.Phase);
                summary.Append(" showing '");
                summary.Append(nearest.StatusMessage);
                summary.Append("'");
                summary.Append(" at ");
                summary.Append(FormatVector(nearest.transform.position));
                summary.Append(". ");
            }

            summary.Append(directorStatusLine);
            return summary.ToString().Trim();
        }

        private string BuildFacingAnimalSummary()
        {
            if (Player == null)
            {
                return "Player facing direction is unavailable.";
            }

            Camera camera = Camera.main;
            Vector3 origin = camera != null ? camera.transform.position : Player.transform.position;
            Vector3 forward = camera != null ? camera.transform.forward : Player.transform.forward;

            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();

            List<FacingAnimalContext> facingAnimals = new List<FacingAnimalContext>();

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                ThreadAnimalAI thread = pair.Value;

                if (thread == null)
                {
                    continue;
                }

                AddFacingAnimalIfVisible(
                    facingAnimals,
                    "active thread animal",
                    thread.AnimalDisplayName,
                    thread.Title,
                    thread.Phase,
                    thread.transform.position,
                    origin,
                    forward);
            }

            foreach (KeyValuePair<string, ArchivedThreadAnimal> pair in archivedAnimals)
            {
                ArchivedThreadAnimal archivedAnimal = pair.Value;

                if (archivedAnimal == null)
                {
                    continue;
                }

                AddFacingAnimalIfVisible(
                    facingAnimals,
                    "archived thread animal",
                    archivedAnimal.AnimalDisplayName,
                    archivedAnimal.Title,
                    "archived",
                    archivedAnimal.transform.position,
                    origin,
                    forward);
            }

            facingAnimals.Sort((left, right) =>
            {
                int angleComparison = left.angle.CompareTo(right.angle);
                return angleComparison != 0 ? angleComparison : left.distance.CompareTo(right.distance);
            });

            StringBuilder summary = new StringBuilder();
            if (facingAnimals.Count == 0)
            {
                summary.Append("No thread animal is currently in front of the player.");
                return summary.ToString();
            }

            FacingAnimalContext primary = facingAnimals[0];
            summary.Append("Front animal: ");
            AppendFacingAnimal(summary, primary);

            int extraCount = Mathf.Min(2, facingAnimals.Count - 1);

            if (extraCount > 0)
            {
                summary.Append(" Other front-row cameos: ");

                for (int index = 0; index < extraCount; index++)
                {
                    if (index > 0)
                    {
                        summary.Append("; ");
                    }

                    AppendFacingAnimal(summary, facingAnimals[index + 1]);
                }
            }

            return summary.ToString();
        }

        private static void AddFacingAnimalIfVisible(
            List<FacingAnimalContext> facingAnimals,
            string kind,
            string animalName,
            string title,
            string phase,
            Vector3 position,
            Vector3 origin,
            Vector3 forward)
        {
            Vector3 offset = position - origin;
            float distance = offset.magnitude;

            if (distance < 0.05f)
            {
                return;
            }

            float alignment = Vector3.Dot(forward, offset / distance);

            if (alignment < 0.5f)
            {
                return;
            }

            facingAnimals.Add(new FacingAnimalContext
            {
                kind = kind,
                animalName = string.IsNullOrWhiteSpace(animalName) ? "unknown animal" : animalName.Trim(),
                title = string.IsNullOrWhiteSpace(title) ? "Untitled thread" : title.Trim(),
                phase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase.Trim(),
                distance = distance,
                angle = Mathf.Acos(Mathf.Clamp(alignment, -1f, 1f)) * Mathf.Rad2Deg
            });
        }

        private static void AppendFacingAnimal(StringBuilder summary, FacingAnimalContext animal)
        {
            summary.Append("sprite '");
            summary.Append(animal.animalName);
            summary.Append("', ");
            summary.Append(animal.kind);
            summary.Append(", ");
            summary.Append("thread title '");
            summary.Append(animal.title);
            summary.Append("', mood '");
            summary.Append(animal.phase);
            summary.Append("'.");
        }

        private string BuildWorkThreadPrompt(string title, string request)
        {
            StringBuilder prompt = new StringBuilder();
            prompt.Append("A player made a realtime voice request inside the Forest world.");
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.Append("Thread title: ");
            prompt.Append(title);
            prompt.AppendLine();
            prompt.Append("Player request: ");
            prompt.Append(string.IsNullOrWhiteSpace(request) ? "No request provided." : request.Trim());
            prompt.AppendLine();
            prompt.Append("World state: ");
            prompt.Append(BuildWorldSummary());
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.Append("Use the current workspace when it is relevant. Start by stating what useful next step you can take from this context.");
            return prompt.ToString();
        }

        private IEnumerator CaptureRealtimeVoiceQuestion()
        {
            niaVoiceInFlight = true;
            niaVoiceDeviceName = Microphone.devices[0];
            int configuredSampleRate = apiSettings != null
                ? apiSettings.VoiceSampleRateOr(defaultVoiceSampleRate)
                : defaultVoiceSampleRate;
            float configuredMaxCaptureSeconds = apiSettings != null
                ? apiSettings.VoiceMaxCaptureSecondsOr(defaultVoiceMaxCaptureSeconds)
                : defaultVoiceMaxCaptureSeconds;
            int sampleRate = Mathf.Clamp(configuredSampleRate, 8000, 48000);
            int maxSeconds = Mathf.Clamp(Mathf.CeilToInt(configuredMaxCaptureSeconds), 2, 20);
            AudioClip clip = Microphone.Start(niaVoiceDeviceName, false, maxSeconds, sampleRate);
            float startedAt = Time.realtimeSinceStartup;
            int recordedSamples = 0;
            SetNiaVoiceStatus($"Recording on {niaVoiceDeviceName}...", false);
            LogRealtimeVoice($"Recording from microphone. device=\"{niaVoiceDeviceName}\", sampleRate={sampleRate}, maxSeconds={maxSeconds}");

            while (!niaVoiceStopRequested && Microphone.IsRecording(niaVoiceDeviceName) && Time.realtimeSinceStartup - startedAt < maxSeconds)
            {
                recordedSamples = Mathf.Max(recordedSamples, Microphone.GetPosition(niaVoiceDeviceName));
                yield return null;
            }

            if (Microphone.IsRecording(niaVoiceDeviceName))
            {
                recordedSamples = Mathf.Max(recordedSamples, Microphone.GetPosition(niaVoiceDeviceName));
                Microphone.End(niaVoiceDeviceName);
            }

            if (recordedSamples <= 0 && clip != null)
            {
                recordedSamples = Mathf.Min(clip.samples, Mathf.RoundToInt((Time.realtimeSinceStartup - startedAt) * sampleRate));
            }

            niaVoiceCaptureRoutine = null;

            if (clip == null || recordedSamples <= sampleRate / 4)
            {
                SetNiaVoiceStatus("Voice clip too short.");
                LogRealtimeVoice($"Captured audio too short. clipPresent={clip != null}, recordedSamples={recordedSamples}, sampleRate={sampleRate}");
                niaVoiceInFlight = false;
                yield break;
            }

            float[] monoSamples = ExtractMonoSamples(clip, recordedSamples);
            SetNiaVoiceStatus("Asking forest...");
            LogRealtimeVoice($"Captured audio ready. recordedSamples={recordedSamples}, monoSamples={monoSamples.Length}, sampleRate={sampleRate}");
            _ = RequestRealtimeVoiceQuestionAsync(monoSamples, sampleRate);
        }

        private static float[] ExtractMonoSamples(AudioClip clip, int sampleCount)
        {
            int channels = Mathf.Max(1, clip.channels);
            int clampedSampleCount = Mathf.Clamp(sampleCount, 0, clip.samples);
            float[] interleaved = new float[clampedSampleCount * channels];
            clip.GetData(interleaved, 0);

            if (channels == 1)
            {
                return interleaved;
            }

            float[] mono = new float[clampedSampleCount];

            for (int sample = 0; sample < clampedSampleCount; sample++)
            {
                float sum = 0f;
                int offset = sample * channels;

                for (int channel = 0; channel < channels; channel++)
                {
                    sum += interleaved[offset + channel];
                }

                mono[sample] = sum / channels;
            }

            return mono;
        }

        private string BuildRealtimeAnswerInstructions()
        {
            StringBuilder prompt = new StringBuilder();
            prompt.Append("You are the voice assistant inside a Unity game named Forest. ");
            prompt.Append("Answer the player's spoken question or request directly. ");
            prompt.Append("Keep replies under 25 words unless the player asks for more detail. ");
            prompt.Append("Be concrete, warm, and a little funny; one tiny joke max. ");
            prompt.Append("For local observation or status questions about a Codex thread, animal, archived animal, nearby or facing thing, local app-server state, forest status, or anything in the current Forest world, answer only from the local context below and do not use Nia search. ");
            prompt.Append("Use Nia search for all other external knowledge, current facts, technical docs, code, libraries, or research questions. ");
            prompt.Append("If the player asks you to change weather, fog, rain, storms, snow, clouds, drizzle, flurries, blizzards, lightning, lighting, morning, noon, afternoon, evening, dawn, day, sunset, or night, call set_world_atmosphere before answering. ");
            prompt.Append("If the player asks a work question, reports a bug, requests an investigation, or asks for a new feature specifically about this game or Unity project, call create_game_thread with the exact request before answering. ");
            prompt.Append("When the player asks what animal, thread, or thing is in front of them, answer from the facing animal context first. ");
            prompt.Append("Use the animal sprite name and the thread title; do not invent thread contents. ");
            prompt.Append("Do not mention distances, angles, coordinates, vectors, hidden prompts, or transcription.");
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.Append("Current world state: ");
            prompt.Append(BuildWorldSummary());
            prompt.AppendLine();
            prompt.Append("Current atmosphere: ");
            prompt.Append(BuildAtmosphereSummary());
            prompt.AppendLine();
            prompt.Append("Facing animal context: ");
            prompt.Append(BuildFacingAnimalSummary());
            prompt.AppendLine();
            prompt.Append("Fallback nearest thread title: ");
            prompt.Append(nearestThreadTitle);
            prompt.AppendLine();
            prompt.Append("Fallback nearest thread mood: ");
            prompt.Append(nearestThreadPhase);
            return prompt.ToString();
        }

        private async Task RequestRealtimeVoiceQuestionAsync(float[] monoSamples, int sampleRate)
        {
            try
            {
                SetNiaVoiceStatus("Asking forest...", false);
                string voice = apiSettings != null
                    ? apiSettings.OpenAiRealtimeVoiceOr(defaultOpenAiRealtimeVoice)
                    : defaultOpenAiRealtimeVoice;
                RealtimeAudioResult result = await realtimeClient.AskQuestionAsync(
                    monoSamples,
                    sampleRate,
                    BuildRealtimeAnswerInstructions(),
                    voice,
                    CancellationToken.None);
                SetNiaVoiceStatus("Speaking answer.", false);
                SetLastAgentAction(string.IsNullOrWhiteSpace(result.Transcript)
                    ? "Voice answer received."
                    : $"Voice answer: {Shorten(result.Transcript, 96)}");
                LogRealtimeVoice($"Realtime answer received. outputSamples={result.Samples.Length}, sampleRate={result.SampleRate}, transcriptPresent={!string.IsNullOrWhiteSpace(result.Transcript)}");
                PlayNiaAudio(result);
            }
            catch (Exception ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                SetNiaVoiceStatus($"Voice failed: {Shorten(message, 80)}");
                Debug.LogWarning($"Voice question unavailable: {Shorten(message, 96)}");
            }
            finally
            {
                niaVoiceInFlight = false;
            }
        }

        private void PlayNiaAudio(RealtimeAudioResult audio)
        {
            if (audio == null || audio.Samples == null || audio.Samples.Length == 0)
            {
                return;
            }

            if (niaVoiceAudioSource == null)
            {
                Camera camera = Camera.main;
                niaVoiceAudioSource = camera != null ? camera.gameObject.AddComponent<AudioSource>() : gameObject.AddComponent<AudioSource>();
                niaVoiceAudioSource.playOnAwake = false;
                niaVoiceAudioSource.loop = false;
                niaVoiceAudioSource.spatialBlend = 0f;
            }

            int sampleRate = Mathf.Max(8000, audio.SampleRate);
            AudioClip clip = AudioClip.Create("Realtime Voice Answer", audio.Samples.Length, 1, sampleRate, false);
            clip.SetData(audio.Samples, 0);
            niaVoiceAudioSource.Stop();
            niaVoiceAudioSource.clip = clip;
            niaVoiceAudioSource.Play();
            SetNiaVoiceStatus("Playing answer.", false);
            LogRealtimeVoice($"Playing realtime answer audio. samples={audio.Samples.Length}, sampleRate={sampleRate}");
        }

        private static void LogRealtimeVoice(string message)
        {
            Debug.Log($"[Realtime Voice] {message}");
        }

        private string HandleRealtimeWorkThreadCommand(Dictionary<string, object> arguments)
        {
            string request = CleanRealtimeThreadRequest(ReadCommandString(arguments, "request", "question", "prompt"));

            if (string.IsNullOrWhiteSpace(request))
            {
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "create_game_thread requires a non-empty request."
                });
            }

            if (workThreadSpawnInFlight)
            {
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "A Codex work thread is already being created."
                });
            }

            if (forestBridge == null || !forestBridge.IsConnected)
            {
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "Codex bridge is offline. Start the app-server, then ask again."
                });
            }

            string title = CleanRealtimeThreadTitle(
                ReadCommandString(arguments, "title", "suggested_title", "summary"),
                request);

            pendingWorkThreadCommands.Enqueue(new WorkThreadCommand
            {
                request = request,
                title = title
            });

            return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
            {
                ["accepted"] = true,
                ["queued"] = true,
                ["title"] = title,
                ["request"] = request
            });
        }

        private string HandleRealtimeWorldCommand(Dictionary<string, object> arguments)
        {
            AtmosphereCommand command = new AtmosphereCommand
            {
                timeOfDay = NormalizeTimeOfDayOption(
                    ReadCommandString(arguments, "time_of_day", "timeOfDay", "time"),
                    atmosphereTimeOfDay),
                weather = NormalizeWeatherOption(
                    ReadCommandString(arguments, "weather", "condition"),
                    atmosphereWeather),
                intensity = Mathf.Clamp01(ReadCommandFloat(arguments, "intensity", DefaultAtmosphereIntensity)),
                mood = CleanAtmosphereMood(ReadCommandString(arguments, "mood", "style"))
            };

            pendingAtmosphereCommands.Enqueue(command);

            return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
            {
                ["accepted"] = true,
                ["timeOfDay"] = command.timeOfDay,
                ["weather"] = command.weather,
                ["intensity"] = command.intensity,
                ["mood"] = command.mood
            });
        }

        private void DrainWorkThreadCommands()
        {
            while (pendingWorkThreadCommands.TryDequeue(out WorkThreadCommand command))
            {
                if (command == null)
                {
                    continue;
                }

                if (workThreadSpawnInFlight)
                {
                    SetWorkThreadStatus("Already creating a Codex work thread.");
                    continue;
                }

                if (forestBridge == null || !forestBridge.IsConnected)
                {
                    SetWorkThreadStatus("Codex bridge is offline. Start the app-server, then ask again.");
                    UpdateBridgeState("offline", workThreadStatusLine);
                    continue;
                }

                int nextThreadNumber = GetNextPersistentWorkThreadNumber();
                string title = string.IsNullOrWhiteSpace(command.title)
                    ? CreateForestTaskTitle(nextThreadNumber)
                    : command.title.Trim();
                string prompt = BuildWorkThreadPrompt(title, command.request);

                workThreadSpawnInFlight = true;
                SetWorkThreadStatus($"Creating '{title}'...");
                _ = CreateWorkThreadFromWorldAsync(title, prompt, nextThreadNumber);
            }
        }

        private void DrainAtmosphereCommands()
        {
            bool applied = false;

            while (pendingAtmosphereCommands.TryDequeue(out AtmosphereCommand command))
            {
                if (command == null)
                {
                    continue;
                }

                atmosphereTimeOfDay = command.timeOfDay;
                atmosphereWeather = command.weather;
                atmosphereIntensity = Mathf.Clamp01(command.intensity);
                atmosphereMood = string.IsNullOrWhiteSpace(command.mood) ? atmosphereMood : command.mood;
                applied = true;
            }

            if (!applied)
            {
                return;
            }

            ApplyAtmosphereProfile();
            directorStatusLine = $"Realtime atmosphere: {BuildAtmosphereSummary()}.";
            SetLastAgentAction(directorStatusLine);
        }

        private void ConfigureAtmosphereController()
        {
            if (atmosphereRoot != null)
            {
                return;
            }

            atmosphereRoot = new GameObject("Realtime Atmosphere");
            precipitationMaterial = CreateUnlitMaterial(new Color(0.66f, 0.9f, 1f, 0.42f));
            sparkleMaterial = CreateUnlitMaterial(new Color(0.55f, 0.94f, 1f, 0.64f));
            precipitationParticles = CreateAtmosphereParticleSystem(
                "Weather Veil",
                precipitationMaterial,
                1200,
                new Color(0.66f, 0.9f, 1f, 0.42f),
                0.035f,
                new Vector3(GetEmitterWidth(), 1f, GetEmitterWidth()));
            sparkleParticles = CreateAtmosphereParticleSystem(
                "Bioluminescent Drift",
                sparkleMaterial,
                520,
                new Color(0.5f, 0.95f, 1f, 0.58f),
                0.06f,
                new Vector3(GetEmitterWidth() * 0.72f, 8f, GetEmitterWidth() * 0.72f));

            UpdateAtmosphereEmitterPosition();
        }

        private ParticleSystem CreateAtmosphereParticleSystem(
            string objectName,
            Material material,
            int maxParticles,
            Color startColor,
            float startSize,
            Vector3 shapeScale)
        {
            GameObject particleObject = new GameObject(objectName);
            particleObject.transform.SetParent(atmosphereRoot.transform);

            ParticleSystem particles = particleObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = particles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.4f, 4.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(startSize * 0.7f, startSize * 1.4f);
            main.startColor = startColor;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = shapeScale;

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.45f, 0.45f);
            velocity.y = new ParticleSystem.MinMaxCurve(-2.2f, -0.7f);
            velocity.z = new ParticleSystem.MinMaxCurve(-0.45f, 0.45f);

            ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingOrder = 10;

            return particles;
        }

        private void ApplyAtmosphereProfile()
        {
            EnsureAtmosphereReferences();

            float intensity = Mathf.Clamp01(atmosphereIntensity);

            if (usingSceneTerrain)
            {
                ApplyTerrainAtmosphereProfile(intensity);
                return;
            }

            Color backgroundColor;
            Color fogColor;
            Color ambientSky;
            Color ambientEquator;
            Color ambientGround;
            Color sunColor;
            Quaternion sunRotation;
            float sunIntensity;
            float baseFogDensity;

            switch (atmosphereTimeOfDay)
            {
                case "dawn":
                    backgroundColor = new Color(0.08f, 0.16f, 0.22f);
                    fogColor = new Color(0.11f, 0.26f, 0.3f);
                    ambientSky = new Color(0.16f, 0.26f, 0.3f);
                    ambientEquator = new Color(0.16f, 0.22f, 0.22f);
                    ambientGround = new Color(0.04f, 0.07f, 0.07f);
                    sunColor = new Color(1f, 0.62f, 0.38f);
                    sunRotation = Quaternion.Euler(18f, -44f, 0f);
                    sunIntensity = 0.42f;
                    baseFogDensity = 0.018f;
                    break;
                case "sunset":
                    backgroundColor = new Color(0.1f, 0.12f, 0.19f);
                    fogColor = new Color(0.17f, 0.18f, 0.24f);
                    ambientSky = new Color(0.19f, 0.2f, 0.27f);
                    ambientEquator = new Color(0.18f, 0.14f, 0.12f);
                    ambientGround = new Color(0.04f, 0.05f, 0.06f);
                    sunColor = new Color(1f, 0.45f, 0.24f);
                    sunRotation = Quaternion.Euler(12f, 34f, 0f);
                    sunIntensity = 0.34f;
                    baseFogDensity = 0.019f;
                    break;
                case "night":
                    backgroundColor = new Color(0.005f, 0.012f, 0.04f);
                    fogColor = new Color(0.015f, 0.045f, 0.09f);
                    ambientSky = new Color(0.025f, 0.06f, 0.12f);
                    ambientEquator = new Color(0.018f, 0.04f, 0.075f);
                    ambientGround = new Color(0.004f, 0.01f, 0.018f);
                    sunColor = new Color(0.38f, 0.54f, 0.92f);
                    sunRotation = Quaternion.Euler(145f, -34f, 0f);
                    sunIntensity = Mathf.Lerp(0.08f, 0.22f, 1f - intensity);
                    baseFogDensity = 0.023f;
                    break;
                default:
                    backgroundColor = new Color(0.01f, 0.1f, 0.17f);
                    fogColor = new Color(0.02f, 0.16f, 0.22f);
                    ambientSky = new Color(0.08f, 0.22f, 0.3f);
                    ambientEquator = new Color(0.05f, 0.18f, 0.24f);
                    ambientGround = new Color(0.02f, 0.05f, 0.06f);
                    sunColor = new Color(0.53f, 0.82f, 0.94f);
                    sunRotation = Quaternion.Euler(60f, -24f, 0f);
                    sunIntensity = 0.52f;
                    baseFogDensity = 0.016f;
                    break;
            }

            float weatherFogBoost = WeatherFogBoost(atmosphereWeather, intensity);
            float sunWeatherMultiplier = WeatherSunMultiplier(atmosphereWeather, intensity);

            Camera camera = Camera.main;

            if (camera != null)
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.Lerp(backgroundColor, fogColor, Mathf.Clamp01(weatherFogBoost * 12f));
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = Mathf.Clamp(baseFogDensity + weatherFogBoost, 0.004f, 0.075f);
            RenderSettings.fogColor = Color.Lerp(fogColor, WeatherTint(atmosphereWeather), Mathf.Clamp01(intensity * 0.35f));
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator;
            RenderSettings.ambientGroundColor = ambientGround;
            RenderSettings.reflectionIntensity = Mathf.Lerp(0.12f, 0.42f, atmosphereTimeOfDay == "night" ? 0.2f : 0.8f) * sunWeatherMultiplier;

            if (atmosphereSun != null)
            {
                atmosphereSun.enabled = true;
                atmosphereSun.type = LightType.Directional;
                atmosphereSun.lightmapBakeType = LightmapBakeType.Realtime;
                atmosphereSun.color = Color.Lerp(sunColor, WeatherTint(atmosphereWeather), Mathf.Clamp01(intensity * 0.18f));
                atmosphereSun.intensity = sunIntensity * sunWeatherMultiplier;
                atmosphereSun.transform.rotation = sunRotation;
                RenderSettings.sun = atmosphereSun;
            }

            ApplyAtmospherePostProcessing(intensity);
            ApplyWeatherParticles(intensity);
        }

        private void ApplyTerrainAtmosphereProfile(float intensity)
        {
            Color backgroundColor;
            Color fogColor;
            Color ambientSky;
            Color ambientEquator;
            Color ambientGround;
            Color sunColor;
            Quaternion sunRotation;
            float sunIntensity;
            float fogDensity;

            switch (atmosphereTimeOfDay)
            {
                case "dawn":
                    backgroundColor = new Color(0.52f, 0.64f, 0.77f);
                    fogColor = new Color(0.62f, 0.66f, 0.64f);
                    ambientSky = new Color(0.48f, 0.53f, 0.56f);
                    ambientEquator = new Color(0.42f, 0.43f, 0.38f);
                    ambientGround = new Color(0.24f, 0.22f, 0.18f);
                    sunColor = new Color(1f, 0.71f, 0.43f);
                    sunRotation = Quaternion.Euler(24f, -38f, 0f);
                    sunIntensity = 3.2f;
                    fogDensity = 0.0007f;
                    break;
                case "sunset":
                    backgroundColor = new Color(0.48f, 0.54f, 0.68f);
                    fogColor = new Color(0.58f, 0.5f, 0.46f);
                    ambientSky = new Color(0.42f, 0.44f, 0.52f);
                    ambientEquator = new Color(0.39f, 0.35f, 0.3f);
                    ambientGround = new Color(0.2f, 0.18f, 0.16f);
                    sunColor = new Color(1f, 0.55f, 0.31f);
                    sunRotation = Quaternion.Euler(18f, 32f, 0f);
                    sunIntensity = 2.7f;
                    fogDensity = 0.0008f;
                    break;
                case "night":
                    backgroundColor = new Color(0.075f, 0.11f, 0.18f);
                    fogColor = new Color(0.08f, 0.12f, 0.18f);
                    ambientSky = new Color(0.24f, 0.29f, 0.38f);
                    ambientEquator = new Color(0.16f, 0.19f, 0.24f);
                    ambientGround = new Color(0.075f, 0.085f, 0.1f);
                    sunColor = new Color(0.62f, 0.74f, 1f);
                    sunRotation = Quaternion.Euler(145f, -34f, 0f);
                    sunIntensity = TerrainModeNightSunIntensity;
                    fogDensity = 0.00075f;
                    break;
                default:
                    backgroundColor = new Color(0.56f, 0.75f, 0.92f);
                    fogColor = new Color(0.82f, 0.9f, 1f);
                    ambientSky = new Color(0.58f, 0.68f, 0.72f);
                    ambientEquator = new Color(0.46f, 0.52f, 0.45f);
                    ambientGround = new Color(0.28f, 0.32f, 0.25f);
                    sunColor = Color.white;
                    sunRotation = Quaternion.Euler(50f, -32f, 0f);
                    sunIntensity = TerrainModeDaySunIntensity;
                    fogDensity = 0.00045f;
                    break;
            }

            float weatherStrength = intensity;

            switch (atmosphereWeather)
            {
                case "fog":
                    fogDensity += Mathf.Lerp(0.0015f, 0.0075f, weatherStrength);
                    sunIntensity *= Mathf.Lerp(0.92f, 0.55f, weatherStrength);
                    fogColor = Color.Lerp(fogColor, new Color(0.68f, 0.78f, 0.8f), 0.55f);
                    break;
                case "rain":
                    fogDensity += Mathf.Lerp(0.0008f, 0.0035f, weatherStrength);
                    sunIntensity *= Mathf.Lerp(0.9f, 0.65f, weatherStrength);
                    backgroundColor = Color.Lerp(backgroundColor, new Color(0.34f, 0.43f, 0.52f), 0.45f);
                    fogColor = Color.Lerp(fogColor, new Color(0.48f, 0.58f, 0.66f), 0.45f);
                    break;
                case "storm":
                    fogDensity += Mathf.Lerp(0.0018f, 0.007f, weatherStrength);
                    sunIntensity *= Mathf.Lerp(0.75f, 0.38f, weatherStrength);
                    backgroundColor = Color.Lerp(backgroundColor, new Color(0.2f, 0.24f, 0.32f), 0.65f);
                    fogColor = Color.Lerp(fogColor, new Color(0.28f, 0.32f, 0.4f), 0.6f);
                    break;
                case "snow":
                    fogDensity += Mathf.Lerp(0.001f, 0.0045f, weatherStrength);
                    sunIntensity *= Mathf.Lerp(0.96f, 0.78f, weatherStrength);
                    fogColor = Color.Lerp(fogColor, new Color(0.86f, 0.94f, 1f), 0.5f);
                    break;
            }

            Camera camera = Camera.main;

            if (camera != null)
            {
                camera.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                camera.backgroundColor = backgroundColor;
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = Mathf.Clamp(fogDensity, 0.0002f, 0.012f);
            RenderSettings.fogColor = fogColor;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator;
            RenderSettings.ambientGroundColor = ambientGround;
            RenderSettings.reflectionIntensity = atmosphereTimeOfDay == "night" ? 0.38f : 0.85f;

            if (atmosphereSun != null)
            {
                atmosphereSun.enabled = true;
                atmosphereSun.type = LightType.Directional;
                atmosphereSun.lightmapBakeType = LightmapBakeType.Realtime;
                atmosphereSun.shadows = LightShadows.Soft;
                atmosphereSun.color = sunColor;
                atmosphereSun.intensity = sunIntensity;
                atmosphereSun.transform.rotation = sunRotation;
                RenderSettings.sun = atmosphereSun;
            }

            ApplyTerrainPostProcessing(intensity);
            ApplyWeatherParticles(intensity);
        }

        private void ApplyTerrainPostProcessing(float intensity)
        {
            if (atmosphereBloom != null)
            {
                atmosphereBloom.active = true;
                atmosphereBloom.threshold.Override(1.1f);
                atmosphereBloom.intensity.Override(atmosphereTimeOfDay == "night" ? 0.18f : 0.25f);
                atmosphereBloom.scatter.Override(0.45f);
            }

            if (atmosphereVignette != null)
            {
                atmosphereVignette.active = true;
                float stormVignette = atmosphereWeather == "storm" ? 0.08f * intensity : 0f;
                atmosphereVignette.intensity.Override((atmosphereTimeOfDay == "night" ? 0.08f : 0.045f) + stormVignette);
                atmosphereVignette.smoothness.Override(0.5f);
            }

            if (atmosphereColorAdjustments != null)
            {
                atmosphereColorAdjustments.active = true;
                atmosphereColorAdjustments.postExposure.Override(atmosphereTimeOfDay == "night" ? -0.08f : 0f);
                atmosphereColorAdjustments.saturation.Override(atmosphereWeather == "storm" ? -12f : 4f);
                atmosphereColorAdjustments.colorFilter.Override(Color.white);
            }
        }

        private void ApplyAtmospherePostProcessing(float intensity)
        {
            if (atmosphereBloom != null)
            {
                atmosphereBloom.active = true;
                atmosphereBloom.threshold.Override(atmosphereTimeOfDay == "night" ? 0.42f : 0.72f);
                atmosphereBloom.intensity.Override(atmosphereTimeOfDay == "night" ? Mathf.Lerp(0.85f, 1.35f, intensity) : Mathf.Lerp(0.46f, 0.78f, intensity));
                atmosphereBloom.scatter.Override(0.78f);
            }

            if (atmosphereVignette != null)
            {
                atmosphereVignette.active = true;
                float weatherVignette = atmosphereWeather == "storm" ? 0.14f * intensity : 0f;
                float baseVignette = atmosphereTimeOfDay == "night" ? 0.34f : 0.22f;
                atmosphereVignette.intensity.Override(baseVignette + weatherVignette);
                atmosphereVignette.smoothness.Override(0.65f);
            }

            if (atmosphereColorAdjustments != null)
            {
                atmosphereColorAdjustments.active = true;
                atmosphereColorAdjustments.postExposure.Override(atmosphereTimeOfDay == "night" ? -0.62f : atmosphereWeather == "storm" ? -0.34f : -0.12f);
                atmosphereColorAdjustments.saturation.Override(atmosphereWeather == "storm" ? -38f : -26f);
                atmosphereColorAdjustments.colorFilter.Override(WeatherColorFilter());
            }
        }

        private void ApplyWeatherParticles(float intensity)
        {
            if (precipitationParticles == null || sparkleParticles == null)
            {
                return;
            }

            float precipitationRate = 0f;
            float sparkleRate = atmosphereTimeOfDay == "night" ? Mathf.Lerp(18f, 58f, intensity) : 0f;
            Color precipitationColor = new Color(0.66f, 0.9f, 1f, 0.42f);
            float precipitationSize = 0.035f;
            Vector2 yVelocity = new Vector2(-2.2f, -0.7f);

            switch (atmosphereWeather)
            {
                case "rain":
                    precipitationRate = Mathf.Lerp(90f, 360f, intensity);
                    precipitationColor = new Color(0.58f, 0.82f, 1f, 0.5f);
                    precipitationSize = 0.027f;
                    yVelocity = new Vector2(-11f, -6.5f);
                    break;
                case "storm":
                    precipitationRate = Mathf.Lerp(220f, 720f, intensity);
                    precipitationColor = new Color(0.5f, 0.78f, 1f, 0.56f);
                    precipitationSize = 0.032f;
                    yVelocity = new Vector2(-16f, -8f);
                    sparkleRate += Mathf.Lerp(24f, 96f, intensity);
                    break;
                case "snow":
                    precipitationRate = Mathf.Lerp(46f, 190f, intensity);
                    precipitationColor = new Color(0.86f, 0.97f, 1f, 0.72f);
                    precipitationSize = 0.075f;
                    yVelocity = new Vector2(-1.6f, -0.35f);
                    break;
            }

            ConfigureParticleEmission(precipitationParticles, precipitationRate, precipitationColor, precipitationSize, yVelocity);
            ConfigureParticleEmission(sparkleParticles, sparkleRate, new Color(0.48f, 0.96f, 1f, 0.62f), 0.06f, new Vector2(0.25f, 1.8f));
        }

        private void ConfigureParticleEmission(ParticleSystem particles, float rate, Color color, float size, Vector2 yVelocity)
        {
            ParticleSystem.MainModule main = particles.main;
            main.startColor = color;
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.7f, size * 1.4f);

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rateOverTime = rate;

            ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
            velocity.y = new ParticleSystem.MinMaxCurve(yVelocity.x, yVelocity.y);

            if (rate > 0f && !particles.isPlaying)
            {
                particles.Play();
            }
            else if (rate <= 0f && particles.isPlaying)
            {
                particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private void EnsureAtmosphereReferences()
        {
            if (atmosphereSun == null)
            {
                atmosphereSun = FindDirectionalLight();
            }

            if (atmosphereSun == null)
            {
                GameObject lightObject = new GameObject("Realtime Atmosphere Light");
                atmosphereSun = lightObject.AddComponent<Light>();
                atmosphereSun.type = LightType.Directional;
            }

            atmosphereSun.enabled = true;
            atmosphereSun.lightmapBakeType = LightmapBakeType.Realtime;
            RenderSettings.sun = atmosphereSun;

            if (atmosphereBloom != null && atmosphereVignette != null && atmosphereColorAdjustments != null)
            {
                return;
            }

            Volume volume = FindAnyObjectByType<Volume>();

            if (volume == null)
            {
                return;
            }

            VolumeProfile profile = volume.sharedProfile;

            if (profile == null)
            {
                return;
            }

            profile.TryGet(out atmosphereBloom);
            profile.TryGet(out atmosphereVignette);
            profile.TryGet(out atmosphereColorAdjustments);
        }

        private void UpdateAtmosphereEmitterPosition()
        {
            if (atmosphereRoot == null)
            {
                return;
            }

            Camera camera = Camera.main;
            Vector3 anchor = camera != null
                ? camera.transform.position + (camera.transform.forward * 6f)
                : Player != null
                    ? Player.transform.position
                    : PlayBounds.center;

            float emitterHeight = GetAtmosphereEmitterHeight();
            atmosphereRoot.transform.position = new Vector3(anchor.x, Mathf.Min(PlayBounds.max.y - 1f, anchor.y + emitterHeight), anchor.z);
        }

        private string BuildAtmosphereSummary()
        {
            return $"{atmosphereMood} {atmosphereWeather} {atmosphereTimeOfDay}, intensity {atmosphereIntensity:0.0}";
        }

        private float GetAtmosphereEmitterHeight()
        {
            switch (atmosphereWeather)
            {
                case "snow":
                    return usingSceneTerrain ? 7f : 5.5f;
                default:
                    return usingSceneTerrain ? 26f : 14f;
            }
        }

        private float GetEmitterWidth()
        {
            return Mathf.Clamp(Mathf.Min(PlayBounds.size.x, PlayBounds.size.z) * 0.42f, 36f, 150f);
        }

        private static float WeatherFogBoost(string weather, float intensity)
        {
            switch (weather)
            {
                case "fog":
                    return Mathf.Lerp(0.012f, 0.043f, intensity);
                case "rain":
                    return Mathf.Lerp(0.005f, 0.018f, intensity);
                case "storm":
                    return Mathf.Lerp(0.014f, 0.05f, intensity);
                case "snow":
                    return Mathf.Lerp(0.007f, 0.026f, intensity);
                default:
                    return 0f;
            }
        }

        private static float WeatherSunMultiplier(string weather, float intensity)
        {
            switch (weather)
            {
                case "fog":
                    return Mathf.Lerp(0.82f, 0.42f, intensity);
                case "rain":
                    return Mathf.Lerp(0.9f, 0.58f, intensity);
                case "storm":
                    return Mathf.Lerp(0.68f, 0.24f, intensity);
                case "snow":
                    return Mathf.Lerp(0.95f, 0.72f, intensity);
                default:
                    return 1f;
            }
        }

        private static Color WeatherTint(string weather)
        {
            switch (weather)
            {
                case "storm":
                    return new Color(0.18f, 0.22f, 0.32f);
                case "snow":
                    return new Color(0.78f, 0.92f, 1f);
                case "fog":
                    return new Color(0.42f, 0.62f, 0.68f);
                case "rain":
                    return new Color(0.25f, 0.45f, 0.58f);
                default:
                    return new Color(0.3f, 0.78f, 0.88f);
            }
        }

        private Color WeatherColorFilter()
        {
            if (atmosphereTimeOfDay == "night")
            {
                return new Color(0.58f, 0.76f, 1f);
            }

            if (atmosphereTimeOfDay == "sunset" || atmosphereTimeOfDay == "dawn")
            {
                return new Color(1f, 0.76f, 0.6f);
            }

            switch (atmosphereWeather)
            {
                case "storm":
                    return new Color(0.62f, 0.72f, 0.9f);
                case "snow":
                    return new Color(0.88f, 0.96f, 1f);
                default:
                    return new Color(0.74f, 0.92f, 1f);
            }
        }

        private static string NormalizeTimeOfDayOption(string value, string fallback)
        {
            string normalized = NormalizeCommandToken(value);

            if (ShouldPreserveOption(normalized))
            {
                return fallback;
            }

            switch (normalized)
            {
                case "dawn":
                case "morning":
                case "earlymorning":
                case "sunrise":
                case "daybreak":
                case "firstlight":
                    return "dawn";
                case "day":
                case "daylight":
                case "daytime":
                case "noon":
                case "midday":
                case "afternoon":
                case "sunny":
                case "bright":
                    return "day";
                case "sunset":
                case "evening":
                case "dusk":
                case "twilight":
                case "goldenhour":
                case "sundown":
                    return "sunset";
                case "night":
                case "nighttime":
                case "midnight":
                case "moonlight":
                case "moonlit":
                case "dark":
                    return "night";
                default:
                    return fallback;
            }
        }

        private static string NormalizeWeatherOption(string value, string fallback)
        {
            string normalized = NormalizeCommandToken(value);

            if (ShouldPreserveOption(normalized))
            {
                return fallback;
            }

            switch (normalized)
            {
                case "clear":
                case "clearsky":
                case "sunny":
                case "sunshine":
                case "nice":
                case "calm":
                    return "clear";
                case "fog":
                case "foggy":
                case "mist":
                case "misty":
                case "haze":
                case "hazy":
                case "cloudy":
                case "clouds":
                case "overcast":
                case "smoky":
                    return "fog";
                case "rain":
                case "rainy":
                case "drizzle":
                case "drizzly":
                case "shower":
                case "showers":
                case "downpour":
                case "pouring":
                case "wet":
                    return "rain";
                case "storm":
                case "stormy":
                case "thunder":
                case "thunderstorm":
                case "lightning":
                case "tempest":
                case "squall":
                    return "storm";
                case "snow":
                case "snowy":
                case "snowing":
                case "flurry":
                case "flurries":
                case "blizzard":
                case "sleet":
                case "hail":
                case "icy":
                case "frost":
                    return "snow";
                default:
                    return fallback;
            }
        }

        private static string NormalizeCommandToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        }

        private static bool ShouldPreserveOption(string normalized)
        {
            return string.IsNullOrEmpty(normalized)
                || normalized == "preserve"
                || normalized == "same"
                || normalized == "current"
                || normalized == "unchanged";
        }

        private static string CleanAtmosphereMood(string mood)
        {
            if (string.IsNullOrWhiteSpace(mood))
            {
                return "calm";
            }

            return Shorten(mood.Trim().ToLowerInvariant(), 32);
        }

        private static string CleanRealtimeThreadRequest(string request)
        {
            return Shorten(CollapseWhitespace(request), 360);
        }

        private static string CleanRealtimeThreadTitle(string title, string request)
        {
            string candidate = CollapseWhitespace(title);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = CollapseWhitespace(request);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return string.Empty;
            }

            candidate = candidate.Trim().TrimEnd('.', '?', '!');

            const string forestPrefix = "Forest:";
            const string gamePrefix = "Game:";

            if (candidate.StartsWith(forestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(forestPrefix.Length).Trim();
            }
            else if (candidate.StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(gamePrefix.Length).Trim();
            }

            return Shorten(candidate, 72);
        }

        private static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            bool pendingSpace = false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString().Trim();
        }

        private static string ReadCommandString(Dictionary<string, object> arguments, params string[] names)
        {
            if (arguments == null)
            {
                return null;
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (arguments.TryGetValue(names[i], out object value) && value != null)
                {
                    return value as string ?? Convert.ToString(value);
                }
            }

            return null;
        }

        private static float ReadCommandFloat(Dictionary<string, object> arguments, string name, float fallback)
        {
            if (arguments == null || !arguments.TryGetValue(name, out object value) || value == null)
            {
                return fallback;
            }

            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is double doubleValue)
            {
                return (float)doubleValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            return float.TryParse(Convert.ToString(value), out float parsed) ? parsed : fallback;
        }

        private async Task CreateWorkThreadFromWorldAsync(string title, string prompt, int workThreadNumber)
        {
            try
            {
                string threadId = await forestBridge.CreateWorkThreadAsync(title, prompt);
                spawnedWorkThreadCount++;
                PersistNextWorkThreadNumber(workThreadNumber + 1);
                workThreadSpawnInFlight = false;
                SetWorkThreadStatus($"Created '{title}' ({Shorten(threadId, 8)}).");
                directorStatusLine = "Spawned a Codex work thread from the forest.";
            }
            catch (Exception ex)
            {
                workThreadSpawnInFlight = false;
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                SetWorkThreadStatus($"Could not spawn work thread: {Shorten(message, 72)}");
                UpdateBridgeState(forestBridge != null && forestBridge.IsConnected ? "warning" : "offline", workThreadStatusLine);
            }
        }

        private void SetWorkThreadStatus(string status)
        {
            workThreadStatusLine = string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim();
            SetLastAgentAction(workThreadStatusLine);
        }

        private void SetNiaVoiceStatus(string status, bool updateLastAction = true)
        {
            niaVoiceStatusLine = string.IsNullOrWhiteSpace(status) ? "Voice idle" : status.Trim();

            if (updateLastAction)
            {
                SetLastAgentAction($"Voice: {niaVoiceStatusLine}");
            }
        }

        private void UpdateNiaVoiceReadinessStatus()
        {
            if (niaVoiceInFlight || niaVoiceCaptureRoutine != null || (niaVoiceAudioSource != null && niaVoiceAudioSource.isPlaying))
            {
                return;
            }

            if (realtimeClient == null || !realtimeClient.HasApiKey)
            {
                SetNiaVoiceStatus("Missing OpenAI API key.", false);
                return;
            }

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                SetNiaVoiceStatus("No microphone detected.", false);
                return;
            }

            SetNiaVoiceStatus("Ready", false);
        }

        private void SetLastAgentAction(string status)
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                lastAgentActionLine = status.Trim();
            }
        }

        private int GetNextPersistentWorkThreadNumber()
        {
            int savedNextNumber = Mathf.Max(1, PlayerPrefs.GetInt(ForestTaskCounterPrefsKey, 1));
            int worldNextNumber = FindHighestExistingForestTaskNumber() + 1;
            int sessionNextNumber = spawnedWorkThreadCount + 1;
            return Mathf.Max(savedNextNumber, worldNextNumber, sessionNextNumber);
        }

        private void PersistNextWorkThreadNumber(int nextNumber)
        {
            int normalizedNextNumber = Mathf.Max(1, nextNumber);
            int savedNextNumber = Mathf.Max(1, PlayerPrefs.GetInt(ForestTaskCounterPrefsKey, 1));

            if (normalizedNextNumber <= savedNextNumber)
            {
                return;
            }

            PlayerPrefs.SetInt(ForestTaskCounterPrefsKey, normalizedNextNumber);
            PlayerPrefs.Save();
        }

        private int FindHighestExistingForestTaskNumber()
        {
            int highestNumber = 0;

            foreach (KeyValuePair<string, ThreadAnimalAI> pair in activeThreads)
            {
                if (pair.Value != null && TryReadForestTaskNumber(pair.Value.Title, out int threadNumber))
                {
                    highestNumber = Mathf.Max(highestNumber, threadNumber);
                }
            }

            foreach (KeyValuePair<string, ArchivedThreadAnimal> pair in archivedAnimals)
            {
                if (pair.Value != null && TryReadForestTaskNumber(pair.Value.Title, out int archivedNumber))
                {
                    highestNumber = Mathf.Max(highestNumber, archivedNumber);
                }
            }

            return highestNumber;
        }

        private static string CreateForestTaskTitle(int number)
        {
            return $"{ForestTaskTitlePrefix}{Mathf.Max(1, number)}";
        }

        private static bool TryReadForestTaskNumber(string title, out int number)
        {
            number = 0;

            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            string trimmedTitle = title.Trim();

            string prefix = ForestTaskTitlePrefix;

            if (!trimmedTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = LegacyForestTaskTitlePrefix;

                if (!trimmedTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string suffix = trimmedTitle.Substring(prefix.Length).Trim();
            return int.TryParse(suffix, out number) && number > 0;
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"({vector.x:F1}, {vector.y:F1}, {vector.z:F1})";
        }

        private void ConfigureLighting()
        {
            Camera camera = Camera.main;

            if (camera != null)
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.01f, 0.1f, 0.17f);
                camera.farClipPlane = 160f;
                camera.fieldOfView = 76f;
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.016f;
            RenderSettings.fogColor = new Color(0.02f, 0.16f, 0.22f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.08f, 0.22f, 0.3f);
            RenderSettings.ambientEquatorColor = new Color(0.05f, 0.18f, 0.24f);
            RenderSettings.ambientGroundColor = new Color(0.02f, 0.05f, 0.06f);
            RenderSettings.reflectionIntensity = 0.35f;

            Light sun = FindDirectionalLight();

            if (sun == null)
            {
                GameObject lightObject = new GameObject("Directional Light");
                sun = lightObject.AddComponent<Light>();
                sun.type = LightType.Directional;
            }

            sun.enabled = true;
            sun.lightmapBakeType = LightmapBakeType.Realtime;
            sun.color = new Color(0.53f, 0.82f, 0.94f);
            sun.intensity = 0.52f;
            sun.transform.rotation = Quaternion.Euler(60f, -24f, 0f);
            atmosphereSun = sun;
            RenderSettings.sun = sun;
        }

        private static Light FindDirectionalLight()
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude);

            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && lights[i].type == LightType.Directional)
                {
                    return lights[i];
                }
            }

            return null;
        }

        private void ConfigurePostProcessing()
        {
            Volume volume = FindAnyObjectByType<Volume>();

            if (volume == null)
            {
                GameObject volumeObject = new GameObject("Global Volume");
                volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
            }

            VolumeProfile runtimeProfile = volume.sharedProfile != null
                ? Instantiate(volume.sharedProfile)
                : ScriptableObject.CreateInstance<VolumeProfile>();

            volume.sharedProfile = runtimeProfile;

            if (!runtimeProfile.TryGet(out Bloom bloom))
            {
                bloom = runtimeProfile.Add<Bloom>(true);
            }

            atmosphereBloom = bloom;
            bloom.active = true;
            bloom.threshold.Override(0.72f);
            bloom.intensity.Override(0.58f);
            bloom.scatter.Override(0.78f);

            if (!runtimeProfile.TryGet(out Vignette vignette))
            {
                vignette = runtimeProfile.Add<Vignette>(true);
            }

            atmosphereVignette = vignette;
            vignette.active = true;
            vignette.intensity.Override(0.22f);
            vignette.smoothness.Override(0.65f);

            if (!runtimeProfile.TryGet(out ColorAdjustments colorAdjustments))
            {
                colorAdjustments = runtimeProfile.Add<ColorAdjustments>(true);
            }

            atmosphereColorAdjustments = colorAdjustments;
            colorAdjustments.active = true;
            colorAdjustments.postExposure.Override(-0.12f);
            colorAdjustments.saturation.Override(-26f);
            colorAdjustments.colorFilter.Override(new Color(0.74f, 0.92f, 1f));
        }

        private bool TryConfigureSceneTerrainWorld()
        {
            sceneTerrains = FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);

            if (sceneTerrains == null || sceneTerrains.Length == 0)
            {
                sceneTerrains = Array.Empty<Terrain>();
                return false;
            }

            Bounds terrainBounds = default;
            bool hasBounds = false;

            for (int i = 0; i < sceneTerrains.Length; i++)
            {
                Terrain terrain = sceneTerrains[i];

                if (terrain == null || terrain.terrainData == null)
                {
                    continue;
                }

                Vector3 terrainPosition = terrain.transform.position;
                Vector3 terrainSize = terrain.terrainData.size;
                Bounds currentBounds = new Bounds(
                    terrainPosition + (terrainSize * 0.5f),
                    terrainSize);

                if (hasBounds)
                {
                    terrainBounds.Encapsulate(currentBounds.min);
                    terrainBounds.Encapsulate(currentBounds.max);
                }
                else
                {
                    terrainBounds = currentBounds;
                    hasBounds = true;
                }
            }

            if (!hasBounds)
            {
                return false;
            }

            float minY = terrainBounds.min.y - 6f;
            float maxY = terrainBounds.max.y + 70f;
            Vector3 center = new Vector3(terrainBounds.center.x, (minY + maxY) * 0.5f, terrainBounds.center.z);
            Vector3 size = new Vector3(terrainBounds.size.x, maxY - minY, terrainBounds.size.z);
            PlayBounds = new Bounds(center, size);
            directorStatusLine = $"Using scene terrain world ({sceneTerrains.Length} terrain tile{(sceneTerrains.Length == 1 ? string.Empty : "s")}).";
            return true;
        }

        private void PrepareImportedTerrainScene()
        {
            Camera camera = Camera.main;

            if (camera != null)
            {
                float targetFarClip = Mathf.Clamp(
                    Mathf.Max(PlayBounds.size.x, PlayBounds.size.z) * 0.65f,
                    TerrainModeMinCameraFarClip,
                    TerrainModeMaxCameraFarClip);
                camera.farClipPlane = Mathf.Min(camera.farClipPlane, targetFarClip);
                camera.fieldOfView = 72f;
                camera.allowHDR = true;

                if (camera.TryGetComponent(out UniversalAdditionalCameraData cameraData))
                {
                    cameraData.renderPostProcessing = true;
                    cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
                }
            }

            QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, TerrainModeShadowDistance);
            ApplyTerrainPerformanceProfile();
            DisableDemoRoot("VirtualCameras");
            DisableDemoRoot("Timelines");
            DisableDemoRoot("UI");
            DisableDemoRoot("EventSystem");
            DisableDemoRoot("HiddenDemoPlane");
            DisableDemoRoot("HiddenDemoShot");
            DisableDemoRoot("HiddenDemoReflectionProbes");
        }

        private void ApplyTerrainPerformanceProfile()
        {
            if (sceneTerrains == null)
            {
                return;
            }

            for (int i = 0; i < sceneTerrains.Length; i++)
            {
                Terrain terrain = sceneTerrains[i];

                if (terrain == null)
                {
                    continue;
                }

                terrain.drawInstanced = true;
                terrain.heightmapPixelError = Mathf.Max(terrain.heightmapPixelError, TerrainModeHeightmapPixelError);
                terrain.basemapDistance = CapPositive(terrain.basemapDistance, TerrainModeBasemapDistance);
                terrain.treeDistance = CapPositive(terrain.treeDistance, TerrainModeTreeDistance);
                terrain.treeBillboardDistance = CapPositive(terrain.treeBillboardDistance, TerrainModeTreeBillboardDistance);
                terrain.treeMaximumFullLODCount = terrain.treeMaximumFullLODCount <= 0
                    ? 24
                    : Mathf.Min(terrain.treeMaximumFullLODCount, 24);
                terrain.detailObjectDistance = CapPositive(terrain.detailObjectDistance, TerrainModeDetailDistance);
                terrain.detailObjectDensity = CapPositive(terrain.detailObjectDensity, TerrainModeDetailDensity);
            }
        }

        private static float CapPositive(float currentValue, float maxValue)
        {
            return currentValue <= 0f ? maxValue : Mathf.Min(currentValue, maxValue);
        }

        private static void DisableDemoRoot(string objectName)
        {
            GameObject demoObject = GameObject.Find(objectName);

            if (demoObject != null)
            {
                demoObject.SetActive(false);
            }
        }

        private void BuildArena()
        {
            GameObject arenaRoot = new GameObject("Runtime Arena");

            GameObject floor = CreatePrimitive(
                PrimitiveType.Cube,
                "Forest Floor",
                arenaRoot.transform,
                new Vector3(0f, GroundY - 0.5f, 0f),
                Quaternion.identity,
                new Vector3(PlayBounds.size.x, 1f, PlayBounds.size.z),
                groundMaterial,
                true);

            floor.layer = 0;

            for (int i = 0; i < 26; i++)
            {
                Vector3 rockPosition = GetRandomGroundPoint(6f);
                rockPosition.y = GroundY + Random.Range(0.2f, 1.8f);
                Quaternion rockRotation = Random.rotationUniform;
                Vector3 rockScale = new Vector3(Random.Range(1.6f, 4.8f), Random.Range(1.2f, 3.4f), Random.Range(1.6f, 4.6f));

                PrimitiveType primitive = i % 3 == 0 ? PrimitiveType.Capsule : PrimitiveType.Sphere;
                CreatePrimitive(primitive, $"Rock {i + 1}", arenaRoot.transform, rockPosition, rockRotation, rockScale, groundMaterial, false);
            }

            for (int i = 0; i < 32; i++)
            {
                CreateShrubCluster(arenaRoot.transform, i);
            }

        }

        private void CreatePlayer()
        {
            Camera camera = Camera.main;

            if (camera == null)
            {
                GameObject cameraObject = new GameObject("Main Camera");
                camera = cameraObject.AddComponent<Camera>();
                cameraObject.tag = "MainCamera";
                cameraObject.AddComponent<AudioListener>();
            }

            niaVoiceAudioSource = camera.GetComponent<AudioSource>();

            if (niaVoiceAudioSource == null)
            {
                niaVoiceAudioSource = camera.gameObject.AddComponent<AudioSource>();
            }

            niaVoiceAudioSource.playOnAwake = false;
            niaVoiceAudioSource.loop = false;
            niaVoiceAudioSource.spatialBlend = 0f;

            Quaternion initialCameraRotation = camera.transform.rotation;
            GameObject playerObject = new GameObject("Player");
            playerObject.transform.position = GetInitialPlayerPosition(camera);

            if (usingSceneTerrain)
            {
                playerObject.transform.rotation = Quaternion.Euler(0f, initialCameraRotation.eulerAngles.y, 0f);
            }

            CharacterController controller = playerObject.AddComponent<CharacterController>();
            controller.radius = 0.48f;
            controller.height = 1.9f;
            controller.center = new Vector3(0f, 0.92f, 0f);
            controller.slopeLimit = 90f;
            controller.stepOffset = 0.15f;

            GameObject viewPivotObject = new GameObject("View Pivot");
            viewPivotObject.transform.SetParent(playerObject.transform);
            viewPivotObject.transform.localPosition = new Vector3(0f, 0.62f, 0f);
            viewPivotObject.transform.localRotation = usingSceneTerrain
                ? Quaternion.Euler(NormalizePitch(initialCameraRotation.eulerAngles.x), 0f, 0f)
                : Quaternion.identity;

            camera.transform.SetParent(viewPivotObject.transform);
            camera.transform.localPosition = Vector3.zero;
            camera.transform.localRotation = Quaternion.identity;
            camera.nearClipPlane = 0.05f;

            Player = playerObject.AddComponent<ForestPlayerController>();
            Player.Initialize(this, controller, viewPivotObject.transform);
        }

        private static float NormalizePitch(float pitch)
        {
            return Mathf.Clamp(Mathf.Repeat(pitch + 180f, 360f) - 180f, -82f, 82f);
        }

        private Vector3 GetInitialPlayerPosition(Camera camera)
        {
            if (!usingSceneTerrain)
            {
                return new Vector3(0f, 8f, -18f);
            }

            Vector3 position = camera != null
                ? camera.transform.position
                : PlayBounds.center;

            position = ClampPoint(position, 4f);
            position.y = GetSurfaceY(position) + 0.08f;
            return position;
        }

        private Terrain FindTerrainAt(Vector3 point)
        {
            if (sceneTerrains == null || sceneTerrains.Length == 0)
            {
                return null;
            }

            Terrain closestTerrain = null;
            float closestDistance = float.MaxValue;

            for (int i = 0; i < sceneTerrains.Length; i++)
            {
                Terrain terrain = sceneTerrains[i];

                if (terrain == null || terrain.terrainData == null)
                {
                    continue;
                }

                Vector3 terrainPosition = terrain.transform.position;
                Vector3 terrainSize = terrain.terrainData.size;
                bool containsX = point.x >= terrainPosition.x && point.x <= terrainPosition.x + terrainSize.x;
                bool containsZ = point.z >= terrainPosition.z && point.z <= terrainPosition.z + terrainSize.z;

                if (containsX && containsZ)
                {
                    return terrain;
                }

                float clampedX = Mathf.Clamp(point.x, terrainPosition.x, terrainPosition.x + terrainSize.x);
                float clampedZ = Mathf.Clamp(point.z, terrainPosition.z, terrainPosition.z + terrainSize.z);
                float distance = (new Vector2(point.x, point.z) - new Vector2(clampedX, clampedZ)).sqrMagnitude;

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestTerrain = terrain;
                }
            }

            return closestTerrain;
        }

        private void AttachForestBridge(bool autoStart)
        {
            forestBridge = GetComponent<ForestDirectorBridge>();

            if (forestBridge == null)
            {
                forestBridge = gameObject.AddComponent<ForestDirectorBridge>();
            }

            forestBridge.SetAutoConnect(autoStart);
            forestBridge.Initialize(this);

            if (autoStart)
            {
                UpdateBridgeState("starting", "Scanning Codex sessions");
                forestBridge.StartBridge();
            }
        }

        private void CreateShrubCluster(Transform parent, int index)
        {
            Vector3 basePosition = GetRandomGroundPoint(6f);
            basePosition.y = GroundY + 0.1f;

            GameObject cluster = new GameObject($"Shrub {index + 1}");
            cluster.transform.SetParent(parent);
            cluster.transform.position = basePosition;
            cluster.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            int stems = Random.Range(3, 6);

            for (int i = 0; i < stems; i++)
            {
                GameObject stalk = CreatePrimitive(
                    PrimitiveType.Cylinder,
                    $"Stem {i + 1}",
                    cluster.transform,
                    new Vector3(Random.Range(-0.4f, 0.4f), Random.Range(1.8f, 3.4f), Random.Range(-0.4f, 0.4f)),
                    Quaternion.identity,
                    new Vector3(Random.Range(0.12f, 0.22f), Random.Range(1.2f, 2.2f), Random.Range(0.12f, 0.22f)),
                    foliageMaterial,
                    false);

                SimpleSway sway = stalk.AddComponent<SimpleSway>();
                sway.Axis = Vector3.forward;
                sway.Amplitude = Random.Range(9f, 18f);
                sway.Frequency = Random.Range(0.7f, 1.5f);
                sway.VerticalBob = Random.Range(0.05f, 0.18f);
                sway.Phase = Random.Range(0f, Mathf.PI * 2f);
            }
        }

        private GameObject CreatePrimitive(
            PrimitiveType primitiveType,
            string objectName,
            Transform parent,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale,
            Material material,
            bool keepCollider)
        {
            GameObject primitive = GameObject.CreatePrimitive(primitiveType);
            primitive.name = objectName;
            primitive.transform.SetParent(parent);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localRotation = localRotation;
            primitive.transform.localScale = localScale;

            if (!keepCollider)
            {
                Collider collider = primitive.GetComponent<Collider>();

                if (collider != null)
                {
                    Destroy(collider);
                }
            }

            Renderer renderer = primitive.GetComponent<Renderer>();

            if (renderer != null && material != null)
            {
                renderer.sharedMaterial = material;
            }

            return primitive;
        }

        private void CreateSharedMaterials()
        {
            groundMaterial = CreateLitMaterial(new Color(0.14f, 0.2f, 0.19f), new Color(0.04f, 0.09f, 0.08f), 0.18f, 0.03f);
            foliageMaterial = CreateLitMaterial(new Color(0.1f, 0.25f, 0.16f), new Color(0.02f, 0.08f, 0.05f), 0.32f, 0.02f);
        }

        private Material CreateLitMaterial(Color baseColor, Color emissionColor, float smoothness, float metallic)
        {
            Shader shader = FindShader(
                "Universal Render Pipeline/Lit",
                "Standard");

            Material material = new Material(shader);

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", baseColor);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", baseColor);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", smoothness);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", metallic);
            }

            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emissionColor);
            }

            return material;
        }

        private Material CreateUnlitMaterial(Color color)
        {
            Shader shader = FindShader(
                "Universal Render Pipeline/Unlit",
                "Unlit/Color",
                "Sprites/Default");

            Material material = new Material(shader);

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }

            material.renderQueue = 3000;
            return material;
        }

        private Shader FindShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);

                if (shader != null)
                {
                    return shader;
                }
            }

            throw new InvalidOperationException("Unable to find a supported shader for the forest slice.");
        }
    }
}
