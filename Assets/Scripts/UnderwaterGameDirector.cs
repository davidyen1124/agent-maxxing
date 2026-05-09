using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

namespace Underwater
{
    public sealed class UnderwaterGameDirector : MonoBehaviour
    {
        private const string ReefTaskTitlePrefix = "Underwater reef task ";
        private const string ReefTaskCounterPrefsKey = "Underwater.ReefTask.NextThreadNumber";

        [SerializeField] private string niaBaseUrl = "https://apigcp.trynia.ai/v2";
        [SerializeField] private string niaApiKeyEnvironmentVariable = "NIA_API_KEY";
        [SerializeField] private string[] niaRepositories = Array.Empty<string>();
        [SerializeField] private string[] niaDataSources = Array.Empty<string>();
        [SerializeField] private int niaMaxTokens = 900;

        private readonly Dictionary<string, ThreadPetAI> activeThreads = new Dictionary<string, ThreadPetAI>();
        private readonly Dictionary<string, ArchivedThreadPet> archivedPets = new Dictionary<string, ArchivedThreadPet>();

        private GUIStyle labelStyle;
        private GUIStyle threadTagStyle;
        private GUIStyle speechBubbleStyle;
        private GUIStyle speechBubbleShadowStyle;
        private GUIStyle loadingTitleStyle;
        private GUIStyle loadingStatusStyle;
        private GUIStyle hudLabelStyle;
        private GUIStyle hudStrongStyle;

        private Material reefMaterial;
        private Material kelpMaterial;
        private Material surfaceMaterial;
        private AquariumDirectorBridge aquariumBridge;
        private CodexPetCatalog petCatalog;
        private Terrain[] sceneTerrains = Array.Empty<Terrain>();
        private NiaApiClient niaClient;
        private Coroutine worldSyncRoutine;
        private QueuedWorldSync queuedWorldSync;
        private int snapshotSequence;
        private string bridgeState = "offline";
        private string directorStatusLine = "Scanning Codex threads";
        private string nearestThreadTitle = "No active threads";
        private string nearestThreadPhase = "idle";
        private bool startupLoading;
        private bool workThreadSpawnInFlight;
        private int spawnedWorkThreadCount;
        private string workThreadStatusLine = "Codex work thread spawner ready";
        private bool niaSearchInFlight;
        private string niaStatusLine = "Nia reef oracle ready";
        private string lastNiaInsight = string.Empty;
        private bool worldSyncLoading;
        private float worldSyncProgress;
        private string worldSyncStatus = "Loading thread pets";
        private bool usingSceneTerrain;

        private sealed class QueuedWorldSync
        {
            public List<AquariumThreadSnapshot> threads;
            public List<AquariumArchivedPetSnapshot> archivedPets;
            public string detail;
        }

        public static UnderwaterGameDirector Instance { get; private set; }

        public Bounds PlayBounds { get; private set; }

        public float SeaFloorY => PlayBounds.min.y + 0.5f;

        public UnderwaterPlayerController Player { get; private set; }

        public int ActiveThreadCount => activeThreads.Count;

        public int ArchivedPetCount => archivedPets.Count;

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

            CreatePlayer();
            niaClient = new NiaApiClient(niaBaseUrl, niaApiKeyEnvironmentVariable, niaRepositories, niaDataSources, niaMaxTokens);
            AttachAquariumBridge(false);
            StartCoroutine(LoadCodexPetsThenAttachBridge());
        }

        private void Update()
        {
            UpdateNearestThreadStatus();
        }

        private void OnDestroy()
        {
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

            if (startupLoading || worldSyncLoading)
            {
                DrawLoadingOverlay();
                return;
            }

            DrawThreadNameTags();
            DrawStatusHud();
        }

