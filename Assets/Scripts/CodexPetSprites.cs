using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Underwater
{
    public enum CodexPetAnimationState
    {
        Idle,
        RunningRight,
        RunningLeft,
        Waving,
        Jumping,
        Failed,
        Waiting,
        Running,
        Review
    }

    public sealed class CodexPetDefinition
    {
        public string Id { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string Kind { get; set; }

        public string Source { get; set; }

        public Texture2D Spritesheet { get; set; }
    }

    public sealed class CodexPetCatalog
    {
        private const int ExpectedAtlasWidth = 1536;
        private const int ExpectedAtlasHeight = 1872;
        private const string ProjectPetsFolderName = "CodexPets";

        private static CodexPetCatalog shared;

        private readonly List<CodexPetDefinition> pets = new List<CodexPetDefinition>();
        private readonly HashSet<string> loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool loaded;
        private bool loading;

        public float LoadProgress { get; private set; }

        public string LoadingStatus { get; private set; } = "Preparing Codex pets";

        public static CodexPetCatalog Shared
        {
            get
            {
                if (shared == null)
                {
                    shared = new CodexPetCatalog();
                }

                return shared;
            }
        }

        public int Count
        {
            get
            {
                EnsureLoaded();
                return pets.Count;
            }
        }

        public CodexPetDefinition GetPetForSeed(string seed)
        {
            EnsureLoaded();

            if (pets.Count == 0)
            {
                return null;
            }

            unchecked
            {
                int hash = 23;
                string safeSeed = string.IsNullOrWhiteSpace(seed) ? "underwater" : seed;

                for (int i = 0; i < safeSeed.Length; i++)
                {
                    hash = (hash * 31) + safeSeed[i];
                }

                int index = (hash & 0x7fffffff) % pets.Count;
                return pets[index];
            }
        }

        public IEnumerator LoadAsync()
        {
            if (loaded)
            {
                SetLoadProgress(1f, $"Codex pets ready ({pets.Count})");
                yield break;
            }

            if (loading)
            {
                while (!loaded)
                {
                    yield return null;
                }

                yield break;
            }

            loading = true;
            SetLoadProgress(0f, "Scanning Codex pet catalog");
            yield return null;

            string petsRoot = Path.Combine(Application.streamingAssetsPath, ProjectPetsFolderName);

            if (Directory.Exists(petsRoot))
            {
                string[] manifests = Directory.GetFiles(petsRoot, "pet.json", SearchOption.AllDirectories);
                Array.Sort(manifests, StringComparer.OrdinalIgnoreCase);

                if (manifests.Length == 0)
                {
                    Debug.LogWarning($"[CodexPetCatalog] Project pet folder has no pet manifests: {petsRoot}");
                }

                for (int i = 0; i < manifests.Length; i++)
                {
                    string petName = Path.GetFileName(Path.GetDirectoryName(manifests[i]) ?? manifests[i]);
                    SetLoadProgress((float)i / Mathf.Max(1, manifests.Length), $"Loading pet {i + 1}/{manifests.Length}: {petName}");
                    yield return TryLoadPetAsync(manifests[i]);
                    SetLoadProgress((float)(i + 1) / Mathf.Max(1, manifests.Length), $"Loaded {i + 1}/{manifests.Length} pet atlases");
                    yield return null;
                }
            }
            else
            {
                Debug.LogWarning($"[CodexPetCatalog] Project pet folder is missing: {petsRoot}");
            }

            loaded = true;
            loading = false;
            SetLoadProgress(1f, pets.Count > 0 ? $"Codex pets ready ({pets.Count})" : "No Codex pet atlases found");
        }

        private void EnsureLoaded()
        {
            if (loaded || loading)
            {
                return;
            }

            loaded = true;

            string petsRoot = Path.Combine(Application.streamingAssetsPath, ProjectPetsFolderName);

            if (Directory.Exists(petsRoot))
            {
                string[] manifests = Directory.GetFiles(petsRoot, "pet.json", SearchOption.AllDirectories);
                Array.Sort(manifests, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < manifests.Length; i++)
                {
                    TryLoadPet(manifests[i]);
                }
            }
            else
            {
                Debug.LogWarning($"[CodexPetCatalog] Project pet folder is missing: {petsRoot}");
            }
        }

        private void TryLoadPet(string manifestPath)
        {
            if (!TryReadManifest(manifestPath, out PetManifest manifest, out string spritesheetPath))
            {
                return;
            }

            Texture2D spritesheet = LoadSpritesheet(manifest.id, spritesheetPath);

            if (spritesheet != null)
            {
                AddPet(CreatePetDefinition(manifest, spritesheet));
            }
        }

        private IEnumerator TryLoadPetAsync(string manifestPath)
        {
            if (!TryReadManifest(manifestPath, out PetManifest manifest, out string spritesheetPath))
            {
                yield break;
            }

            Texture2D spritesheet = null;
            yield return LoadSpritesheetAsync(manifest.id, spritesheetPath, texture => spritesheet = texture);

            if (spritesheet != null)
            {
                AddPet(CreatePetDefinition(manifest, spritesheet));
            }
        }

        private static bool TryReadManifest(string manifestPath, out PetManifest manifest, out string spritesheetPath)
        {
            manifest = null;
            spritesheetPath = string.Empty;

            try
            {
                manifest = JsonUtility.FromJson<PetManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CodexPetCatalog] Could not load pet manifest '{manifestPath}': {ex.Message}");
                return false;
            }

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.id) || string.IsNullOrWhiteSpace(manifest.spritesheetPath))
            {
                return false;
            }

            string petFolder = Path.GetDirectoryName(manifestPath);
            spritesheetPath = Path.IsPathRooted(manifest.spritesheetPath)
                ? manifest.spritesheetPath
                : Path.Combine(petFolder ?? string.Empty, manifest.spritesheetPath);
            return true;
        }

        private static CodexPetDefinition CreatePetDefinition(PetManifest manifest, Texture2D spritesheet)
        {
            string id = manifest.id.Trim();

            return new CodexPetDefinition
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(manifest.displayName) ? id : manifest.displayName.Trim(),
                Description = string.IsNullOrWhiteSpace(manifest.description) ? string.Empty : manifest.description.Trim(),
                Kind = string.IsNullOrWhiteSpace(manifest.kind) ? string.Empty : manifest.kind.Trim(),
                Source = string.IsNullOrWhiteSpace(manifest.source) ? "project" : manifest.source.Trim(),
                Spritesheet = spritesheet
            };
        }

        private void AddPet(CodexPetDefinition pet)
        {
            if (pet == null || string.IsNullOrWhiteSpace(pet.Id) || pet.Spritesheet == null || loadedIds.Contains(pet.Id))
            {
                return;
            }

            pets.Add(pet);
            loadedIds.Add(pet.Id);
        }

        private static Texture2D LoadSpritesheet(string petId, string spritesheetPath)
        {
            if (string.IsNullOrWhiteSpace(spritesheetPath) || !File.Exists(spritesheetPath))
            {
                return null;
            }

            Texture2D texture = TryLoadImageFile(spritesheetPath);

            if (texture == null && string.Equals(Path.GetExtension(spritesheetPath), ".webp", StringComparison.OrdinalIgnoreCase))
            {
                string cachedPng = ConvertWebpToCachedPng(petId, spritesheetPath);
                texture = TryLoadImageFile(cachedPng);
            }

            if (texture == null)
            {
                Debug.LogWarning($"[CodexPetCatalog] Unity could not decode pet spritesheet '{spritesheetPath}'.");
                return null;
            }

            if (texture.width != ExpectedAtlasWidth || texture.height != ExpectedAtlasHeight)
            {
                Debug.LogWarning(
                    $"[CodexPetCatalog] Pet '{petId}' atlas is {texture.width}x{texture.height}, expected {ExpectedAtlasWidth}x{ExpectedAtlasHeight}.");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.name = $"Codex Pet {petId}";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.anisoLevel = 0;
            return texture;
        }

        private static IEnumerator LoadSpritesheetAsync(string petId, string spritesheetPath, Action<Texture2D> onLoaded)
        {
            onLoaded?.Invoke(null);

            if (string.IsNullOrWhiteSpace(spritesheetPath) || !File.Exists(spritesheetPath))
            {
                yield break;
            }

            string loadPath = spritesheetPath;

            if (string.Equals(Path.GetExtension(spritesheetPath), ".webp", StringComparison.OrdinalIgnoreCase))
            {
                string cachedPng = string.Empty;
                yield return ConvertWebpToCachedPngAsync(petId, spritesheetPath, convertedPath => cachedPng = convertedPath);

                if (!string.IsNullOrWhiteSpace(cachedPng))
                {
                    loadPath = cachedPng;
                }
            }

            Texture2D texture = TryLoadImageFile(loadPath);

            if (texture == null && !string.Equals(loadPath, spritesheetPath, StringComparison.OrdinalIgnoreCase))
            {
                texture = TryLoadImageFile(spritesheetPath);
            }

            if (texture == null)
            {
                Debug.LogWarning($"[CodexPetCatalog] Unity could not decode pet spritesheet '{spritesheetPath}'.");
                yield break;
            }

            if (texture.width != ExpectedAtlasWidth || texture.height != ExpectedAtlasHeight)
            {
                Debug.LogWarning(
                    $"[CodexPetCatalog] Pet '{petId}' atlas is {texture.width}x{texture.height}, expected {ExpectedAtlasWidth}x{ExpectedAtlasHeight}.");
                UnityEngine.Object.Destroy(texture);
                yield break;
            }

            texture.name = $"Codex Pet {petId}";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.anisoLevel = 0;
            onLoaded?.Invoke(texture);
        }

        private static Texture2D TryLoadImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            if (!ImageConversion.LoadImage(texture, data, false))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            return texture;
        }

        private static string ConvertWebpToCachedPng(string petId, string sourcePath)
        {
            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.OSXPlayer)
            {
                return string.Empty;
            }

            try
            {
                string cacheRoot = Path.Combine(Application.temporaryCachePath, "UnderwaterPets");
                Directory.CreateDirectory(cacheRoot);

                string outputPath = Path.Combine(cacheRoot, $"{SafeFilename(petId)}.png");

                if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    return outputPath;
                }

                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/sips",
                    Arguments = $"-s format png {QuoteArgument(sourcePath)} --out {QuoteArgument(outputPath)}",
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };

                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
                {
                    if (process == null || !process.WaitForExit(5000) || process.ExitCode != 0)
                    {
                        return string.Empty;
                    }
                }

                return outputPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CodexPetCatalog] WebP conversion failed for '{sourcePath}': {ex.Message}");
                return string.Empty;
            }
        }

        private static IEnumerator ConvertWebpToCachedPngAsync(string petId, string sourcePath, Action<string> onConverted)
        {
            onConverted?.Invoke(string.Empty);

            if (Application.platform != RuntimePlatform.OSXEditor && Application.platform != RuntimePlatform.OSXPlayer)
            {
                yield break;
            }

            System.Diagnostics.Process process = null;
            string outputPath;

            try
            {
                string cacheRoot = Path.Combine(Application.temporaryCachePath, "UnderwaterPets");
                Directory.CreateDirectory(cacheRoot);

                outputPath = Path.Combine(cacheRoot, $"{SafeFilename(petId)}.png");

                if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    onConverted?.Invoke(outputPath);
                    yield break;
                }

                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/sips",
                    Arguments = $"-s format png {QuoteArgument(sourcePath)} --out {QuoteArgument(outputPath)}",
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };

                process = System.Diagnostics.Process.Start(startInfo);

                if (process == null)
                {
                    yield break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CodexPetCatalog] WebP conversion failed for '{sourcePath}': {ex.Message}");
                yield break;
            }

            float startedAt = Time.realtimeSinceStartup;

            while (!process.HasExited)
            {
                if (Time.realtimeSinceStartup - startedAt > 12f)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // Best-effort cleanup; a failed conversion only skips this atlas.
                    }

                    process.Dispose();
                    yield break;
                }

                yield return null;
            }

            try
            {
                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    onConverted?.Invoke(outputPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CodexPetCatalog] WebP conversion failed for '{sourcePath}': {ex.Message}");
            }

            process.Dispose();
        }

        private void SetLoadProgress(float progress, string status)
        {
            LoadProgress = Mathf.Clamp01(progress);

            if (!string.IsNullOrWhiteSpace(status))
            {
                LoadingStatus = status;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string SafeFilename(string value)
        {
            string safe = string.IsNullOrWhiteSpace(value) ? "pet" : value.Trim();

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(invalid, '-');
            }

            return safe;
        }

        #pragma warning disable 0649
        [Serializable]
        private sealed class PetManifest
        {
            public string id;
            public string displayName;
            public string description;
            public string kind;
            public string source;
            public string spritesheetPath;
        }
        #pragma warning restore 0649
    }

    public sealed class CodexPetSpriteAnimator : MonoBehaviour
    {
        private static readonly Vector2 AtlasScale = new Vector2(1f / 8f, 1f / 9f);

        private static readonly RowSpec[] Rows =
        {
            new RowSpec(CodexPetAnimationState.Idle, 0, new[] { 280, 110, 110, 140, 140, 320 }),
            new RowSpec(CodexPetAnimationState.RunningRight, 1, new[] { 120, 120, 120, 120, 120, 120, 120, 220 }),
            new RowSpec(CodexPetAnimationState.RunningLeft, 2, new[] { 120, 120, 120, 120, 120, 120, 120, 220 }),
            new RowSpec(CodexPetAnimationState.Waving, 3, new[] { 140, 140, 140, 280 }),
            new RowSpec(CodexPetAnimationState.Jumping, 4, new[] { 140, 140, 140, 140, 280 }),
            new RowSpec(CodexPetAnimationState.Failed, 5, new[] { 140, 140, 140, 140, 140, 140, 140, 240 }),
            new RowSpec(CodexPetAnimationState.Waiting, 6, new[] { 150, 150, 150, 150, 150, 260 }),
            new RowSpec(CodexPetAnimationState.Running, 7, new[] { 120, 120, 120, 120, 120, 220 }),
            new RowSpec(CodexPetAnimationState.Review, 8, new[] { 150, 150, 150, 150, 150, 280 })
        };

        private Material material;
        private RowSpec currentRow;
        private int frameIndex;
        private float frameTimerMs;
        private float playbackSpeed = 1f;

        public static CodexPetSpriteAnimator Create(
            Transform parent,
            CodexPetDefinition pet,
            CodexPetAnimationState state,
            Vector3 localPosition,
            float height,
            string objectName)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = objectName;
            quad.transform.SetParent(parent);
            quad.transform.localPosition = localPosition;
            quad.transform.localRotation = Quaternion.identity;
            quad.transform.localScale = new Vector3(height * (192f / 208f), height, 1f);

            Collider collider = quad.GetComponent<Collider>();

            if (collider != null)
            {
                Destroy(collider);
            }

            CodexPetSpriteAnimator animator = quad.AddComponent<CodexPetSpriteAnimator>();
            animator.Initialize(pet, state);
            return animator;
        }

        public void SetState(CodexPetAnimationState state)
        {
            RowSpec row = GetRow(state);

            if (currentRow.State == row.State)
            {
                return;
            }

            currentRow = row;
            frameIndex = 0;
            frameTimerMs = currentRow.DurationsMs[0];
            ApplyFrame();
        }

        public void SetState(string stateId)
        {
            if (TryParseState(stateId, out CodexPetAnimationState state))
            {
                SetState(state);
            }
        }

        public void SetPlaybackSpeed(float speed)
        {
            playbackSpeed = Mathf.Clamp(speed, 0.45f, 2.4f);
        }

        public void RandomizePlayback(float phase)
        {
            if (currentRow.DurationsMs == null || currentRow.DurationsMs.Length == 0)
            {
                return;
            }

            float wrappedPhase = Mathf.Repeat(phase, 1f);
            frameIndex = Mathf.Clamp(Mathf.FloorToInt(wrappedPhase * currentRow.DurationsMs.Length), 0, currentRow.DurationsMs.Length - 1);
            frameTimerMs = Mathf.Lerp(20f, currentRow.DurationsMs[frameIndex], Mathf.Repeat(wrappedPhase * 3.7f, 1f));
            ApplyFrame();
        }

        public static bool TryParseState(string stateId, out CodexPetAnimationState state)
        {
            switch ((stateId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "idle":
                    state = CodexPetAnimationState.Idle;
                    return true;
                case "running-right":
                case "run-right":
                    state = CodexPetAnimationState.RunningRight;
                    return true;
                case "running-left":
                case "run-left":
                    state = CodexPetAnimationState.RunningLeft;
                    return true;
                case "waving":
                case "wave":
                    state = CodexPetAnimationState.Waving;
                    return true;
                case "jumping":
                case "jump":
                    state = CodexPetAnimationState.Jumping;
                    return true;
                case "failed":
                case "failure":
                case "error":
                    state = CodexPetAnimationState.Failed;
                    return true;
                case "waiting":
                case "wait":
                    state = CodexPetAnimationState.Waiting;
                    return true;
                case "running":
                case "run":
                    state = CodexPetAnimationState.Running;
                    return true;
                case "review":
                case "reviewing":
                    state = CodexPetAnimationState.Review;
                    return true;
                default:
                    state = CodexPetAnimationState.Idle;
                    return false;
            }
        }

        private void Initialize(CodexPetDefinition pet, CodexPetAnimationState state)
        {
            material = CreateMaterial(pet.Spritesheet);
            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            currentRow = GetRow(state);
            frameIndex = 0;
            frameTimerMs = currentRow.DurationsMs[0];
            ApplyFrame();
        }

        private void LateUpdate()
        {
            frameTimerMs -= Time.deltaTime * 1000f * playbackSpeed;

            while (frameTimerMs <= 0f)
            {
                frameIndex = (frameIndex + 1) % currentRow.DurationsMs.Length;
                frameTimerMs += currentRow.DurationsMs[frameIndex];
                ApplyFrame();
            }

            Camera camera = Camera.main;

            if (camera != null)
            {
                Vector3 toCamera = camera.transform.position - transform.position;

                if (toCamera.sqrMagnitude > 0.0001f)
                {
                    transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
                }
            }
        }

        private void ApplyFrame()
        {
            if (material == null)
            {
                return;
            }

            Vector2 offset = new Vector2(frameIndex * AtlasScale.x, 1f - ((currentRow.RowIndex + 1) * AtlasScale.y));
            ApplyTextureTransform(material, "_BaseMap", offset);
            ApplyTextureTransform(material, "_MainTex", offset);
        }

        private static void ApplyTextureTransform(Material targetMaterial, string textureName, Vector2 offset)
        {
            if (!targetMaterial.HasProperty(textureName))
            {
                return;
            }

            targetMaterial.SetTextureScale(textureName, AtlasScale);
            targetMaterial.SetTextureOffset(textureName, offset);
        }

        private static RowSpec GetRow(CodexPetAnimationState state)
        {
            for (int i = 0; i < Rows.Length; i++)
            {
                if (Rows[i].State == state)
                {
                    return Rows[i];
                }
            }

            return Rows[0];
        }

        private static Material CreateMaterial(Texture2D spritesheet)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Transparent") ??
                Shader.Find("Sprites/Default");
            Material petMaterial = new Material(shader)
            {
                name = "Codex Pet Sprite"
            };

            if (petMaterial.HasProperty("_BaseMap"))
            {
                petMaterial.SetTexture("_BaseMap", spritesheet);
            }

            if (petMaterial.HasProperty("_MainTex"))
            {
                petMaterial.SetTexture("_MainTex", spritesheet);
            }

            if (petMaterial.HasProperty("_BaseColor"))
            {
                petMaterial.SetColor("_BaseColor", Color.white);
            }

            if (petMaterial.HasProperty("_Color"))
            {
                petMaterial.SetColor("_Color", Color.white);
            }

            SetMaterialInt(petMaterial, "_Surface", 1);
            SetMaterialInt(petMaterial, "_Blend", 0);
            SetMaterialInt(petMaterial, "_Cull", (int)CullMode.Off);
            SetMaterialInt(petMaterial, "_SrcBlend", (int)BlendMode.SrcAlpha);
            SetMaterialInt(petMaterial, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            SetMaterialInt(petMaterial, "_ZWrite", 0);
            petMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            petMaterial.renderQueue = 3000;
            return petMaterial;
        }

        private static void SetMaterialInt(Material targetMaterial, string propertyName, int value)
        {
            if (targetMaterial.HasProperty(propertyName))
            {
                targetMaterial.SetInt(propertyName, value);
            }
        }

        private struct RowSpec
        {
            public readonly CodexPetAnimationState State;
            public readonly int RowIndex;
            public readonly int[] DurationsMs;

            public RowSpec(CodexPetAnimationState state, int rowIndex, int[] durationsMs)
            {
                State = state;
                RowIndex = rowIndex;
                DurationsMs = durationsMs;
            }
        }
    }
}
