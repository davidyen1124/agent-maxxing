using UnityEngine;

namespace Forest
{
    public sealed class ActiveThreadAnimalController : MonoBehaviour
    {
        private const float TerrainGroundOffset = 0.05f;
        private const float TerrainTargetArrivalDistance = 0.95f;
        private const float TerrainSurfaceFollowSpeed = 12f;
        private const float ActiveAnimalHeight = 1.4f;

        private ForestGameDirector director;
        private ThreadAnimalVisual animalVisual;
        private Vector3 velocity;
        private Vector3 roamTarget;
        private Vector3 homePosition;
        private Vector3 chaosImpulse;
        private float roamTimer;
        private float actionTimer;
        private float nextActionTimer;
        private float dashTimer;
        private float nextDashTimer;
        private float dashMultiplier = 1f;
        private float personalitySpeed;
        private float personalityAcceleration;
        private float personalityRoam;
        private float meanderAmplitude;
        private float meanderFrequencyX;
        private float meanderFrequencyZ;
        private float targetMinSeconds;
        private float targetMaxSeconds;
        private CodexAnimalAnimationState actionState;
        private ChaosMoveMode moveMode;
        private float seed;
        private string threadId = string.Empty;
        private string title = "Untitled thread";
        private string statusMessage = "Idle";
        private string phase = "idle";
        private float ageMinutes;

        public string ThreadId => threadId;

        public string Title => title;

        public string StatusMessage => statusMessage;

        public string BubbleMessage => BuildBubbleMessage();

        public Vector3 BubbleAnchorWorldPosition => transform.position + (Vector3.up * (ActiveAnimalHeight + 0.65f));

        public string Phase => phase;

        public Vector3 Velocity => velocity;

        public string AnimalId => animalVisual != null ? animalVisual.AnimalId : "unknown-animal";

        public string AnimalDisplayName => animalVisual != null ? animalVisual.AnimalDisplayName : AnimalId;

        private enum ChaosMoveMode
        {
            Wander,
            OrbitPlayer,
            DivePop,
            Spiral,
            ZigZag,
            ChaseCamera
        }

        public bool Initialize(ForestGameDirector owningDirector, ForestThreadSnapshot snapshot)
        {
            director = owningDirector;
            seed = Random.Range(0f, 100f);
            Vector3 startPosition = snapshot != null && snapshot.position != null
                ? snapshot.position.ToVector3()
                : director.GetRandomRoamingPoint();
            transform.position = ProjectToGround(startPosition);
            homePosition = transform.position;
            roamTarget = transform.position;
            ConfigurePersonality();

            ApplySnapshot(snapshot);

            animalVisual = ThreadAnimalVisual.Create(transform, threadId, ActiveAnimalHeight);

            if (animalVisual == null)
            {
                Debug.LogWarning("[ActiveThreadAnimalController] No 3D animal prefab is available; thread animal will not be created.");
                return false;
            }

            animalVisual.SetState(GetAnimalState(), velocity, ShouldForceRun(), IsImportantPhase(), IsFailurePhase());

            SphereCollider collider = gameObject.AddComponent<SphereCollider>();
            collider.radius = 0.85f;
            collider.center = new Vector3(0f, 0.3f, 0f);
            return true;
        }

        public void ApplySnapshot(ForestThreadSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            threadId = snapshot.id ?? threadId;
            title = string.IsNullOrWhiteSpace(snapshot.title) ? "Untitled thread" : snapshot.title.Trim();
            phase = string.IsNullOrWhiteSpace(snapshot.phase) ? "idle" : snapshot.phase.Trim().ToLowerInvariant();
            statusMessage = string.IsNullOrWhiteSpace(snapshot.statusMessage) ? string.Empty : snapshot.statusMessage.Trim();
            ageMinutes = Mathf.Max(0f, snapshot.ageMinutes);

            if (snapshot.position != null)
            {
                homePosition = ProjectToGround(snapshot.position.ToVector3(), 3f);
            }

            animalVisual?.SetState(GetAnimalState(), velocity, ShouldForceRun(), IsImportantPhase(), IsFailurePhase());
        }

        public ForestThreadSnapshot CreateSnapshot()
        {
            return new ForestThreadSnapshot
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
            roamTimer -= Time.deltaTime;

            UpdateGroundedMovement();
        }

