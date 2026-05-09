using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Underwater
{
    public sealed class UnderwaterGameDirector : MonoBehaviour
    {
        private const int SharkCount = 5;
        private const int LobsterCount = 14;

        private readonly List<SeaCreature> creatures = new List<SeaCreature>();
        private readonly List<SharkAI> sharks = new List<SharkAI>();
        private readonly List<LobsterAI> lobsters = new List<LobsterAI>();

        private GUIStyle labelStyle;
        private GUIStyle headlineStyle;

        private Material reefMaterial;
        private Material kelpMaterial;
        private Material sharkBodyMaterial;
        private Material sharkAccentMaterial;
        private Material lobsterBodyMaterial;
        private Material lobsterAccentMaterial;
        private Material surfaceMaterial;

        public static UnderwaterGameDirector Instance { get; private set; }

        public Bounds PlayBounds { get; private set; }

        public float SeaFloorY => PlayBounds.min.y + 0.5f;

        public UnderwaterPlayerController Player { get; private set; }

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
            SpawnPopulation();
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

            Rect panel = new Rect(16f, 16f, 280f, 126f);
            GUI.Box(panel, GUIContent.none);

            GUILayout.BeginArea(panel);
            GUILayout.Space(8f);
            GUILayout.Label("Underwater Swim Slice", headlineStyle);
            GUILayout.Label("WASD move  Mouse look  Space rise  Ctrl dive", labelStyle);
            GUILayout.Label("Shift sprint  Ambient reef pass  Esc unlock", labelStyle);
            GUILayout.Space(8f);
            DrawBar("Boost", Player.BoostNormalized, new Color(0.3f, 0.82f, 0.96f));
            GUILayout.Space(6f);
            GUILayout.Label($"Sharks active: {CountAlive(CreatureKind.Shark)}", labelStyle);
            GUILayout.Label($"Lobsters alive: {CountAlive(CreatureKind.Lobster)}", labelStyle);
            GUILayout.EndArea();

            if (!Player.HasPointerLock)
            {
                Rect prompt = new Rect(Screen.width * 0.5f - 170f, Screen.height - 84f, 340f, 40f);
                GUI.Box(prompt, "Click in the window to dive back in.");
            }
        }

        public void RegisterCreature(SeaCreature creature)
        {
            if (creature == null || creatures.Contains(creature))
            {
                return;
            }

            creatures.Add(creature);

            if (creature is SharkAI shark)
            {
                sharks.Add(shark);
            }
            else if (creature is LobsterAI lobster)
            {
                lobsters.Add(lobster);
            }
        }

        public void UnregisterCreature(SeaCreature creature)
        {
            if (creature == null)
            {
                return;
            }

            creatures.Remove(creature);

            if (creature is SharkAI shark)
            {
                sharks.Remove(shark);
            }
            else if (creature is LobsterAI lobster)
            {
                lobsters.Remove(lobster);
            }
        }

        public int CountAlive(CreatureKind kind)
        {
            int alive = 0;

            for (int i = 0; i < creatures.Count; i++)
            {
                SeaCreature creature = creatures[i];

                if (creature != null && creature.IsAlive && creature.Kind == kind)
                {
                    alive++;
                }
            }

            return alive;
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

            headlineStyle = new GUIStyle(labelStyle)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold
            };
        }

        private void DrawBar(string label, float normalizedValue, Color fillColor)
        {
            Rect rowRect = GUILayoutUtility.GetRect(220f, 18f, GUILayout.ExpandWidth(true));
            GUI.Label(new Rect(rowRect.x, rowRect.y, 54f, rowRect.height), label, labelStyle);

            Rect barRect = new Rect(rowRect.x + 58f, rowRect.y + 3f, rowRect.width - 64f, 12f);
            GUI.color = new Color(0.06f, 0.12f, 0.16f, 0.9f);
            GUI.DrawTexture(barRect, Texture2D.whiteTexture);

            Rect fillRect = new Rect(barRect.x + 1f, barRect.y + 1f, (barRect.width - 2f) * Mathf.Clamp01(normalizedValue), barRect.height - 2f);
            GUI.color = fillColor;
            GUI.DrawTexture(fillRect, Texture2D.whiteTexture);
            GUI.color = Color.white;
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

        private void SpawnPopulation()
        {
            for (int i = 0; i < SharkCount; i++)
            {
                GameObject sharkObject = new GameObject($"Shark {i + 1}");
                SharkAI shark = sharkObject.AddComponent<SharkAI>();
                shark.Initialize(this, GetRandomMidWaterPoint(), sharkBodyMaterial, sharkAccentMaterial);
            }

            for (int i = 0; i < LobsterCount; i++)
            {
                GameObject lobsterObject = new GameObject($"Lobster {i + 1}");
                LobsterAI lobster = lobsterObject.AddComponent<LobsterAI>();
                lobster.Initialize(this, GetRandomSeafloorPoint(), lobsterBodyMaterial, lobsterAccentMaterial);
            }
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
            sharkBodyMaterial = CreateLitMaterial(new Color(0.28f, 0.42f, 0.5f), new Color(0.04f, 0.11f, 0.16f), 0.56f, 0.08f);
            sharkAccentMaterial = CreateLitMaterial(new Color(0.74f, 0.86f, 0.94f), new Color(0.08f, 0.16f, 0.2f), 0.18f, 0f);
            lobsterBodyMaterial = CreateLitMaterial(new Color(0.74f, 0.26f, 0.17f), new Color(0.22f, 0.05f, 0.03f), 0.36f, 0.04f);
            lobsterAccentMaterial = CreateLitMaterial(new Color(0.96f, 0.57f, 0.3f), new Color(0.28f, 0.11f, 0.05f), 0.22f, 0f);
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

            return Shader.Find("Standard");
        }
    }
}
