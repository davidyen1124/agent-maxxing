using UnityEngine;

namespace Forest
{
    public sealed class ArchivedThreadAnimalController : MonoBehaviour
    {
        private const float ArchivedAnimalHeight = 1f;
        private const float TerrainGroundOffset = 0.05f;

        private ThreadAnimalVisual animalVisual;
        private ForestGameDirector director;
        private Vector3 basePosition;
        private float seed;
        private float nextActionTimer;
        private float actionTimer;
        private float hopTimer;
        private float idleBobFrequency;
        private CodexAnimalAnimationState actionState = CodexAnimalAnimationState.Waiting;
        private string threadId = string.Empty;
        private string title = "Untitled thread";
        private string statusMessage = "Archived";

        public string ThreadId => threadId;

        public string Title => title;

        public string StatusMessage => statusMessage;

        public string AnimalId => animalVisual != null ? animalVisual.AnimalId : "unknown-animal";

        public string AnimalDisplayName => animalVisual != null ? animalVisual.AnimalDisplayName : AnimalId;

        public bool Initialize(ForestGameDirector director, ForestArchivedThreadSnapshot snapshot)
        {
            this.director = director;
            threadId = snapshot != null ? snapshot.id ?? string.Empty : string.Empty;
            title = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.title) ? snapshot.title.Trim() : "Untitled thread";
            statusMessage = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.statusMessage) ? snapshot.statusMessage.Trim() : "Archived";

            Vector3 position = snapshot != null && snapshot.position != null
                ? snapshot.position.ToVector3()
                : director.GetRandomGroundPoint(6f);

            transform.position = new Vector3(position.x, director.GetSurfaceY(position) + TerrainGroundOffset, position.z);
            basePosition = transform.position;
            seed = Random.Range(0f, 100f);
            nextActionTimer = Random.Range(0.1f, 5f);
            hopTimer = Random.Range(0f, 1.5f);
            idleBobFrequency = Random.Range(0.8f, 1.7f);
            animalVisual = ThreadAnimalVisual.Create(transform, threadId, ArchivedAnimalHeight);

            if (animalVisual == null)
            {
                Debug.LogWarning("[ArchivedThreadAnimalController] No 3D animal prefab is available; archived animal will not be created.");
                return false;
            }

            animalVisual.SetState(CodexAnimalAnimationState.Waiting, Vector3.zero, false);
            return true;
        }

        public ForestArchivedThreadSnapshot CreateSnapshot()
        {
            return new ForestArchivedThreadSnapshot
            {
                id = threadId,
                title = title,
                statusMessage = statusMessage,
                position = SerializableVector3.FromVector3(transform.position)
            };
        }

        private void Update()
        {
            nextActionTimer -= Time.deltaTime;
            actionTimer -= Time.deltaTime;
            hopTimer -= Time.deltaTime;

            if (nextActionTimer <= 0f)
            {
                actionState = PickRandomState();
                actionTimer = Random.Range(0.45f, 2.4f);
                nextActionTimer = Random.Range(0.35f, 6.5f);
                animalVisual?.SetState(actionState, Vector3.zero, false);
            }

            if (hopTimer <= 0f)
            {
                hopTimer = Random.Range(0.35f, 2.8f);
                basePosition += new Vector3(Random.Range(-0.25f, 0.25f), 0f, Random.Range(-0.25f, 0.25f));

                if (director != null)
                {
                    basePosition = director.ClampPoint(basePosition, 3f);
                    basePosition.y = director.GetSurfaceY(basePosition) + TerrainGroundOffset;
                }
            }

            float bob = actionState == CodexAnimalAnimationState.Jumping && actionTimer > 0f
                ? Mathf.Abs(Mathf.Sin(Time.time * 10f + seed)) * 0.55f
                : Mathf.Sin(Time.time * idleBobFrequency + seed) * 0.08f;
            transform.position = Vector3.Lerp(transform.position, basePosition + Vector3.up * bob, Time.deltaTime * 7f);

            if (actionTimer > 0f)
            {
                animalVisual?.SetState(actionState, Vector3.zero, false);
            }
            else
            {
                animalVisual?.SetState(CodexAnimalAnimationState.Waiting, Vector3.zero, false);
            }
        }

        private static CodexAnimalAnimationState PickRandomState()
        {
            int roll = Random.Range(0, 100);

            if (roll < 22)
            {
                return CodexAnimalAnimationState.Waving;
            }

            if (roll < 42)
            {
                return CodexAnimalAnimationState.Jumping;
            }

            if (roll < 58)
            {
                return CodexAnimalAnimationState.Review;
            }

            if (roll < 72)
            {
                return CodexAnimalAnimationState.Idle;
            }

            if (roll < 86)
            {
                return CodexAnimalAnimationState.Failed;
            }

            return CodexAnimalAnimationState.Waiting;
        }
    }
}