        private void ConfigurePersonality()
        {
            personalitySpeed = Random.Range(0.72f, 1.75f);
            personalityAcceleration = Random.Range(0.75f, 1.9f);
            personalityRoam = Random.Range(0.65f, 1.8f);
            meanderAmplitude = Random.Range(0.5f, 2.8f);
            meanderFrequencyX = Random.Range(0.55f, 2.4f);
            meanderFrequencyZ = Random.Range(0.55f, 2.8f);
            targetMinSeconds = Random.Range(0.18f, 1.1f);
            targetMaxSeconds = Random.Range(0.85f, 3.8f);
            moveMode = (ChaosMoveMode)Random.Range(0, 6);
            roamTimer = Random.Range(0.05f, 2.2f);
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
            }

            if (nextDashTimer <= 0f)
            {
                dashTimer = Random.Range(0.18f, 0.85f);
                dashMultiplier = Random.Range(1.6f, 2.8f);
                nextDashTimer = IsImportantPhase() ? Random.Range(2.8f, 7.5f) : Random.Range(0.7f, 5.5f);
                chaosImpulse = Random.insideUnitSphere * (IsImportantPhase() ? Random.Range(0.8f, 2.4f) : Random.Range(3f, 9f));
                chaosImpulse.y = 0f;
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

                    cameraForward.y = 0f;

                    if (cameraForward.sqrMagnitude < 0.0001f)
                    {
                        cameraForward = Camera.main.transform.forward;
                    }

                    focusPoint = Camera.main.transform.position + (cameraForward.normalized * Random.Range(4f, 11f));
                    circle = Random.insideUnitCircle * Random.Range(1.2f, 5.5f);
                }
                else if (phase == "responding" || phase == "working" || phase == "fresh")
                {
                    focusPoint = Vector3.Lerp(homePosition, playerPosition, 0.65f);
                }
            }

            float verticalOffset = 0f;

            switch (moveMode)
            {
                case ChaosMoveMode.DivePop:
                    verticalOffset = 0f;
                    break;
                case ChaosMoveMode.Spiral:
                    float spiralTime = Time.time * Random.Range(0.7f, 1.6f) + seed;
                    circle += new Vector2(Mathf.Cos(spiralTime), Mathf.Sin(spiralTime)) * Random.Range(2f, 6f);
                    verticalOffset = 0f;
                    break;
                case ChaosMoveMode.ZigZag:
                    circle = new Vector2(Mathf.Sign(Mathf.Sin(Time.time * 7f + seed)) * roamRadius, Random.Range(-roamRadius, roamRadius));
                    verticalOffset = 0f;
                    break;
            }

