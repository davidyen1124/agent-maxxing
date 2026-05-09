using UnityEngine;

namespace Underwater
{
    public sealed class ThreadPetAI : MonoBehaviour
    {
        private const float TerrainGroundOffset = 0.65f;
        private const float TerrainTargetArrivalDistance = 0.95f;
        private const float TerrainSurfaceFollowSpeed = 12f;

        private UnderwaterGameDirector director;
        private CodexPetSpriteAnimator petAnimator;
        private Transform modelRoot;
        private Vector3 modelBaseLocalPosition;
        private Vector3 velocity;
        private Vector3 swimTarget;
        private Vector3 homePosition;
        private Vector3 chaosImpulse;
        private float swimTimer;
        private float actionTimer;
        private float nextActionTimer;
        private float dashTimer;
        private float nextDashTimer;
        private float dashMultiplier = 1f;
        private float personalitySpeed;
        private float personalityAcceleration;
        private float personalityRoam;
        private float waveAmplitude;
        private float waveFrequencyX;
        private float waveFrequencyY;
        private float waveFrequencyZ;
        private float targetMinSeconds;
        private float targetMaxSeconds;
        private CodexPetAnimationState actionState;
        private ChaosMoveMode moveMode;
        private float seed;
        private string threadId = string.Empty;
        private string title = "Untitled thread";
        private string statusMessage = "Idle";
        private string phase = "idle";
        private float ageMinutes;
        private CodexPetDefinition petDefinition;

        public string ThreadId => threadId;

        public string Title => title;

        public string StatusMessage => statusMessage;

        public string BubbleMessage => BuildBubbleMessage();

        public string Phase => phase;

        public Vector3 Velocity => velocity;

        public string PetId => petDefinition != null && !string.IsNullOrWhiteSpace(petDefinition.Id) ? petDefinition.Id : "unknown-pet";

        public string PetDisplayName => petDefinition != null && !string.IsNullOrWhiteSpace(petDefinition.DisplayName) ? petDefinition.DisplayName : PetId;

        private enum ChaosMoveMode
        {
            Wander,
            OrbitPlayer,
            DivePop,
            Spiral,
            ZigZag,
            ChaseCamera
        }

        public bool Initialize(UnderwaterGameDirector owningDirector, AquariumThreadSnapshot snapshot)
        {
            director = owningDirector;
            seed = Random.Range(0f, 100f);
            Vector3 startPosition = snapshot != null && snapshot.position != null
                ? snapshot.position.ToVector3()
                : director.GetRandomMidWaterPoint();
            transform.position = ShouldUseTerrainGrounding()
                ? ProjectToTerrainGround(startPosition)
                : startPosition;
            homePosition = transform.position;
            swimTarget = transform.position;
            ConfigurePersonality();

            ApplySnapshot(snapshot);

            CodexPetDefinition pet = CodexPetCatalog.Shared.GetPetForSeed(threadId);

            if (pet == null)
            {
                Debug.LogWarning("[ThreadPetAI] No Codex pet atlas is available; thread pet will not be created.");
                return false;
            }

            petDefinition = pet;
            modelRoot = new GameObject("Model").transform;
            modelRoot.SetParent(transform);
            modelRoot.localPosition = Vector3.zero;
            modelRoot.localRotation = Quaternion.identity;
            modelBaseLocalPosition = modelRoot.localPosition;

            petAnimator = CodexPetSpriteAnimator.Create(
                modelRoot,
                pet,
                GetPetState(),
                new Vector3(0f, 0.65f, 0f),
                2.6f,
                $"Pet Sprite ({pet.DisplayName})");
            petAnimator.SetPlaybackSpeed(Random.Range(0.72f, 1.85f));
            petAnimator.RandomizePlayback(Random.value);

            SphereCollider collider = gameObject.AddComponent<SphereCollider>();
            collider.radius = 0.85f;
            collider.center = new Vector3(0f, 0.3f, 0f);
            return true;
        }

