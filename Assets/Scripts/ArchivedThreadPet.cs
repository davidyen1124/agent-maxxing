using UnityEngine;

namespace Underwater
{
    public sealed class ArchivedThreadPet : MonoBehaviour
    {
        private CodexPetSpriteAnimator petAnimator;
        private UnderwaterGameDirector director;
        private Vector3 basePosition;
        private float seed;
        private float nextActionTimer;
        private float actionTimer;
        private float hopTimer;
        private float idleBobFrequency;
        private CodexPetAnimationState actionState = CodexPetAnimationState.Waiting;
        private string threadId = string.Empty;
        private string title = "Untitled thread";
        private string statusMessage = "Archived";

        public string ThreadId => threadId;

        public string Title => title;

        public string StatusMessage => statusMessage;

        public bool Initialize(UnderwaterGameDirector director, AquariumArchivedPetSnapshot snapshot)
        {
            this.director = director;
            threadId = snapshot != null ? snapshot.id ?? string.Empty : string.Empty;
            title = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.title) ? snapshot.title.Trim() : "Untitled thread";
            statusMessage = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.statusMessage) ? snapshot.statusMessage.Trim() : "Archived";

            CodexPetDefinition pet = CodexPetCatalog.Shared.GetPetForSeed(threadId);

            if (pet == null)
            {
                Debug.LogWarning("[ArchivedThreadPet] No Codex pet atlas is available; archived pet will not be created.");
                return false;
            }

            Vector3 position = snapshot != null && snapshot.position != null
                ? snapshot.position.ToVector3()
                : director.GetRandomSeafloorPoint(6f);

            transform.position = new Vector3(position.x, director.SeaFloorY + 0.18f, position.z);
            basePosition = transform.position;
            seed = Random.Range(0f, 100f);
            nextActionTimer = Random.Range(0.1f, 5f);
            hopTimer = Random.Range(0f, 1.5f);
            idleBobFrequency = Random.Range(0.8f, 1.7f);
            petAnimator = CodexPetSpriteAnimator.Create(
                transform,
                pet,
                CodexPetAnimationState.Waiting,
                new Vector3(0f, 1.05f, 0f),
                2.1f,
                $"Archived Pet Sprite ({pet.DisplayName})");
            petAnimator.SetPlaybackSpeed(Random.Range(0.55f, 1.65f));
            petAnimator.RandomizePlayback(Random.value);
            return true;
        }

        public AquariumArchivedPetSnapshot CreateSnapshot()
        {
            return new AquariumArchivedPetSnapshot
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
                petAnimator?.SetPlaybackSpeed(Random.Range(0.5f, 2f));
                petAnimator?.RandomizePlayback(Random.value);
            }

            if (hopTimer <= 0f)
            {
                hopTimer = Random.Range(0.35f, 2.8f);
                basePosition += new Vector3(Random.Range(-0.25f, 0.25f), 0f, Random.Range(-0.25f, 0.25f));

                if (director != null)
                {
                    basePosition = director.ClampPoint(basePosition, 3f);
                    basePosition.y = director.SeaFloorY + 0.18f;
                }
            }

            float bob = actionState == CodexPetAnimationState.Jumping && actionTimer > 0f
                ? Mathf.Abs(Mathf.Sin(Time.time * 10f + seed)) * 0.55f
                : Mathf.Sin(Time.time * idleBobFrequency + seed) * 0.08f;
            transform.position = Vector3.Lerp(transform.position, basePosition + Vector3.up * bob, Time.deltaTime * 7f);

            if (actionTimer > 0f)
            {
                petAnimator?.SetState(actionState);
            }
            else
            {
                petAnimator?.SetState(CodexPetAnimationState.Waiting);
            }
        }

        private static CodexPetAnimationState PickRandomState()
        {
            int roll = Random.Range(0, 100);

            if (roll < 22)
            {
                return CodexPetAnimationState.Waving;
            }

            if (roll < 42)
            {
                return CodexPetAnimationState.Jumping;
            }

            if (roll < 58)
            {
                return CodexPetAnimationState.Review;
            }

            if (roll < 72)
            {
                return CodexPetAnimationState.Idle;
            }

            if (roll < 86)
            {
                return CodexPetAnimationState.Failed;
            }

            return CodexPetAnimationState.Waiting;
        }
    }
}
