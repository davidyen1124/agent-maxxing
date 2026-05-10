using UnityEngine;

namespace Forest
{
    public sealed class ThreadAnimalVisual : MonoBehaviour
    {
        private const string AnimatorMoveParameter = "Vert";
        private const string AnimatorRunParameter = "State";
        private const float AnimatorDampTime = 0.08f;

        private static readonly string[] AnimalPrefabNames =
        {
            "Dog_001",
            "Kitty_001",
            "Chicken_001",
            "Deer_001",
            "Pinguin_001",
            "Tiger_001",
            "Horse_001"
        };

        private static readonly string[] AnimalDisplayNames =
        {
            "Dog",
            "Kitty",
            "Chicken",
            "Deer",
            "Pinguin",
            "Tiger",
            "Horse"
        };

        private Animator animator;
        private Transform modelRoot;
        private Light focusLight;
        private Vector3 modelBaseLocalPosition;
        private float targetHeight = 1.2f;
        private float currentMove;
        private float currentRun;
        private float focusGlow;
        private float bobSeed;
        private string animalId = "unknown-animal";
        private string animalDisplayName = "Unknown animal";

        public string AnimalId => animalId;

        public string AnimalDisplayName => animalDisplayName;

        public static ThreadAnimalVisual Create(Transform parent, string seed, float targetHeight)
        {
            int index = GetStableIndex(seed, AnimalPrefabNames.Length);
            GameObject prefab = LoadAnimalPrefab(index);

            if (prefab == null)
            {
                Debug.LogWarning("[ThreadAnimalVisual] No animal prefab could be loaded for thread animal.");
                return null;
            }

            ThreadAnimalVisual visual = parent.gameObject.AddComponent<ThreadAnimalVisual>();
            visual.Initialize(prefab, index, targetHeight);
            return visual;
        }

        public void SetState(CodexAnimalAnimationState state, Vector3 velocity, bool forceRun, bool focused = false, bool failedFocus = false)
        {
            if (animator == null)
            {
                return;
            }

            float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            float move = Mathf.InverseLerp(0.05f, 2.2f, horizontalSpeed);
            float run = forceRun || horizontalSpeed > 3.2f || IsRunningState(state) ? 1f : 0f;

            currentMove = Mathf.MoveTowards(currentMove, move, Time.deltaTime * 5f);
            currentRun = Mathf.MoveTowards(currentRun, run, Time.deltaTime * 6f);
            animator.SetFloat(AnimatorMoveParameter, currentMove, AnimatorDampTime, Time.deltaTime);
            animator.SetFloat(AnimatorRunParameter, currentRun, AnimatorDampTime, Time.deltaTime);

            if (modelRoot != null)
            {
                float jumpBob = state == CodexAnimalAnimationState.Jumping ? Mathf.Abs(Mathf.Sin(Time.time * 9f + bobSeed)) * 0.08f : 0f;
                float idleBob = currentMove < 0.05f ? Mathf.Sin(Time.time * 2.1f + bobSeed) * 0.015f : 0f;
                modelRoot.localPosition = modelBaseLocalPosition + (Vector3.up * (jumpBob + idleBob));
            }

            UpdateFocusGlow(focused, failedFocus);
        }

        private void Initialize(GameObject prefab, int animalIndex, float requestedHeight)
        {
            targetHeight = Mathf.Max(0.2f, requestedHeight);
            bobSeed = Random.Range(0f, 100f);
            animalId = animalIndex >= 0 && animalIndex < AnimalPrefabNames.Length ? AnimalPrefabNames[animalIndex] : prefab.name;
            animalDisplayName = animalIndex >= 0 && animalIndex < AnimalDisplayNames.Length ? AnimalDisplayNames[animalIndex] : prefab.name;

            GameObject instance = Instantiate(prefab, transform);
            instance.name = prefab.name;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            modelRoot = instance.transform;

            StripImportedControlComponents(instance);
            ScaleAndGround(instance);
            modelBaseLocalPosition = modelRoot.localPosition;
            animator = instance.GetComponentInChildren<Animator>();
            SetRenderersForRuntime(instance);
            CreateFocusLight();
        }

        private void CreateFocusLight()
        {
            GameObject lightObject = new GameObject("Thread Animal Focus Glow");
            lightObject.transform.SetParent(transform);
            lightObject.transform.localPosition = Vector3.up * Mathf.Max(0.35f, targetHeight * 0.58f);
            lightObject.transform.localRotation = Quaternion.identity;

            focusLight = lightObject.AddComponent<Light>();
            focusLight.type = LightType.Point;
            focusLight.range = Mathf.Max(2.4f, targetHeight * 2.8f);
            focusLight.intensity = 0f;
            focusLight.color = new Color(0.32f, 0.94f, 1f);
            focusLight.shadows = LightShadows.None;
            focusLight.enabled = false;
        }

        private void UpdateFocusGlow(bool focused, bool failedFocus)
        {
            if (focusLight == null)
            {
                return;
            }

            float targetGlow = focused ? 1f : 0f;
            focusGlow = Mathf.MoveTowards(focusGlow, targetGlow, Time.deltaTime * 3.5f);
            focusLight.enabled = focusGlow > 0.01f;
            focusLight.color = failedFocus ? new Color(1f, 0.42f, 0.32f) : new Color(0.32f, 0.94f, 1f);
            focusLight.intensity = focusGlow * (failedFocus ? 1.35f : 0.95f);
        }

        private void StripImportedControlComponents(GameObject instance)
        {
            MonoBehaviour[] behaviours = instance.GetComponentsInChildren<MonoBehaviour>(true);

            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null)
                {
                    behaviours[i].enabled = false;
                }
            }

            CharacterController[] controllers = instance.GetComponentsInChildren<CharacterController>(true);

            for (int i = 0; i < controllers.Length; i++)
            {
                controllers[i].enabled = false;
            }
        }

        private void ScaleAndGround(GameObject instance)
        {
            Bounds bounds = CalculateRendererBounds(instance);

            if (bounds.size.y > 0.0001f)
            {
                float scale = targetHeight / bounds.size.y;
                instance.transform.localScale *= scale;
            }

            bounds = CalculateRendererBounds(instance);
            float bottomOffset = bounds.min.y - transform.position.y;
            instance.transform.localPosition -= Vector3.up * bottomOffset;
        }

        private static void SetRenderersForRuntime(GameObject instance)
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderers[i].receiveShadows = false;
            }
        }

        private static Bounds CalculateRendererBounds(GameObject instance)
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);

            if (renderers.Length == 0)
            {
                return new Bounds(instance.transform.position, Vector3.one);
            }

            Bounds bounds = renderers[0].bounds;

            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private static GameObject LoadAnimalPrefab(int index)
        {
            if (index < 0 || index >= AnimalPrefabNames.Length)
            {
                return null;
            }

            return Resources.Load<GameObject>($"ThreadAnimals/{AnimalPrefabNames[index]}");
        }

        private static int GetStableIndex(string seed, int count)
        {
            unchecked
            {
                int hash = 23;
                string safeSeed = string.IsNullOrWhiteSpace(seed) ? "forest" : seed;

                for (int i = 0; i < safeSeed.Length; i++)
                {
                    hash = (hash * 31) + safeSeed[i];
                }

                return (hash & 0x7fffffff) % Mathf.Max(1, count);
            }
        }

        private static bool IsRunningState(CodexAnimalAnimationState state)
        {
            return state == CodexAnimalAnimationState.Running ||
                state == CodexAnimalAnimationState.RunningLeft ||
                state == CodexAnimalAnimationState.RunningRight;
        }
    }
}