        public void SyncThreadWorld(IReadOnlyList<AquariumThreadSnapshot> threads, IReadOnlyList<AquariumArchivedPetSnapshot> syncedArchivedPets, string detail)
        {
            QueuedWorldSync sync = new QueuedWorldSync
            {
                threads = threads != null ? new List<AquariumThreadSnapshot>(threads) : new List<AquariumThreadSnapshot>(),
                archivedPets = syncedArchivedPets != null ? new List<AquariumArchivedPetSnapshot>(syncedArchivedPets) : new List<AquariumArchivedPetSnapshot>(),
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
            int totalWork = sync.threads.Count + sync.archivedPets.Count + activeThreads.Count + archivedPets.Count;
            int completedWork = 0;
            worldSyncLoading = CountWorldSyncMutations(sync) > 8;
            SetWorldSyncProgress(0, Mathf.Max(1, totalWork), "Loading thread pets");

            HashSet<string> liveIds = new HashSet<string>();

            if (sync.threads != null)
            {
                for (int i = 0; i < sync.threads.Count; i++)
                {
                    AquariumThreadSnapshot snapshot = sync.threads[i];
                    completedWork++;

                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        SetWorldSyncProgress(completedWork, totalWork, "Loading thread pets");
                        yield return null;
                        continue;
                    }

                    liveIds.Add(snapshot.id);

                    if (activeThreads.TryGetValue(snapshot.id, out ThreadPetAI existing))
                    {
                        existing.ApplySnapshot(snapshot);
                    }
                    else
                    {
                        GameObject threadObject = new GameObject($"Thread {snapshot.id}");
                        ThreadPetAI threadPet = threadObject.AddComponent<ThreadPetAI>();

                        if (threadPet.Initialize(this, snapshot))
                        {
                            activeThreads[snapshot.id] = threadPet;
                        }
                        else
                        {
                            Destroy(threadObject);
                        }
                    }

                    SetWorldSyncProgress(completedWork, totalWork, $"Loading thread pets {completedWork}/{totalWork}");
                    yield return null;
                }
            }

            List<string> staleActiveIds = new List<string>();

            foreach (KeyValuePair<string, ThreadPetAI> pair in activeThreads)
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

                if (activeThreads.TryGetValue(id, out ThreadPetAI threadPet))
                {
                    if (threadPet != null)
                    {
                        Destroy(threadPet.gameObject);
                    }

                    activeThreads.Remove(id);
                }

                SetWorldSyncProgress(completedWork, totalWork, $"Cleaning up thread pets {completedWork}/{totalWork}");
                yield return null;
            }

            HashSet<string> archivedIds = new HashSet<string>();