        public void ApplySnapshot(AquariumThreadSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            threadId = snapshot.id ?? threadId;
            title = string.IsNullOrWhiteSpace(snapshot.title) ? "Untitled thread" : snapshot.title.Trim();
            phase = string.IsNullOrWhiteSpace(snapshot.phase) ? "idle" : snapshot.phase.Trim().ToLowerInvariant();
            statusMessage = string.IsNullOrWhiteSpace(snapshot.statusMessage) ? BuildFallbackStatusMessage(phase) : snapshot.statusMessage.Trim();
            ageMinutes = Mathf.Max(0f, snapshot.ageMinutes);

            if (snapshot.position != null)
            {
                homePosition = ShouldUseTerrainGrounding()
                    ? ProjectToTerrainGround(snapshot.position.ToVector3(), 3f)
                    : director.ClampPoint(snapshot.position.ToVector3(), 3f);
            }

            petAnimator?.SetState(GetPetState());
        }

        public AquariumThreadSnapshot CreateSnapshot()
        {
            return new AquariumThreadSnapshot
            {
                id = threadId,
                title = title,
                statusMessage = statusMessage,
                phase = phase,
                source = "world",
                ageMinutes = ageMinutes,
                position = SerializableVector3.FromVector3(transform.position),
                velocity = SerializableVector3.FromVector3(velocity)
            };
        }

        private void Update()
        {
            UpdateChaosTimers();
            swimTimer -= Time.deltaTime;

            if (ShouldUseTerrainGrounding())
            {
                UpdateGroundedMovement();
                return;
            }

            if (swimTimer <= 0f || Vector3.Distance(transform.position, swimTarget) < 1.8f)
            {
                PickNextTarget();
            }

            Vector3 wave = new Vector3(
                Mathf.Sin((Time.time * waveFrequencyX) + seed) * waveAmplitude,
                Mathf.Sin((Time.time * waveFrequencyY) + (seed * 0.7f)) * waveAmplitude * 0.55f,
                Mathf.Cos((Time.time * waveFrequencyZ) + seed) * waveAmplitude * 0.8f);
            Vector3 desiredVelocity = (swimTarget + wave + chaosImpulse - transform.position).normalized * GetSwimSpeed();
            float buoyancyTarget = Mathf.Clamp(homePosition.y + GetVerticalBias(), director.SeaFloorY + 2f, director.PlayBounds.max.y - 2f);

            AddBuoyancyBand(buoyancyTarget, 0.22f);
            MoveThread(desiredVelocity, GetAcceleration(), 4.1f, 1.4f);
            chaosImpulse = Vector3.Lerp(chaosImpulse, Vector3.zero, Time.deltaTime * 2.2f);
            petAnimator?.SetState(GetPetState());
        }

        private void ConfigurePersonality()
        {
            personalitySpeed = Random.Range(0.72f, 1.75f);
            personalityAcceleration = Random.Range(0.75f, 1.9f);
            personalityRoam = Random.Range(0.65f, 1.8f);
            waveAmplitude = Random.Range(0.5f, 2.8f);
            waveFrequencyX = Random.Range(0.55f, 2.4f);
            waveFrequencyY = Random.Range(0.45f, 2.1f);
            waveFrequencyZ = Random.Range(0.55f, 2.8f);
            targetMinSeconds = Random.Range(0.18f, 1.1f);
            targetMaxSeconds = Random.Range(0.85f, 3.8f);
            moveMode = (ChaosMoveMode)Random.Range(0, 6);
            swimTimer = Random.Range(0.05f, 2.2f);
            nextActionTimer = Random.Range(0.15f, 3.8f);
            nextDashTimer = Random.Range(0.2f, 4.2f);
        }

