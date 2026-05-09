using UnityEngine;

namespace Underwater
{
    public sealed class ThreadLobsterAI : MonoBehaviour
    {
        private UnderwaterGameDirector director;
        private Transform modelRoot;
        private Transform leftClaw;
        private Transform rightClaw;
        private Transform tailFan;
        private Renderer[] renderers;
        private Vector3 velocity;
        private Vector3 swimTarget;
        private Vector3 homePosition;
        private float swimTimer;
        private float seed;
        private string threadId = string.Empty;
        private string title = "Untitled thread";
        private string phase = "idle";
        private float ageMinutes;

        public string ThreadId => threadId;

        public string Title => title;

        public string Phase => phase;

        public Vector3 Velocity => velocity;

        public void Initialize(
            UnderwaterGameDirector owningDirector,
            AquariumThreadSnapshot snapshot,
            Material bodyMaterial,
            Material accentMaterial)
        {
            director = owningDirector;
            seed = Random.Range(0f, 100f);
            transform.position = snapshot != null && snapshot.position != null
                ? snapshot.position.ToVector3()
                : director.GetRandomMidWaterPoint();
            homePosition = transform.position;
            swimTarget = transform.position;

            BuildVisuals(bodyMaterial, accentMaterial);
            ApplySnapshot(snapshot);

            SphereCollider collider = gameObject.AddComponent<SphereCollider>();
            collider.radius = 0.85f;
            collider.center = new Vector3(0f, 0.3f, 0f);
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
            ageMinutes = Mathf.Max(0f, snapshot.ageMinutes);

            if (snapshot.position != null)
            {
                homePosition = director.ClampPoint(snapshot.position.ToVector3(), 3f);
            }

            UpdateTint();
        }

        public AquariumThreadSnapshot CreateSnapshot()
        {
            return new AquariumThreadSnapshot
            {
                id = threadId,
                title = title,
                phase = phase,
                source = "world",
                ageMinutes = ageMinutes,
                position = SerializableVector3.FromVector3(transform.position),
                velocity = SerializableVector3.FromVector3(velocity)
            };
        }

        private void Update()
        {
            swimTimer -= Time.deltaTime;

            if (swimTimer <= 0f || Vector3.Distance(transform.position, swimTarget) < 1.8f)
            {
                PickNextTarget();
            }

            Vector3 wave = new Vector3(
                Mathf.Sin(Time.time * 0.8f + seed) * 1.2f,
                Mathf.Sin(Time.time * 0.65f + (seed * 0.7f)) * 0.7f,
                Mathf.Cos(Time.time * 0.72f + seed) * 1.0f);
            Vector3 desiredVelocity = (swimTarget + wave - transform.position).normalized * GetSwimSpeed();
            float buoyancyTarget = Mathf.Clamp(homePosition.y + GetVerticalBias(), director.SeaFloorY + 2f, director.PlayBounds.max.y - 2f);

            AddBuoyancyBand(buoyancyTarget, 0.22f);
            MoveThread(desiredVelocity, GetAcceleration(), 4.1f, 1.4f);
            AnimateVisuals();
        }

        private void PickNextTarget()
        {
            float roamRadius = GetRoamRadius();
            Vector2 circle = Random.insideUnitCircle * roamRadius;
            Vector3 focusPoint = homePosition;

            if (director.Player != null)
            {
                Vector3 playerPosition = director.Player.transform.position;

                if (phase == "responding" || phase == "fresh")
                {
                    focusPoint = Vector3.Lerp(homePosition, playerPosition, 0.45f);
                }
            }

            swimTarget = director.ClampPoint(focusPoint + new Vector3(circle.x, Random.Range(-2f, 2f), circle.y), 2f);
            swimTimer = Random.Range(0.8f, 2.4f);
        }

        private float GetSwimSpeed()
        {
            switch (phase)
            {
                case "responding":
                    return 4.4f;
                case "working":
                    return 3.3f;
                case "fresh":
                    return 3.8f;
                default:
                    return 2.2f;
            }
        }