            if (sync.archivedPets != null)
            {
                for (int i = 0; i < sync.archivedPets.Count; i++)
                {
                    AquariumArchivedPetSnapshot snapshot = sync.archivedPets[i];
                    completedWork++;

                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        SetWorldSyncProgress(completedWork, totalWork, "Loading archived pets");
                        yield return null;
                        continue;
                    }

                    archivedIds.Add(snapshot.id);

                    if (archivedPets.ContainsKey(snapshot.id))
                    {
                        SetWorldSyncProgress(completedWork, totalWork, $"Loading archived pets {completedWork}/{totalWork}");
                        yield return null;
                        continue;
                    }

                    GameObject archivedPetObject = new GameObject($"Archived Pet {snapshot.id}");
                    ArchivedThreadPet archivedPet = archivedPetObject.AddComponent<ArchivedThreadPet>();

                    if (archivedPet.Initialize(this, snapshot))
                    {
                        archivedPets[snapshot.id] = archivedPet;
                    }
                    else
                    {
                        Destroy(archivedPetObject);
                    }

                    SetWorldSyncProgress(completedWork, totalWork, $"Loading archived pets {completedWork}/{totalWork}");
                    yield return null;
                }
            }

            List<string> staleArchivedIds = new List<string>();

            foreach (KeyValuePair<string, ArchivedThreadPet> pair in archivedPets)
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

                if (archivedPets.TryGetValue(id, out ArchivedThreadPet archivedPet))
                {
                    if (archivedPet != null)
                    {
                        Destroy(archivedPet.gameObject);
                    }

                    archivedPets.Remove(id);
                }

                SetWorldSyncProgress(completedWork, totalWork, $"Cleaning up archived pets {completedWork}/{totalWork}");
                yield return null;
            }

            directorStatusLine = string.IsNullOrWhiteSpace(sync.detail)
                ? $"Synced {ActiveThreadCount} swimming threads and {ArchivedPetCount} archived pets."
                : sync.detail;
            PersistNextWorkThreadNumber(FindHighestExistingReefTaskNumber() + 1);
            UpdateNearestThreadStatus();
            SetWorldSyncProgress(totalWork, Mathf.Max(1, totalWork), "Thread pets ready");
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
                    AquariumThreadSnapshot snapshot = sync.threads[i];

                    if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        incomingActiveIds.Add(snapshot.id);
                    }
                }
            }

            if (sync.archivedPets != null)
            {
                for (int i = 0; i < sync.archivedPets.Count; i++)
                {
                    AquariumArchivedPetSnapshot snapshot = sync.archivedPets[i];

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
                if (!archivedPets.ContainsKey(id))
                {
                    mutationCount++;
                }
            }

            foreach (string id in archivedPets.Keys)
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

        public void RequestWorkThreadSpawnFromPlayer()
        {
            if (startupLoading)
            {
                SetWorkThreadStatus("Codex pets are still loading. Try again in a moment.");
                return;
            }

            if (workThreadSpawnInFlight)
            {
                SetWorkThreadStatus("Already creating a Codex work thread.");
                return;
            }

            if (aquariumBridge == null || !aquariumBridge.IsConnected)
            {
                SetWorkThreadStatus("Codex bridge is offline. Start the app-server, then press E again.");
                UpdateBridgeState("offline", workThreadStatusLine);
                return;
            }

            int nextThreadNumber = GetNextPersistentWorkThreadNumber();
            string title = CreateReefTaskTitle(nextThreadNumber);
            string prompt = BuildWorkThreadPrompt(title);

            workThreadSpawnInFlight = true;
            SetWorkThreadStatus($"Creating '{title}'...");
            _ = CreateWorkThreadFromWorldAsync(title, prompt, nextThreadNumber);
        }

        public void RequestNiaReefInsightFromPlayer()
        {
            if (startupLoading)
            {
                SetNiaStatus("Nia will be ready after the reef finishes loading.");
                return;
            }

            if (niaSearchInFlight)
            {
                SetNiaStatus("Nia is already reading the reef.");
                return;
            }

            niaClient ??= new NiaApiClient(niaBaseUrl, niaApiKeyEnvironmentVariable, niaRepositories, niaDataSources, niaMaxTokens);

            if (!niaClient.HasApiKey)
            {
                SetNiaStatus($"Set {niaApiKeyEnvironmentVariable} before launching Unity to enable Nia.");
                return;
            }

            niaSearchInFlight = true;
            lastNiaInsight = string.Empty;
            SetNiaStatus("Asking Nia for a grounded reef read...");
            _ = RequestNiaReefInsightAsync();
        }

        public AquariumDirectorSnapshot CreateSnapshot()
        {
            List<AquariumThreadSnapshot> threadSnapshots = new List<AquariumThreadSnapshot>(activeThreads.Count);

            foreach (KeyValuePair<string, ThreadPetAI> pair in activeThreads)
            {
                if (pair.Value != null)
                {
                    threadSnapshots.Add(pair.Value.CreateSnapshot());
                }
            }

            List<AquariumArchivedPetSnapshot> archivedPetSnapshots = new List<AquariumArchivedPetSnapshot>(archivedPets.Count);

            foreach (KeyValuePair<string, ArchivedThreadPet> pair in archivedPets)
            {
                if (pair.Value != null)
                {
                    archivedPetSnapshots.Add(pair.Value.CreateSnapshot());
                }
            }

            return new AquariumDirectorSnapshot
            {
                sequence = ++snapshotSequence,
                capturedAtUtc = DateTime.UtcNow.ToString("o"),
                summary = BuildWorldSummary(),
                metrics = new AquariumDirectorMetrics
                {
                    activeThreads = ActiveThreadCount,
                    archivedPets = ArchivedPetCount,
                    bridgeState = bridgeState
                },
                player = new AquariumPlayerSnapshot
                {
                    position = SerializableVector3.FromVector3(Player != null ? Player.transform.position : Vector3.zero),
                    forward = SerializableVector3.FromVector3(Player != null ? Player.transform.forward : Vector3.forward),
                    boostNormalized = Player != null ? Player.BoostNormalized : 0f,
                    hasPointerLock = Player != null && Player.HasPointerLock
                },
                threads = threadSnapshots.ToArray(),
                archivedPets = archivedPetSnapshots.ToArray()
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

        public Vector3 GetRandomMidWaterPoint(float margin = 8f)
        {
            Vector3 point = usingSceneTerrain
                ? GetRandomTerrainPointNearPlayer(margin, 12f, 42f)
                : GetRandomPoint(margin);

            if (usingSceneTerrain)
            {
                point.y = Mathf.Min(PlayBounds.max.y - 2f, GetSurfaceY(point) + Random.Range(3.5f, 11f));
            }
            else
            {
                point.y = Random.Range(SeaFloorY + 4f, PlayBounds.max.y - 2f);
            }

            return point;
        }

        public Vector3 GetRandomSeafloorPoint(float margin = 5f)
        {
            Vector3 point = usingSceneTerrain
                ? GetRandomTerrainPointNearPlayer(margin, 9f, 34f)
                : GetRandomPoint(margin);
            point.y = usingSceneTerrain
                ? GetSurfaceY(point) + Random.Range(0.45f, 1.15f)
                : SeaFloorY + Random.Range(0.45f, 1.15f);
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
                return SeaFloorY;
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

            hudLabelStyle = new GUIStyle(labelStyle)
            {
                fontSize = 13,
                wordWrap = true,
                clipping = TextClipping.Clip,
                padding = new RectOffset(12, 12, 3, 3),
                normal = { textColor = new Color(0.77f, 0.94f, 0.96f) }
            };

            hudStrongStyle = new GUIStyle(hudLabelStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 1f, 0.98f) }
            };
        }

        private static Texture2D CreateRoundedRectTexture(int width, int height, float radius, Color fillColor, Color borderColor, float borderWidth)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Aquarium Speech Bubble"
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

            foreach (KeyValuePair<string, ThreadPetAI> pair in activeThreads)
            {
                ThreadPetAI thread = pair.Value;

                if (thread == null)
                {
                    continue;
                }

                string message = Shorten(thread.BubbleMessage, 64);
                DrawNameTag(camera, message, thread.transform.position + Vector3.up * 1.55f);
            }

            foreach (KeyValuePair<string, ArchivedThreadPet> pair in archivedPets)
            {
                ArchivedThreadPet archivedPet = pair.Value;

                if (archivedPet == null)
                {
                    continue;
                }

                string message = Shorten(archivedPet.StatusMessage, 64);
                DrawNameTag(camera, message, archivedPet.transform.position + Vector3.up * 1.05f);
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

        private void DrawStatusHud()
        {
            string insight = string.IsNullOrWhiteSpace(lastNiaInsight) ? niaStatusLine : lastNiaInsight;
            float width = Mathf.Clamp(Screen.width - 36f, 300f, 460f);
            float height = string.IsNullOrWhiteSpace(insight) ? 86f : 122f;
            Rect panelRect = new Rect(18f, 18f, width, height);
            Rect bridgeRect = new Rect(panelRect.x + 10f, panelRect.y + 10f, panelRect.width - 20f, 22f);
            Rect threadRect = new Rect(panelRect.x + 10f, panelRect.y + 34f, panelRect.width - 20f, 22f);
            Rect niaRect = new Rect(panelRect.x + 10f, panelRect.y + 58f, panelRect.width - 20f, 50f);
            Color previousColor = GUI.color;

            GUI.color = new Color(0.01f, 0.07f, 0.1f, 0.72f);
            GUI.DrawTexture(panelRect, Texture2D.whiteTexture);
            GUI.color = new Color(0.24f, 0.9f, 0.98f, 0.38f);
            GUI.DrawTexture(new Rect(panelRect.x, panelRect.yMax - 2f, panelRect.width, 2f), Texture2D.whiteTexture);
            GUI.color = previousColor;

            GUI.Label(bridgeRect, Shorten(directorStatusLine, 80), hudLabelStyle);
            GUI.Label(threadRect, Shorten(nearestThreadTitle, 42) + " - " + Shorten(nearestThreadPhase, 42), hudStrongStyle);
            GUI.Label(niaRect, Shorten(insight, 160), hudLabelStyle);
        }

        private static bool ShouldDrawBubbleMessage(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && !string.Equals(message.Trim(), "Idle", StringComparison.OrdinalIgnoreCase);
        }

        private void DrawLoadingOverlay()
        {
            bool loadingCatalog = startupLoading && petCatalog != null;
            float progress = loadingCatalog ? petCatalog.LoadProgress : worldSyncProgress;
            string title = loadingCatalog ? "Loading thread pets" : "Syncing threads";
            string status = loadingCatalog ? petCatalog.LoadingStatus : worldSyncStatus;
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

            ThreadPetAI nearest = null;
            float nearestDistance = float.MaxValue;
            Vector3 playerPosition = Player.transform.position;

            foreach (KeyValuePair<string, ThreadPetAI> pair in activeThreads)
            {
                ThreadPetAI thread = pair.Value;

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
            nearestThreadPhase = string.IsNullOrWhiteSpace(nearest.StatusMessage) ? nearest.Phase : nearest.StatusMessage;
        }

        private string BuildWorldSummary()
        {
            StringBuilder summary = new StringBuilder();
            Vector3 playerPosition = Player != null ? Player.transform.position : Vector3.zero;
            summary.Append("Player at ");
            summary.Append(FormatVector(playerPosition));
            summary.Append(". ");
            summary.Append(ActiveThreadCount);
            summary.Append(" active thread pets swimming, ");
            summary.Append(ArchivedPetCount);
            summary.Append(" archived pet companions resting on the seafloor. ");

            ThreadPetAI nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (KeyValuePair<string, ThreadPetAI> pair in activeThreads)
            {
                ThreadPetAI thread = pair.Value;

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

        private string BuildWorkThreadPrompt(string title)
        {
            StringBuilder prompt = new StringBuilder();
            prompt.Append("A player spawned this Codex work thread from play mode inside the Underwater world.");
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.Append("Thread title: ");
            prompt.Append(title);
            prompt.AppendLine();
            prompt.Append("World state: ");
            prompt.Append(BuildWorldSummary());
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.Append("Use the current workspace when it is relevant. Start by stating what useful next step you can take from this context.");
            return prompt.ToString();
        }

        private string BuildNiaInsightPrompt()
        {
            StringBuilder prompt = new StringBuilder();
            prompt.Append("You are the Nia-backed knowledge layer for a Unity game named Underwater. ");
            prompt.Append("Use any indexed project, documentation, or web context available to give one grounded, practical next step. ");
            prompt.Append("Keep the answer under 35 words and avoid generic encouragement.");
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.Append("Current world state: ");
            prompt.Append(BuildWorldSummary());
            prompt.AppendLine();
            prompt.Append("Nearest thread title: ");
            prompt.Append(nearestThreadTitle);
            prompt.AppendLine();
            prompt.Append("Nearest thread phase/status: ");
            prompt.Append(nearestThreadPhase);
            return prompt.ToString();
        }

        private async Task RequestNiaReefInsightAsync()
        {
            try
            {
                NiaSearchResult result = await niaClient.QueryAsync(BuildNiaInsightPrompt(), CancellationToken.None);
                niaSearchInFlight = false;
                lastNiaInsight = CleanHudText(result.Answer, 180);

                if (result.SourceLabels != null && result.SourceLabels.Length > 0)
                {
                    SetNiaStatus($"Nia cited {result.SourceLabels.Length} source(s).");
                }
                else
                {
                    SetNiaStatus("Nia answered from available context.");
                }
            }
            catch (Exception ex)
            {
                niaSearchInFlight = false;
                lastNiaInsight = string.Empty;
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                SetNiaStatus($"Nia unavailable: {Shorten(message, 96)}");
            }
        }

        private async Task CreateWorkThreadFromWorldAsync(string title, string prompt, int workThreadNumber)
        {
            try
            {
                string threadId = await aquariumBridge.CreateWorkThreadAsync(title, prompt);
                spawnedWorkThreadCount++;
                PersistNextWorkThreadNumber(workThreadNumber + 1);
                workThreadSpawnInFlight = false;
                SetWorkThreadStatus($"Created '{title}' ({Shorten(threadId, 8)}).");
                directorStatusLine = "Spawned a Codex work thread from the reef.";
            }
            catch (Exception ex)
            {
                workThreadSpawnInFlight = false;
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                SetWorkThreadStatus($"Could not spawn work thread: {Shorten(message, 72)}");
                UpdateBridgeState(aquariumBridge != null && aquariumBridge.IsConnected ? "warning" : "offline", workThreadStatusLine);
            }
        }

        private void SetWorkThreadStatus(string status)
        {
            workThreadStatusLine = string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim();
        }

        private void SetNiaStatus(string status)
        {
            niaStatusLine = string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim();
        }

        private int GetNextPersistentWorkThreadNumber()
        {
            int savedNextNumber = Mathf.Max(1, PlayerPrefs.GetInt(ReefTaskCounterPrefsKey, 1));
            int worldNextNumber = FindHighestExistingReefTaskNumber() + 1;
            int sessionNextNumber = spawnedWorkThreadCount + 1;
            return Mathf.Max(savedNextNumber, worldNextNumber, sessionNextNumber);
        }

        private void PersistNextWorkThreadNumber(int nextNumber)
        {
            int normalizedNextNumber = Mathf.Max(1, nextNumber);
            int savedNextNumber = Mathf.Max(1, PlayerPrefs.GetInt(ReefTaskCounterPrefsKey, 1));

            if (normalizedNextNumber <= savedNextNumber)
            {
                return;
            }

            PlayerPrefs.SetInt(ReefTaskCounterPrefsKey, normalizedNextNumber);
            PlayerPrefs.Save();
        }

        private int FindHighestExistingReefTaskNumber()
        {
            int highestNumber = 0;

            foreach (KeyValuePair<string, ThreadPetAI> pair in activeThreads)
            {
                if (pair.Value != null && TryReadReefTaskNumber(pair.Value.Title, out int threadNumber))
                {
                    highestNumber = Mathf.Max(highestNumber, threadNumber);
                }
            }

            foreach (KeyValuePair<string, ArchivedThreadPet> pair in archivedPets)
            {
                if (pair.Value != null && TryReadReefTaskNumber(pair.Value.Title, out int archivedNumber))
                {
                    highestNumber = Mathf.Max(highestNumber, archivedNumber);
                }
            }

            return highestNumber;
        }

        private static string CreateReefTaskTitle(int number)
        {
            return $"{ReefTaskTitlePrefix}{Mathf.Max(1, number)}";
        }

        private static bool TryReadReefTaskNumber(string title, out int number)
        {
            number = 0;

            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            string trimmedTitle = title.Trim();

            if (!trimmedTitle.StartsWith(ReefTaskTitlePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string suffix = trimmedTitle.Substring(ReefTaskTitlePrefix.Length).Trim();
            return int.TryParse(suffix, out number) && number > 0;
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"({vector.x:F1}, {vector.y:F1}, {vector.z:F1})";
        }

        private static string CleanHudText(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string cleaned = text.Replace("\r", " ").Replace("\n", " ").Trim();

            while (cleaned.Contains("  "))
            {
                cleaned = cleaned.Replace("  ", " ");
            }

            return Shorten(cleaned, maxLength);
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

            Light sun = FindAnyObjectByType<Light>();

            if (sun == null)
            {
                GameObject lightObject = new GameObject("Directional Light");
                sun = lightObject.AddComponent<Light>();
                sun.type = LightType.Directional;
            }

            sun.color = new Color(0.53f, 0.82f, 0.94f);
            sun.intensity = 0.52f;
            sun.transform.rotation = Quaternion.Euler(60f, -24f, 0f);
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

            bloom.active = true;
            bloom.threshold.Override(0.72f);
            bloom.intensity.Override(0.58f);
            bloom.scatter.Override(0.78f);

            if (!runtimeProfile.TryGet(out Vignette vignette))
            {
                vignette = runtimeProfile.Add<Vignette>(true);
            }

            vignette.active = true;
            vignette.intensity.Override(0.22f);
            vignette.smoothness.Override(0.65f);

            if (!runtimeProfile.TryGet(out ColorAdjustments colorAdjustments))
            {
                colorAdjustments = runtimeProfile.Add<ColorAdjustments>(true);
            }

            colorAdjustments.active = true;
            colorAdjustments.postExposure.Override(-0.12f);
            colorAdjustments.saturation.Override(-26f);
            colorAdjustments.colorFilter.Override(new Color(0.74f, 0.92f, 1f));
        }

        private bool TryConfigureSceneTerrainWorld()
        {
            sceneTerrains = FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

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
                camera.farClipPlane = Mathf.Max(camera.farClipPlane, Mathf.Max(PlayBounds.size.x, PlayBounds.size.z) * 1.5f);
                camera.fieldOfView = 72f;
            }

            DisableDemoRoot("VirtualCameras");
            DisableDemoRoot("Timelines");
            DisableDemoRoot("UI");
            DisableDemoRoot("EventSystem");
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
                "Sea Floor",
                arenaRoot.transform,
                new Vector3(0f, SeaFloorY - 0.5f, 0f),
                Quaternion.identity,
                new Vector3(PlayBounds.size.x, 1f, PlayBounds.size.z),
                reefMaterial,
                true);

            floor.layer = 0;

            CreatePrimitive(
                PrimitiveType.Quad,
                "Water Surface",
                arenaRoot.transform,
                new Vector3(0f, PlayBounds.max.y - 0.15f, 0f),
                Quaternion.Euler(90f, 0f, 0f),
                new Vector3(PlayBounds.size.x, PlayBounds.size.z, 1f),
                surfaceMaterial,
                false);

            for (int i = 0; i < 26; i++)
            {
                Vector3 rockPosition = GetRandomSeafloorPoint(6f);
                rockPosition.y = SeaFloorY + Random.Range(0.2f, 1.8f);
                Quaternion rockRotation = Random.rotationUniform;
                Vector3 rockScale = new Vector3(Random.Range(1.6f, 4.8f), Random.Range(1.2f, 3.4f), Random.Range(1.6f, 4.6f));

                PrimitiveType primitive = i % 3 == 0 ? PrimitiveType.Capsule : PrimitiveType.Sphere;
                CreatePrimitive(primitive, $"Rock {i + 1}", arenaRoot.transform, rockPosition, rockRotation, rockScale, reefMaterial, false);
            }

            for (int i = 0; i < 32; i++)
            {
                CreateKelpCluster(arenaRoot.transform, i);
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

            Player = playerObject.AddComponent<UnderwaterPlayerController>();
            Player.Initialize(this, controller, viewPivotObject.transform, camera);
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
            position.y = Mathf.Max(position.y, GetSurfaceY(position) + 2.2f);
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

        private void AttachAquariumBridge(bool autoStart)
        {
            aquariumBridge = GetComponent<AquariumDirectorBridge>();

            if (aquariumBridge == null)
            {
                aquariumBridge = gameObject.AddComponent<AquariumDirectorBridge>();
            }

            aquariumBridge.SetAutoConnect(autoStart);
            aquariumBridge.Initialize(this);

            if (autoStart)
            {
                UpdateBridgeState("starting", "Scanning Codex sessions");
                aquariumBridge.StartBridge();
            }
        }

        private IEnumerator LoadCodexPetsThenAttachBridge()
        {
            startupLoading = true;
            directorStatusLine = "Loading Codex pet sprites";
            petCatalog = CodexPetCatalog.Shared;

            yield return petCatalog.LoadAsync();
            yield return null;

            startupLoading = false;
            AttachAquariumBridge(true);
        }

        private void CreateKelpCluster(Transform parent, int index)
        {
            Vector3 basePosition = GetRandomSeafloorPoint(6f);
            basePosition.y = SeaFloorY + 0.1f;

            GameObject cluster = new GameObject($"Kelp {index + 1}");
            cluster.transform.SetParent(parent);
            cluster.transform.position = basePosition;
            cluster.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            int fronds = Random.Range(3, 6);

            for (int i = 0; i < fronds; i++)
            {
                GameObject stalk = CreatePrimitive(
                    PrimitiveType.Cylinder,
                    $"Frond {i + 1}",
                    cluster.transform,
                    new Vector3(Random.Range(-0.4f, 0.4f), Random.Range(1.8f, 3.4f), Random.Range(-0.4f, 0.4f)),
                    Quaternion.identity,
                    new Vector3(Random.Range(0.12f, 0.22f), Random.Range(1.2f, 2.2f), Random.Range(0.12f, 0.22f)),
                    kelpMaterial,
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
            reefMaterial = CreateLitMaterial(new Color(0.14f, 0.2f, 0.19f), new Color(0.04f, 0.09f, 0.08f), 0.18f, 0.03f);
            kelpMaterial = CreateLitMaterial(new Color(0.1f, 0.25f, 0.16f), new Color(0.02f, 0.08f, 0.05f), 0.32f, 0.02f);
            surfaceMaterial = CreateUnlitMaterial(new Color(0.3f, 0.78f, 0.88f, 0.18f));
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

            throw new InvalidOperationException("Unable to find a supported shader for the underwater slice.");
        }
    }
}