        private void UpdateChaosTimers()
        {
            actionTimer -= Time.deltaTime;
            nextActionTimer -= Time.deltaTime;
            dashTimer -= Time.deltaTime;
            nextDashTimer -= Time.deltaTime;

            if (nextActionTimer <= 0f)
            {
                actionState = PickRandomActionState();
                actionTimer = Random.Range(0.35f, 2.2f);
                nextActionTimer = Random.Range(0.25f, 4.5f);
                petAnimator?.SetPlaybackSpeed(Random.Range(0.6f, 2.2f));
                petAnimator?.RandomizePlayback(Random.value);
            }

            if (nextDashTimer <= 0f)
            {
                dashTimer = Random.Range(0.18f, 0.85f);
                dashMultiplier = Random.Range(1.6f, 2.8f);
                nextDashTimer = Random.Range(0.7f, 5.5f);
                chaosImpulse = Random.insideUnitSphere * Random.Range(3f, 9f);
                chaosImpulse.y = ShouldUseTerrainGrounding() ? 0f : Random.Range(-2.5f, 4.5f);
                moveMode = (ChaosMoveMode)Random.Range(0, 6);
                PickNextTarget();
            }
        }

        private void PickNextTarget()
        {
            float roamRadius = GetRoamRadius() * personalityRoam;
            Vector2 circle = Random.insideUnitCircle * roamRadius;
            Vector3 focusPoint = homePosition;

            if (director.Player != null)
            {
                Vector3 playerPosition = director.Player.transform.position;

                if (moveMode == ChaosMoveMode.OrbitPlayer)
                {
                    focusPoint = playerPosition;
                    float angle = Random.Range(0f, Mathf.PI * 2f);
                    circle = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Random.Range(2.5f, 9f);
                }
                else if (moveMode == ChaosMoveMode.ChaseCamera && Camera.main != null)
                {
                    Vector3 cameraForward = Camera.main.transform.forward;

                    if (ShouldUseTerrainGrounding())
                    {
                        cameraForward.y = 0f;

                        if (cameraForward.sqrMagnitude < 0.0001f)
                        {
                            cameraForward = Camera.main.transform.forward;
                        }
                    }

                    focusPoint = Camera.main.transform.position + (cameraForward.normalized * Random.Range(4f, 11f));
                    circle = Random.insideUnitCircle * Random.Range(1.2f, 5.5f);
                }
                else if (phase == "responding" || phase == "fresh")
                {
                    focusPoint = Vector3.Lerp(homePosition, playerPosition, 0.45f);
                }
            }

            float verticalOffset = Random.Range(-2f, 2f);

            switch (moveMode)
            {
                case ChaosMoveMode.DivePop:
                    verticalOffset = ShouldUseTerrainGrounding()
                        ? 0f
                        : Random.value > 0.5f ? Random.Range(3f, 7f) : Random.Range(-5f, -1.5f);
                    break;
                case ChaosMoveMode.Spiral:
                    float spiralTime = Time.time * Random.Range(0.7f, 1.6f) + seed;
                    circle += new Vector2(Mathf.Cos(spiralTime), Mathf.Sin(spiralTime)) * Random.Range(2f, 6f);
                    verticalOffset = ShouldUseTerrainGrounding()
                        ? 0f
                        : Mathf.Sin(spiralTime * 1.3f) * Random.Range(1.5f, 4f);
                    break;
                case ChaosMoveMode.ZigZag:
                    circle = new Vector2(Mathf.Sign(Mathf.Sin(Time.time * 7f + seed)) * roamRadius, Random.Range(-roamRadius, roamRadius));
                    verticalOffset = ShouldUseTerrainGrounding() ? 0f : Random.Range(-3.5f, 3.5f);
                    break;
            }

            Vector3 target = focusPoint + new Vector3(circle.x, verticalOffset, circle.y);
            swimTarget = ShouldUseTerrainGrounding()
                ? ProjectToTerrainGround(target, 2f)
                : director.ClampPoint(target, 2f);
            swimTimer = Random.Range(targetMinSeconds, Mathf.Max(targetMinSeconds + 0.2f, targetMaxSeconds));
        }

        private float GetSwimSpeed()
        {
            switch (phase)
            {
                case "responding":
                    return 4.4f * personalitySpeed * GetDashMultiplier();
                case "working":
                    return 3.3f * personalitySpeed * GetDashMultiplier();
                case "fresh":
                    return 3.8f * personalitySpeed * GetDashMultiplier();
                default:
                    return 2.2f * personalitySpeed * GetDashMultiplier();
            }
        }