            Vector3 target = focusPoint + new Vector3(circle.x, verticalOffset, circle.y);
            roamTarget = ProjectToGround(target, 2f);
            roamTimer = Random.Range(targetMinSeconds, Mathf.Max(targetMinSeconds + 0.2f, targetMaxSeconds));
        }

        private float GetRoamSpeed()
        {
            switch (phase)
            {
                case "responding":
                    return 1.8f * personalitySpeed * GetDashMultiplier();
                case "working":
                    return 1.35f * personalitySpeed * GetDashMultiplier();
                case "fresh":
                    return 1.65f * personalitySpeed * GetDashMultiplier();
                default:
                    return 2.2f * personalitySpeed * GetDashMultiplier();
            }
        }

        private float GetAcceleration()
        {
            switch (phase)
            {
                case "responding":
                    return 4.2f * personalityAcceleration * GetDashMultiplier();
                case "working":
                    return 3.4f * personalityAcceleration * GetDashMultiplier();
                default:
                    return 4.8f * personalityAcceleration * GetDashMultiplier();
            }
        }

        private float GetRoamRadius()
        {
            switch (phase)
            {
                case "responding":
                    return 3.2f;
                case "working":
                    return 3.8f;
                default:
                    return 10f;
            }
        }

        private float GetDashMultiplier()
        {
            if (dashTimer <= 0f)
            {
                return 1f;
            }

            return IsImportantPhase() ? Mathf.Lerp(1f, dashMultiplier, 0.25f) : dashMultiplier;
        }

        private void UpdateGroundedMovement()
        {
            Vector3 meander = new Vector3(
                Mathf.Sin((Time.time * meanderFrequencyX) + seed) * meanderAmplitude,
                0f,
                Mathf.Cos((Time.time * meanderFrequencyZ) + seed) * meanderAmplitude * 0.8f);
            Vector3 horizontalOffset = roamTarget + meander - transform.position;
            horizontalOffset.y = 0f;

            if (roamTimer <= 0f || horizontalOffset.sqrMagnitude < TerrainTargetArrivalDistance * TerrainTargetArrivalDistance)
            {
                PickNextTarget();
                horizontalOffset = roamTarget - transform.position;
                horizontalOffset.y = 0f;
            }

            Vector3 horizontalImpulse = new Vector3(chaosImpulse.x, 0f, chaosImpulse.z) * 0.25f;
            Vector3 desiredVelocity = horizontalOffset.sqrMagnitude > 0.0001f
                ? horizontalOffset.normalized * GetRoamSpeed()
                : Vector3.zero;

            MoveGroundedThread(desiredVelocity + horizontalImpulse, GetAcceleration(), 7.2f, 1.4f);
            FacePlayerWhenImportant();
            chaosImpulse = Vector3.Lerp(chaosImpulse, Vector3.zero, Time.deltaTime * 2.2f);
            animalVisual?.SetState(GetAnimalState(), velocity, ShouldForceRun(), IsImportantPhase(), IsFailurePhase());
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

        private Vector3 ProjectToGround(Vector3 point, float clampPadding = 1.4f)
        {
            Vector3 clamped = director.ClampPoint(point, clampPadding);
            clamped.y = director.GetSurfaceY(clamped) + TerrainGroundOffset;
            return clamped;
        }

        private CodexAnimalAnimationState GetAnimalState()
        {
            if (IsFailurePhase())
            {
                return CodexAnimalAnimationState.Failed;
            }

            if (IsImportantPhase())
            {
                switch (phase)
                {
                    case "fresh":
                    case "responding":
                        return CodexAnimalAnimationState.Waving;
                    case "working":
                        return CodexAnimalAnimationState.Review;
                }
            }

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
                case "warning":
                    return CodexAnimalAnimationState.Review;
                case "fresh":
                    return CodexAnimalAnimationState.Jumping;
                case "responding":
                    return CodexAnimalAnimationState.Waving;
                case "working":
                    return CodexAnimalAnimationState.Review;
                case "idle":
                    return velocity.sqrMagnitude > 1.2f ? GetDirectionalRunState() : CodexAnimalAnimationState.Waiting;
                default:
                    return velocity.sqrMagnitude > 1.2f ? GetDirectionalRunState() : CodexAnimalAnimationState.Idle;
            }
        }

        private CodexAnimalAnimationState GetDirectionalRunState()
        {
            Camera camera = Camera.main;

            if (camera == null || velocity.sqrMagnitude < 0.0001f)
            {
                return CodexAnimalAnimationState.Running;
            }

            float screenDirection = Vector3.Dot(velocity.normalized, camera.transform.right);

            if (screenDirection > 0.45f)
            {
                return CodexAnimalAnimationState.RunningRight;
            }

            if (screenDirection < -0.45f)
            {
                return CodexAnimalAnimationState.RunningLeft;
            }

            return CodexAnimalAnimationState.Running;
        }

        private CodexAnimalAnimationState PickRandomActionState()
        {
            int roll = Random.Range(0, 100);

            if (roll < 16)
            {
                return CodexAnimalAnimationState.Jumping;
            }

            if (roll < 31)
            {
                return CodexAnimalAnimationState.Waving;
            }

            if (roll < 45)
            {
                return CodexAnimalAnimationState.Review;
            }

            if (roll < 57)
            {
                return CodexAnimalAnimationState.Waiting;
            }

            if (roll < 82)
            {
                return GetDirectionalRunState();
            }

            return CodexAnimalAnimationState.Idle;
        }

        private string BuildBubbleMessage()
        {
            if (string.Equals(phase, "idle", System.StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(title) ? "Untitled thread" : title.Trim();
            }

            if (!ShouldShowBubbleMessage(statusMessage))
            {
                return string.Empty;
            }

            return statusMessage;
        }

        private bool IsImportantPhase()
        {
            switch (phase)
            {
                case "fresh":
                case "responding":
                case "working":
                    return true;
                default:
                    return IsFailurePhase();
            }
        }

        private bool IsFailurePhase()
        {
            switch (phase)
            {
                case "failed":
                case "failure":
                case "error":
                    return true;
                default:
                    return false;
            }
        }

        private bool ShouldForceRun()
        {
            return dashTimer > 0f && !IsImportantPhase();
        }

        private void FacePlayerWhenImportant()
        {
            if (!IsImportantPhase() || director == null || director.Player == null)
            {
                return;
            }

            Vector3 direction = director.Player.transform.position - transform.position;
            direction.y = 0f;

            if (direction.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 6.5f);
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
