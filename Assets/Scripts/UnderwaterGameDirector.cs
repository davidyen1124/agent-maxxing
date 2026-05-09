using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;

namespace Underwater
{
    public sealed class UnderwaterGameDirector : MonoBehaviour
    {
        private readonly Dictionary<string, ThreadLobsterAI> activeThreads = new Dictionary<string, ThreadLobsterAI>();
        private readonly Dictionary<string, ArchivedThreadRoll> archivedRolls = new Dictionary<string, ArchivedThreadRoll>();

        private GUIStyle labelStyle;
        private GUIStyle headlineStyle;
        private GUIStyle mutedStyle;
        private GUIStyle panelStyle;
        private GUIStyle threadTagStyle;

        private Material reefMaterial;
        private Material kelpMaterial;
        private Material threadBodyMaterial;
        private Material threadAccentMaterial;
        private Material rollMaterial;
        private Material surfaceMaterial;
        private AquariumDirectorBridge aquariumBridge;
        private int snapshotSequence;
        private string bridgeState = "offline";
        private string directorStatusLine = "Scanning Codex threads";
        private string nearestThreadTitle = "No active threads";
        private string nearestThreadPhase = "idle";

        public static UnderwaterGameDirector Instance { get; private set; }

        public Bounds PlayBounds { get; private set; }

        public float SeaFloorY => PlayBounds.min.y + 0.5f;

        public UnderwaterPlayerController Player { get; private set; }

        public int ActiveThreadCount => activeThreads.Count;

        public int ArchivedRollCount => archivedRolls.Count;

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
            ConfigureLighting();
            ConfigurePostProcessing();
            BuildArena();
            CreatePlayer();
            AttachAquariumBridge();
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

            const float panelPaddingX = 14f;
            const float panelPaddingY = 10f;
            const float panelWidth = 330f;
            string statusText = Shorten(directorStatusLine, 52);
            float contentWidth = panelWidth - (panelPaddingX * 2f);
            float contentHeight = CalculateHudContentHeight(contentWidth, statusText);
            Rect panel = new Rect(16f, 16f, panelWidth, contentHeight + (panelPaddingY * 2f));
            Rect content = new Rect(panel.x + panelPaddingX, panel.y + panelPaddingY, contentWidth, contentHeight);
            GUI.color = new Color(0.03f, 0.08f, 0.12f, 0.9f);
            GUI.Box(panel, GUIContent.none, panelStyle);
            GUI.color = Color.white;

            GUILayout.BeginArea(content);
            GUILayout.Label("Thread Reef", headlineStyle);
            GUILayout.Space(4f);
            GUILayout.Label("Boost", labelStyle);
            DrawBar(Player.BoostNormalized, new Color(0.3f, 0.82f, 0.96f));
            GUILayout.Space(4f);
            GUILayout.Label($"Active threads: {ActiveThreadCount}", labelStyle);
            GUILayout.Label($"Archived rolls: {ArchivedRollCount}", labelStyle);
            GUILayout.Label(statusText, mutedStyle);
            GUILayout.EndArea();

            DrawThreadNameTags();

            if (!Player.HasPointerLock)
            {
                Rect prompt = new Rect(Screen.width * 0.5f - 170f, Screen.height - 84f, 340f, 40f);
                GUI.Box(prompt, "Click in the window to dive back in.");
            }
        }

        public void SyncThreadWorld(IReadOnlyList<AquariumThreadSnapshot> threads, IReadOnlyList<AquariumArchivedRollSnapshot> rolls, string detail)
        {
            HashSet<string> liveIds = new HashSet<string>();

            if (threads != null)
            {
                for (int i = 0; i < threads.Count; i++)
                {
                    AquariumThreadSnapshot snapshot = threads[i];

                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        continue;
                    }

                    liveIds.Add(snapshot.id);

                    if (activeThreads.TryGetValue(snapshot.id, out ThreadLobsterAI existing))
                    {
                        existing.ApplySnapshot(snapshot);
                    }
                    else
                    {
                        GameObject threadObject = new GameObject($"Thread {snapshot.id}");
                        ThreadLobsterAI threadLobster = threadObject.AddComponent<ThreadLobsterAI>();
                        threadLobster.Initialize(this, snapshot, threadBodyMaterial, threadAccentMaterial);
                        activeThreads[snapshot.id] = threadLobster;
                    }
                }
            }