        private float GetAcceleration()
        {
            switch (phase)
            {
                case "responding":
                    return 8.6f * personalityAcceleration * GetDashMultiplier();
                case "working":
                    return 6.2f * personalityAcceleration * GetDashMultiplier();
                default:
                    return 4.8f * personalityAcceleration * GetDashMultiplier();
            }
        }

        private float GetRoamRadius()
        {
            switch (phase)
            {
                case "responding":
                    return 5f;
                case "working":
                    return 7f;
                default:
                    return 10f;
            }
        }

        private float GetDashMultiplier()
        {
            return dashTimer > 0f ? dashMultiplier : 1f;
        }

        private float GetVerticalBias()
        {
            switch (phase)
            {
                case "responding":
                    return 2.6f;
                case "working":
                    return 1.3f;
                default:
                    return 0.2f;
            }
        }

        private void AddBuoyancyBand(float centerHeight, float strength)
        {
            float offset = centerHeight - transform.position.y;
            velocity += Vector3.up * offset * strength * Time.deltaTime;
        }

        private void MoveThread(Vector3 desiredVelocity, float acceleration, float turnSpeed, float clampPadding)
        {
            velocity = Vector3.MoveTowards(velocity, desiredVelocity, acceleration * Time.deltaTime);
            transform.position += velocity * Time.deltaTime;
            transform.position = director.ClampPoint(transform.position, clampPadding);

            if (velocity.sqrMagnitude > 0.06f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * turnSpeed);
            }
        }

        private void UpdateGroundedMovement()
        {
            Vector3 horizontalOffset = swimTarget - transform.position;
            horizontalOffset.y = 0f;

            if (swimTimer <= 0f || horizontalOffset.sqrMagnitude < TerrainTargetArrivalDistance * TerrainTargetArrivalDistance)
            {
                PickNextTarget();
                horizontalOffset = swimTarget - transform.position;
                horizontalOffset.y = 0f;
            }

            Vector3 horizontalImpulse = new Vector3(chaosImpulse.x, 0f, chaosImpulse.z) * 0.25f;
            Vector3 desiredVelocity = horizontalOffset.sqrMagnitude > 0.0001f
                ? horizontalOffset.normalized * GetSwimSpeed()
                : Vector3.zero;

            MoveGroundedThread(desiredVelocity + horizontalImpulse, GetAcceleration(), 7.2f, 1.4f);
            chaosImpulse = Vector3.Lerp(chaosImpulse, Vector3.zero, Time.deltaTime * 2.2f);
            UpdateGroundedModelMotion();
            petAnimator?.SetState(GetPetState());
        }

        private void MoveGroundedThread(Vector3 desiredVelocity, float acceleration, float turnSpeed, float clampPadding)
        {
            desiredVelocity.y = 0f;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, desiredVelocity, acceleration * Time.deltaTime);
            velocity = horizontalVelocity;

            Vector3 nextPosition = transform.position + (velocity * Time.deltaTime);
            nextPosition = director.ClampPoint(nextPosition, clampPadding);
            float groundY = director.GetSurfaceY(nextPosition) + TerrainGroundOffset;
            nextPosition.y = Mathf.Abs(nextPosition.y - groundY) > 2f
                ? groundY
                : Mathf.Lerp(nextPosition.y, groundY, Time.deltaTime * TerrainSurfaceFollowSpeed);
            transform.position = nextPosition;

