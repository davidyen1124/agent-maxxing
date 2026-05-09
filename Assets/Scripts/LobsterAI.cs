using UnityEngine;

namespace Underwater
{
    public sealed class LobsterAI : SeaCreature
    {
        private Transform leftClaw;
        private Transform rightClaw;
        private Transform tailFan;
        private Vector3 homePosition;
        private Vector3 scuttleTarget;
        private float scuttleTimer;
        private float seed;

        public void Initialize(UnderwaterGameDirector director, Vector3 spawnPosition, Material bodyMaterial, Material accentMaterial)
        {
            transform.position = spawnPosition;
            homePosition = spawnPosition;
            scuttleTarget = spawnPosition;
            seed = Random.Range(0f, 100f);

            BuildVisuals(bodyMaterial, accentMaterial);
            InitializeBase(director, CreatureKind.Lobster);

            SphereCollider collider = gameObject.AddComponent<SphereCollider>();
            collider.radius = 0.78f;
            collider.center = new Vector3(0f, 0.28f, 0f);
        }

        protected override void TickBehavior()
        {
            scuttleTimer -= Time.deltaTime;

            if (scuttleTimer <= 0f || Vector3.Distance(transform.position, scuttleTarget) < 1.2f)
            {
                UpdateScuttleTarget();
            }

            Vector3 scuttleOffset = transform.right * Mathf.Sin(Time.time * 5.2f + seed) * 0.42f;
            float speed = HasDirective(CreatureDirectiveMode.PressurePlayer) ? 3.1f : 2.2f;
            Vector3 desiredVelocity = ((scuttleTarget + scuttleOffset) - transform.position).normalized * speed;

            AddBuoyancyBand(Director.SeaFloorY + 0.8f, 0.45f);
            MoveCreature(desiredVelocity, 8.5f, 4.6f, 1.1f);
            AnimateVisuals();
        }

        private void UpdateScuttleTarget()
        {
            Vector3 directiveCenter = homePosition;
            float roamRadius = 5f;
            float minTimer = 1.8f;
            float maxTimer = 3.4f;

            switch (DirectiveMode)
            {
                case CreatureDirectiveMode.MoveToPoint:
                    directiveCenter = DirectiveTarget;
                    roamRadius = 1.25f;
                    minTimer = 0.8f;
                    maxTimer = 1.4f;
                    break;
                case CreatureDirectiveMode.GuardZone:
                    directiveCenter = DirectiveTarget;
                    roamRadius = Mathf.Max(1.5f, DirectiveRadius);
                    minTimer = 1.1f;
                    maxTimer = 1.9f;
                    break;
                case CreatureDirectiveMode.HoldPosition:
                    directiveCenter = DirectiveTarget;
                    roamRadius = 0.4f;
                    minTimer = 0.5f;
                    maxTimer = 0.9f;
                    break;
                case CreatureDirectiveMode.PressurePlayer:
                    directiveCenter = Director.Player != null ? Director.Player.transform.position : transform.position;
                    roamRadius = 1.8f;
                    minTimer = 0.45f;
                    maxTimer = 0.9f;
                    break;
                case CreatureDirectiveMode.RetreatFromPlayer:
                    Vector3 away = transform.position;

                    if (Director.Player != null)
                    {
                        Vector3 retreatDirection = (transform.position - Director.Player.transform.position).normalized;
                        away += retreatDirection * Mathf.Max(DirectiveRadius, 6f);
                    }

                    directiveCenter = away;
                    roamRadius = 1.2f;
                    minTimer = 0.6f;
                    maxTimer = 1.1f;
                    break;
            }

            Vector2 randomCircle = Random.insideUnitCircle * roamRadius;
            scuttleTarget = directiveCenter + new Vector3(randomCircle.x, 0f, randomCircle.y);
            scuttleTarget = Director.ClampPoint(scuttleTarget, 4f);
            scuttleTarget.y = Director.SeaFloorY + Random.Range(0.45f, 0.95f);
            scuttleTimer = Random.Range(minTimer, maxTimer);
        }

        private void BuildVisuals(Material bodyMaterial, Material accentMaterial)
        {
            ModelRoot = new GameObject("Model").transform;
            ModelRoot.SetParent(transform);
            ModelRoot.localPosition = Vector3.zero;
            ModelRoot.localRotation = Quaternion.identity;

            Material shellMaterial = CreateRuntimeMaterial(bodyMaterial);
            Material highlightMaterial = CreateRuntimeMaterial(accentMaterial);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(ModelRoot);
            body.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            body.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            body.transform.localScale = new Vector3(0.65f, 0.45f, 1.55f);
            Destroy(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().material = shellMaterial;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(ModelRoot);
            head.transform.localPosition = new Vector3(0.95f, 0.36f, 0f);
            head.transform.localScale = new Vector3(0.52f, 0.44f, 0.44f);
            Destroy(head.GetComponent<Collider>());
            head.GetComponent<Renderer>().material = highlightMaterial;

            GameObject leftClawObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftClawObject.name = "Left Claw";
            leftClawObject.transform.SetParent(ModelRoot);
            leftClawObject.transform.localPosition = new Vector3(0.9f, 0.24f, -0.62f);
            leftClawObject.transform.localRotation = Quaternion.Euler(14f, 18f, 0f);
            leftClawObject.transform.localScale = new Vector3(0.82f, 0.16f, 0.22f);
            Destroy(leftClawObject.GetComponent<Collider>());
            leftClawObject.GetComponent<Renderer>().material = highlightMaterial;
            leftClaw = leftClawObject.transform;

            GameObject rightClawObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightClawObject.name = "Right Claw";
            rightClawObject.transform.SetParent(ModelRoot);
            rightClawObject.transform.localPosition = new Vector3(0.9f, 0.24f, 0.62f);
            rightClawObject.transform.localRotation = Quaternion.Euler(-14f, -18f, 0f);
            rightClawObject.transform.localScale = new Vector3(0.82f, 0.16f, 0.22f);
            Destroy(rightClawObject.GetComponent<Collider>());
            rightClawObject.GetComponent<Renderer>().material = highlightMaterial;
            rightClaw = rightClawObject.transform;

            GameObject tailObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tailObject.name = "Tail Fan";
            tailObject.transform.SetParent(ModelRoot);
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
                leg.transform.SetParent(ModelRoot);
                leg.transform.localPosition = new Vector3(0.35f - row * 0.45f, 0.02f, side * (0.42f + row * 0.08f));
                leg.transform.localRotation = Quaternion.Euler(0f, 0f, side * 30f);
                leg.transform.localScale = new Vector3(0.45f, 0.05f, 0.06f);
                Destroy(leg.GetComponent<Collider>());
                leg.GetComponent<Renderer>().material = shellMaterial;
            }
        }

        private void AnimateVisuals()
        {
            float clawWave = Mathf.Sin(Time.time * 8.2f + seed) * 18f;
            float tailWave = Mathf.Sin(Time.time * 9.5f + seed * 1.4f) * 12f;
            float bodyTilt = Mathf.Clamp(Vector3.Dot(Velocity.normalized, transform.right) * -8f, -8f, 8f);

            ModelRoot.localRotation = Quaternion.Euler(0f, 0f, bodyTilt);
            leftClaw.localRotation = Quaternion.Euler(14f + clawWave, 18f, 0f);
            rightClaw.localRotation = Quaternion.Euler(-14f - clawWave, -18f, 0f);
            tailFan.localRotation = Quaternion.Euler(0f, tailWave, 0f);
        }
    }
}