        private float GetAcceleration()
        {
            switch (phase)
            {
                case "responding":
                    return 8.6f;
                case "working":
                    return 6.2f;
                default:
                    return 4.8f;
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

        private void UpdateTint()
        {
            if (renderers == null)
            {
                return;
            }

            Color tint;

            switch (phase)
            {
                case "responding":
                    tint = new Color(1f, 0.82f, 0.54f);
                    break;
                case "working":
                    tint = new Color(0.96f, 0.6f, 0.34f);
                    break;
                case "fresh":
                    tint = new Color(0.92f, 0.72f, 0.44f);
                    break;
                default:
                    tint = new Color(0.72f, 0.44f, 0.26f);
                    break;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                Material material = renderer != null ? renderer.material : null;

                if (material == null)
                {
                    continue;
                }

                if (material.HasProperty("_BaseColor"))
                {
                    Color baseColor = material.GetColor("_BaseColor");
                    material.SetColor("_BaseColor", Color.Lerp(baseColor, tint, 0.34f));
                }

                if (material.HasProperty("_Color"))
                {
                    Color color = material.GetColor("_Color");
                    material.SetColor("_Color", Color.Lerp(color, tint, 0.34f));
                }
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

        private void BuildVisuals(Material bodyMaterial, Material accentMaterial)
        {
            modelRoot = new GameObject("Model").transform;
            modelRoot.SetParent(transform);
            modelRoot.localPosition = Vector3.zero;
            modelRoot.localRotation = Quaternion.identity;

            Material shellMaterial = new Material(bodyMaterial);
            Material highlightMaterial = new Material(accentMaterial);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(modelRoot);
            body.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            body.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            body.transform.localScale = new Vector3(0.65f, 0.45f, 1.55f);
            Destroy(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().material = shellMaterial;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(modelRoot);
            head.transform.localPosition = new Vector3(0.95f, 0.36f, 0f);
            head.transform.localScale = new Vector3(0.52f, 0.44f, 0.44f);
            Destroy(head.GetComponent<Collider>());
            head.GetComponent<Renderer>().material = highlightMaterial;

            GameObject leftClawObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftClawObject.name = "Left Claw";
            leftClawObject.transform.SetParent(modelRoot);
            leftClawObject.transform.localPosition = new Vector3(0.9f, 0.24f, -0.62f);
            leftClawObject.transform.localRotation = Quaternion.Euler(14f, 18f, 0f);
            leftClawObject.transform.localScale = new Vector3(0.82f, 0.16f, 0.22f);
            Destroy(leftClawObject.GetComponent<Collider>());
            leftClawObject.GetComponent<Renderer>().material = highlightMaterial;
            leftClaw = leftClawObject.transform;

            GameObject rightClawObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightClawObject.name = "Right Claw";
            rightClawObject.transform.SetParent(modelRoot);
            rightClawObject.transform.localPosition = new Vector3(0.9f, 0.24f, 0.62f);
            rightClawObject.transform.localRotation = Quaternion.Euler(-14f, -18f, 0f);
            rightClawObject.transform.localScale = new Vector3(0.82f, 0.16f, 0.22f);
            Destroy(rightClawObject.GetComponent<Collider>());
            rightClawObject.GetComponent<Renderer>().material = highlightMaterial;
            rightClaw = rightClawObject.transform;

            GameObject tailObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tailObject.name = "Tail Fan";
            tailObject.transform.SetParent(modelRoot);
            tailObject.transform.localPosition = new Vector3(-1.02f, 0.2f, 0f);
            tailObject.transform.localRotation = Quaternion.identity;
            tailObject.transform.localScale = new Vector3(0.35f, 0.22f, 0.86f);
            Destroy(tailObject.GetComponent<Collider>());
            tailObject.GetComponent<Renderer>().material = shellMaterial;
            tailFan = tailObject.transform;

            for (int i = 0; i < 6; i++)
            {
                float side = i % 2 == 0 ? -1f : 1f;
                float row = i / 2f;

                GameObject leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                leg.name = $"Leg {i + 1}";
                leg.transform.SetParent(modelRoot);
                leg.transform.localPosition = new Vector3(0.35f - row * 0.45f, 0.02f, side * (0.42f + row * 0.08f));
                leg.transform.localRotation = Quaternion.Euler(0f, 0f, side * 30f);
                leg.transform.localScale = new Vector3(0.45f, 0.05f, 0.06f);
                Destroy(leg.GetComponent<Collider>());
                leg.GetComponent<Renderer>().material = shellMaterial;
            }

            renderers = modelRoot.GetComponentsInChildren<Renderer>();
        }

        private void AnimateVisuals()
        {
            float clawWave = Mathf.Sin(Time.time * 8.2f + seed) * 18f;
            float tailWave = Mathf.Sin(Time.time * 9.5f + seed * 1.4f) * 12f;
            float bodyTilt = Mathf.Clamp(Vector3.Dot(velocity.normalized, transform.right) * -8f, -8f, 8f);

            modelRoot.localRotation = Quaternion.Euler(0f, 0f, bodyTilt);
            leftClaw.localRotation = Quaternion.Euler(14f + clawWave, 18f, 0f);
            rightClaw.localRotation = Quaternion.Euler(-14f - clawWave, -18f, 0f);
            tailFan.localRotation = Quaternion.Euler(0f, tailWave, 0f);
        }
    }
}