            if (velocity.sqrMagnitude > 0.06f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * turnSpeed);
            }
        }

        private void UpdateGroundedModelMotion()
        {
            if (modelRoot == null)
            {
                return;
            }

            float bob = actionState == CodexPetAnimationState.Jumping && actionTimer > 0f
                ? Mathf.Abs(Mathf.Sin(Time.time * 9f + seed)) * 0.24f
                : Mathf.Sin(Time.time * 5.5f + seed) * Mathf.Clamp01(velocity.magnitude / 3f) * 0.04f;
            modelRoot.localPosition = Vector3.Lerp(
                modelRoot.localPosition,
                modelBaseLocalPosition + (Vector3.up * bob),
                Time.deltaTime * 8f);
        }

        private Vector3 ProjectToTerrainGround(Vector3 point, float clampPadding = 1.4f)
        {
            Vector3 clamped = director.ClampPoint(point, clampPadding);
            clamped.y = director.GetSurfaceY(clamped) + TerrainGroundOffset;
            return clamped;
        }

        private bool ShouldUseTerrainGrounding()
        {
            return director != null && director.UsesSceneTerrain;
        }

        private CodexPetAnimationState GetPetState()
        {
            if (actionTimer > 0f)
            {
                return actionState;
            }

            if (dashTimer > 0f)
            {
                return GetDirectionalRunState();
            }

            switch (phase)
            {
                case "failed":
                case "failure":
                case "error":
                case "warning":
                    return CodexPetAnimationState.Failed;
                case "fresh":
                    return CodexPetAnimationState.Jumping;
                case "responding":
                    return CodexPetAnimationState.Waving;
                case "working":
                    return CodexPetAnimationState.Review;
                case "idle":
                    return velocity.sqrMagnitude > 1.2f ? GetDirectionalRunState() : CodexPetAnimationState.Waiting;
                default:
                    return velocity.sqrMagnitude > 1.2f ? GetDirectionalRunState() : CodexPetAnimationState.Idle;
            }
        }

        private CodexPetAnimationState GetDirectionalRunState()
        {
            Camera camera = Camera.main;

            if (camera == null || velocity.sqrMagnitude < 0.0001f)
            {
                return CodexPetAnimationState.Running;
            }

            float screenDirection = Vector3.Dot(velocity.normalized, camera.transform.right);

            if (screenDirection > 0.45f)
            {
                return CodexPetAnimationState.RunningRight;
            }

            if (screenDirection < -0.45f)
            {
                return CodexPetAnimationState.RunningLeft;
            }

            return CodexPetAnimationState.Running;
        }

        private CodexPetAnimationState PickRandomActionState()
        {
            int roll = Random.Range(0, 100);

            if (roll < 16)
            {
                return CodexPetAnimationState.Jumping;
            }

            if (roll < 31)
            {
                return CodexPetAnimationState.Waving;
            }

            if (roll < 45)
            {
                return CodexPetAnimationState.Review;
            }

            if (roll < 57)
            {
                return CodexPetAnimationState.Waiting;
            }

            if (roll < 66)
            {
                return CodexPetAnimationState.Failed;
            }

            if (roll < 82)
            {
                return GetDirectionalRunState();
            }

            return CodexPetAnimationState.Idle;
        }

        private static string BuildFallbackStatusMessage(string phase)
        {
            switch ((phase ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "failed":
                case "failure":
                case "error":
                case "warning":
                    return "Blocked";
                case "fresh":
                case "responding":
                case "working":
                    return "Thinking";
                case "idle":
                    return "Idle";
                default:
                    return "Info";
            }
        }

        private string BuildBubbleMessage()
        {
            if (!ShouldShowBubbleMessage(statusMessage))
            {
                return string.Empty;
            }

            return ShouldShowTitleProgress(statusMessage) ? BuildTitleProgressMessage() : statusMessage;
        }

        private string BuildTitleProgressMessage()
        {
            string label = string.IsNullOrWhiteSpace(title) ? "Untitled thread" : title.Trim();
            const int maxLength = 64;

            if (label.EndsWith("...", System.StringComparison.Ordinal))
            {
                return label.Length <= maxLength ? label : label.Substring(0, maxLength);
            }

            int maxTitleLength = Mathf.Max(0, maxLength - 3);

            if (label.Length > maxTitleLength)
            {
                label = label.Substring(0, maxTitleLength).TrimEnd('.', ' ');
            }

            return label + "...";
        }

        private static bool ShouldShowTitleProgress(string message)
        {
            switch ((message ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "thinking":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ShouldShowBubbleMessage(string message)
        {
            switch ((message ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "idle":
                case "info":
                    return false;
                default:
                    return true;
            }
        }
    }
}