            List<string> staleActiveIds = new List<string>();

            foreach (KeyValuePair<string, ThreadLobsterAI> pair in activeThreads)
            {
                if (!liveIds.Contains(pair.Key))
                {
                    staleActiveIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleActiveIds.Count; i++)
            {
                string id = staleActiveIds[i];

                if (activeThreads.TryGetValue(id, out ThreadLobsterAI creature))
                {
                    if (creature != null)
                    {
                        Destroy(creature.gameObject);
                    }

                    activeThreads.Remove(id);
                }
            }

            HashSet<string> archivedIds = new HashSet<string>();

            if (rolls != null)
            {
                for (int i = 0; i < rolls.Count; i++)
                {
                    AquariumArchivedRollSnapshot snapshot = rolls[i];

                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.id))
                    {
                        continue;
                    }

                    archivedIds.Add(snapshot.id);

                    if (archivedRolls.ContainsKey(snapshot.id))
                    {
                        continue;
                    }

                    GameObject rollObject = new GameObject($"Lobster Roll {snapshot.id}");
                    ArchivedThreadRoll roll = rollObject.AddComponent<ArchivedThreadRoll>();
                    roll.Initialize(this, snapshot, rollMaterial);
                    archivedRolls[snapshot.id] = roll;
                }
            }

            List<string> staleArchivedIds = new List<string>();

            foreach (KeyValuePair<string, ArchivedThreadRoll> pair in archivedRolls)
            {
                if (!archivedIds.Contains(pair.Key))
                {
                    staleArchivedIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleArchivedIds.Count; i++)
            {
                string id = staleArchivedIds[i];

                if (archivedRolls.TryGetValue(id, out ArchivedThreadRoll roll))
                {
                    if (roll != null)
                    {
                        Destroy(roll.gameObject);
                    }

                    archivedRolls.Remove(id);
                }
            }

            directorStatusLine = string.IsNullOrWhiteSpace(detail)
                ? $"Synced {ActiveThreadCount} swimming threads and {ArchivedRollCount} rolls."
                : detail;
            UpdateNearestThreadStatus();
        }

        public void UpdateBridgeState(string state, string detail)
        {
            bridgeState = string.IsNullOrWhiteSpace(state) ? "offline" : state;

            if (!string.IsNullOrWhiteSpace(detail))
            {
                directorStatusLine = detail;
            }
        }

        public AquariumDirectorSnapshot CreateSnapshot()
        {
            List<AquariumThreadSnapshot> threadSnapshots = new List<AquariumThreadSnapshot>(activeThreads.Count);

            foreach (KeyValuePair<string, ThreadLobsterAI> pair in activeThreads)
            {
                if (pair.Value != null)
                {
                    threadSnapshots.Add(pair.Value.CreateSnapshot());
                }
            }

            List<AquariumArchivedRollSnapshot> rollSnapshots = new List<AquariumArchivedRollSnapshot>(archivedRolls.Count);

            foreach (KeyValuePair<string, ArchivedThreadRoll> pair in archivedRolls)
            {
                if (pair.Value != null)
                {
                    rollSnapshots.Add(pair.Value.CreateSnapshot());
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
                    archivedRolls = ArchivedRollCount,
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
                archivedRolls = rollSnapshots.ToArray()
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
            Vector3 point = GetRandomPoint(margin);
            point.y = Random.Range(SeaFloorY + 4f, PlayBounds.max.y - 2f);
            return point;
        }

        public Vector3 GetRandomSeafloorPoint(float margin = 5f)
        {
            Vector3 point = GetRandomPoint(margin);
            point.y = SeaFloorY + Random.Range(0.45f, 1.15f);
            return point;
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

            panelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(14, 14, 10, 10)
            };

            panelStyle.normal.background = Texture2D.whiteTexture;
            panelStyle.normal.textColor = Color.white;

            mutedStyle = new GUIStyle(labelStyle)
            {
                normal = { textColor = new Color(0.76f, 0.9f, 0.95f) },
                fontSize = 12
            };

            headlineStyle = new GUIStyle(labelStyle)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold
            };

            threadTagStyle = new GUIStyle(labelStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                clipping = TextClipping.Clip,
                padding = new RectOffset(8, 8, 4, 4)
            };
        }

        private void DrawBar(float normalizedValue, Color fillColor)
        {
            Rect rowRect = GUILayoutUtility.GetRect(220f, 18f, GUILayout.ExpandWidth(true));
            Rect barRect = new Rect(rowRect.x, rowRect.y + 3f, rowRect.width, 12f);
            GUI.color = new Color(0.06f, 0.12f, 0.16f, 0.9f);
            GUI.DrawTexture(barRect, Texture2D.whiteTexture);

            Rect fillRect = new Rect(barRect.x + 1f, barRect.y + 1f, (barRect.width - 2f) * Mathf.Clamp01(normalizedValue), barRect.height - 2f);
            GUI.color = fillColor;
            GUI.DrawTexture(fillRect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private float CalculateHudContentHeight(float contentWidth, string statusText)
        {
            return MeasureGuiTextHeight(headlineStyle, "Thread Reef", contentWidth)
                + 4f
                + MeasureGuiTextHeight(labelStyle, "Boost", contentWidth)
                + 18f
                + 4f
                + MeasureGuiTextHeight(labelStyle, $"Active threads: {ActiveThreadCount}", contentWidth)
                + MeasureGuiTextHeight(labelStyle, $"Archived rolls: {ArchivedRollCount}", contentWidth)
                + MeasureGuiTextHeight(mutedStyle, statusText, contentWidth)
                + 2f;
        }

        private static float MeasureGuiTextHeight(GUIStyle style, string text, float width)
        {
            float calculatedHeight = style.CalcHeight(new GUIContent(text), width);
            float fallbackHeight = style.lineHeight > 0f ? style.lineHeight : style.fontSize + 4f;
            return Mathf.Max(calculatedHeight, fallbackHeight);
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

            foreach (KeyValuePair<string, ThreadLobsterAI> pair in activeThreads)
            {
                ThreadLobsterAI thread = pair.Value;

                if (thread == null)
                {
                    continue;
                }

                string title = Shorten(thread.Title, 24);
                DrawNameTag(camera, title, thread.transform.position + Vector3.up * 1.35f, new Color(0.03f, 0.08f, 0.12f, 0.88f));
            }

            foreach (KeyValuePair<string, ArchivedThreadRoll> pair in archivedRolls)
            {
                ArchivedThreadRoll roll = pair.Value;

                if (roll == null)
                {
                    continue;
                }

                string title = Shorten(roll.Title, 24);
                DrawNameTag(camera, title, roll.transform.position + Vector3.up * 0.9f, new Color(0.11f, 0.08f, 0.04f, 0.88f));
            }
        }

        private void DrawNameTag(Camera camera, string title, Vector3 worldAnchor, Color backgroundColor)
        {
            if (camera == null || string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            Vector3 screenPoint = camera.WorldToScreenPoint(worldAnchor);

            if (screenPoint.z <= 0f)
            {
                return;
            }

            Vector2 size = threadTagStyle.CalcSize(new GUIContent(title));
            float width = Mathf.Clamp(size.x + 10f, 88f, 180f);
            float height = 22f;
            float x = Mathf.Clamp(screenPoint.x - (width * 0.5f), 6f, Screen.width - width - 6f);
            float y = Mathf.Clamp(Screen.height - screenPoint.y - 12f, 6f, Screen.height - height - 6f);
            Rect tagRect = new Rect(x, y, width, height);

            GUI.color = backgroundColor;
            GUI.Box(tagRect, GUIContent.none, panelStyle);
            GUI.color = Color.white;
            GUI.Label(tagRect, title, threadTagStyle);
        }

        private void UpdateNearestThreadStatus()
        {
            if (Player == null || activeThreads.Count == 0)
            {
                nearestThreadTitle = "No active threads";
                nearestThreadPhase = "idle";
                return;
            }

            ThreadLobsterAI nearest = null;
            float nearestDistance = float.MaxValue;
            Vector3 playerPosition = Player.transform.position;

            foreach (KeyValuePair<string, ThreadLobsterAI> pair in activeThreads)
            {
                ThreadLobsterAI thread = pair.Value;

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
            nearestThreadPhase = nearest.Phase;
        }

        private string BuildWorldSummary()
        {
            StringBuilder summary = new StringBuilder();
            Vector3 playerPosition = Player != null ? Player.transform.position : Vector3.zero;
            summary.Append("Player at ");
            summary.Append(FormatVector(playerPosition));
            summary.Append(". ");
            summary.Append(ActiveThreadCount);
            summary.Append(" active thread creatures swimming, ");
            summary.Append(ArchivedRollCount);
            summary.Append(" archived lobster rolls resting on the seafloor. ");

            ThreadLobsterAI nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (KeyValuePair<string, ThreadLobsterAI> pair in activeThreads)
            {
                ThreadLobsterAI thread = pair.Value;

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
                summary.Append(" at ");
                summary.Append(FormatVector(nearest.transform.position));
                summary.Append(". ");
            }

            summary.Append(directorStatusLine);
            return summary.ToString().Trim();
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

            Light sun = FindFirstObjectByType<Light>();

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
            Volume volume = FindFirstObjectByType<Volume>();

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

            for (int i = 0; i < 6; i++)
            {
                CreateBubbleColumn(arenaRoot.transform, i);
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

            GameObject playerObject = new GameObject("Player");
            playerObject.transform.position = new Vector3(0f, 8f, -18f);

            CharacterController controller = playerObject.AddComponent<CharacterController>();
            controller.radius = 0.48f;
            controller.height = 1.9f;
            controller.center = new Vector3(0f, 0.92f, 0f);
            controller.slopeLimit = 90f;
            controller.stepOffset = 0.15f;

            GameObject viewPivotObject = new GameObject("View Pivot");
            viewPivotObject.transform.SetParent(playerObject.transform);
            viewPivotObject.transform.localPosition = new Vector3(0f, 0.62f, 0f);
            viewPivotObject.transform.localRotation = Quaternion.identity;

            camera.transform.SetParent(viewPivotObject.transform);
            camera.transform.localPosition = Vector3.zero;
            camera.transform.localRotation = Quaternion.identity;
            camera.nearClipPlane = 0.05f;

            Player = playerObject.AddComponent<UnderwaterPlayerController>();
            Player.Initialize(this, controller, viewPivotObject.transform, camera);
        }

        private void AttachAquariumBridge()
        {
            aquariumBridge = GetComponent<AquariumDirectorBridge>();

            if (aquariumBridge == null)
            {
                aquariumBridge = gameObject.AddComponent<AquariumDirectorBridge>();
            }

            aquariumBridge.Initialize(this);
            UpdateBridgeState("starting", "Scanning Codex sessions");
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

        private void CreateBubbleColumn(Transform parent, int index)
        {
            GameObject bubbleColumn = new GameObject($"Bubble Column {index + 1}");
            bubbleColumn.transform.SetParent(parent);
            bubbleColumn.transform.position = GetRandomSeafloorPoint(8f);
            bubbleColumn.transform.position = new Vector3(
                bubbleColumn.transform.position.x,
                SeaFloorY + 0.2f,
                bubbleColumn.transform.position.z);

            ParticleSystem particleSystem = bubbleColumn.AddComponent<ParticleSystem>();
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = particleSystem.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 6f;
            main.startLifetime = 5.5f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
            main.maxParticles = 140;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(0.74f, 0.96f, 1f, 0.45f);

            ParticleSystem.EmissionModule emission = particleSystem.emission;
            emission.rateOverTime = 12f;

            ParticleSystem.ShapeModule shape = particleSystem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.35f;

            ParticleSystem.VelocityOverLifetimeModule velocityOverLifetime = particleSystem.velocityOverLifetime;
            velocityOverLifetime.enabled = true;
            velocityOverLifetime.x = new ParticleSystem.MinMaxCurve(-0.16f, 0.16f);
            velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocityOverLifetime.z = new ParticleSystem.MinMaxCurve(-0.16f, 0.16f);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = particleSystem.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.7f, 0.92f, 1f), 0f),
                    new GradientColorKey(new Color(0.96f, 1f, 1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.5f, 0.15f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            ParticleSystemRenderer renderer = particleSystem.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            particleSystem.Play();
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
            threadBodyMaterial = CreateLitMaterial(new Color(0.78f, 0.32f, 0.18f), new Color(0.22f, 0.05f, 0.03f), 0.36f, 0.04f);
            threadAccentMaterial = CreateLitMaterial(new Color(0.98f, 0.68f, 0.34f), new Color(0.28f, 0.11f, 0.05f), 0.22f, 0f);
            rollMaterial = CreateLitMaterial(new Color(0.88f, 0.67f, 0.4f), new Color(0.18f, 0.08f, 0.03f), 0.28f, 0.02f);
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
