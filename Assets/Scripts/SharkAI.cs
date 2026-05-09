using UnityEngine;

namespace Underwater
{
    public sealed class SharkAI : SeaCreature
    {
        private Transform tail;
        private Transform dorsalFin;
        private Vector3 homePosition;
        private Vector3 driftTarget;
        private float driftTimer;
        private float orbitRadius;
        private float cruiseHeight;
        private float seed;

        public void Initialize(UnderwaterGameDirector director, Vector3 spawnPosition, Material bodyMaterial, Material accentMaterial)
        {
            transform.position = spawnPosition;
            seed = Random.Range(0f, 100f);
            homePosition = spawnPosition;
            driftTarget = spawnPosition;
            orbitRadius = Random.Range(6f, 13f);
            cruiseHeight = Random.Range(director.SeaFloorY + 6f, director.PlayBounds.max.y - 2.5f);

            BuildVisuals(bodyMaterial, accentMaterial);
            InitializeBase(director, CreatureKind.Shark);

            CapsuleCollider collider = gameObject.AddComponent<CapsuleCollider>();
            collider.direction = 0;
            collider.center = new Vector3(0f, 0f, 0f);
            collider.radius = 0.62f;
            collider.height = 3.8f;
        }

        protected override void TickBehavior()
        {
            driftTimer -= Time.deltaTime;

            if (driftTimer <= 0f || Vector3.Distance(transform.position, driftTarget) < 2.4f)
            {
                UpdateDriftTarget();
            }

            Vector3 wave = new Vector3(
                Mathf.Sin(Time.time * 0.65f + seed) * 1.3f,
                Mathf.Sin(Time.time * 0.95f + seed * 0.8f) * 0.6f,
                Mathf.Cos(Time.time * 0.72f + seed) * 1.1f);

            float speed = HasDirective(CreatureDirectiveMode.PressurePlayer) ? 5.6f : 4.2f;
            Vector3 desiredVelocity = (driftTarget + wave - transform.position).normalized * speed;

            AddBuoyancyBand(cruiseHeight, 0.2f);
            MoveCreature(desiredVelocity, 5.5f, 2.4f, 1.4f);
            AnimateVisuals();
        }

        private void UpdateDriftTarget()
        {
            Vector3 center = homePosition;
            float localRadius = orbitRadius;
            float minTimer = 2.4f;
            float maxTimer = 4.8f;
            float targetHeight = cruiseHeight;

            switch (DirectiveMode)
            {
                case CreatureDirectiveMode.MoveToPoint:
                    center = DirectiveTarget;
                    localRadius = 1.8f;
                    minTimer = 0.8f;
                    maxTimer = 1.5f;
                    targetHeight = DirectiveTarget.y;
                    break;
                case CreatureDirectiveMode.GuardZone:
                    center = DirectiveTarget;
                    localRadius = Mathf.Max(2f, DirectiveRadius);
                    minTimer = 1.2f;
                    maxTimer = 2.1f;
                    targetHeight = DirectiveTarget.y;
                    break;
                case CreatureDirectiveMode.HoldPosition:
                    center = DirectiveTarget;
                    localRadius = 0.9f;
                    minTimer = 0.5f;
                    maxTimer = 1f;
                    targetHeight = DirectiveTarget.y;
                    break;
                case CreatureDirectiveMode.PressurePlayer:
                    center = Director.Player != null ? Director.Player.transform.position : transform.position;
                    localRadius = Mathf.Max(2.4f, DirectiveRadius);
                    minTimer = 0.55f;
                    maxTimer = 1.1f;
                    targetHeight = center.y + 0.4f;
                    break;
                case CreatureDirectiveMode.RetreatFromPlayer:
                    Vector3 away = transform.position;

                    if (Director.Player != null)
                    {
                        Vector3 retreatDirection = (transform.position - Director.Player.transform.position).normalized;
                        away += retreatDirection * Mathf.Max(DirectiveRadius, 10f);
                    }

                    center = away;
                    localRadius = 2.2f;
                    minTimer = 0.65f;
                    maxTimer = 1.35f;
                    targetHeight = center.y;
                    break;
            }

            Vector2 circle = Random.insideUnitCircle * localRadius;
            driftTarget = center + new Vector3(circle.x, 0f, circle.y);
            driftTarget = Director.ClampPoint(driftTarget, 6f);
            driftTarget.y = Mathf.Clamp(
                targetHeight + Mathf.Sin(Time.time * 0.7f + seed) * 1.5f,
                Director.SeaFloorY + 2.5f,
                Director.PlayBounds.max.y - 1.5f);
            driftTimer = Random.Range(minTimer, maxTimer);
            cruiseHeight = Mathf.Lerp(cruiseHeight, driftTarget.y, 0.45f);
        }

        private void BuildVisuals(Material bodyMaterial, Material accentMaterial)
        {
            ModelRoot = new GameObject("Model").transform;
            ModelRoot.SetParent(transform);
            ModelRoot.localPosition = Vector3.zero;
            ModelRoot.localRotation = Quaternion.identity;

            Material sharkBody = CreateRuntimeMaterial(bodyMaterial);
            Material sharkAccent = CreateRuntimeMaterial(accentMaterial);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(ModelRoot);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            body.transform.localScale = new Vector3(1.1f, 1f, 2.8f);
            Destroy(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().material = sharkBody;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(ModelRoot);
            head.transform.localPosition = new Vector3(1.6f, 0.05f, 0f);
            head.transform.localScale = new Vector3(0.75f, 0.62f, 0.62f);
            Destroy(head.GetComponent<Collider>());
            head.GetComponent<Renderer>().material = sharkAccent;

            GameObject leftFin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftFin.name = "Left Fin";
            leftFin.transform.SetParent(ModelRoot);
            leftFin.transform.localPosition = new Vector3(0.2f, -0.12f, -0.72f);
            leftFin.transform.localRotation = Quaternion.Euler(10f, 28f, 24f);
            leftFin.transform.localScale = new Vector3(0.5f, 0.05f, 1.15f);
            Destroy(leftFin.GetComponent<Collider>());
            leftFin.GetComponent<Renderer>().material = sharkBody;

            GameObject rightFin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightFin.name = "Right Fin";
            rightFin.transform.SetParent(ModelRoot);
            rightFin.transform.localPosition = new Vector3(0.2f, -0.12f, 0.72f);
            rightFin.transform.localRotation = Quaternion.Euler(-10f, -28f, 24f);
            rightFin.transform.localScale = new Vector3(0.5f, 0.05f, 1.15f);
            Destroy(rightFin.GetComponent<Collider>());
            rightFin.GetComponent<Renderer>().material = sharkBody;

            GameObject dorsal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            dorsal.name = "Dorsal Fin";
            dorsal.transform.SetParent(ModelRoot);
            dorsal.transform.localPosition = new Vector3(-0.05f, 0.78f, 0f);
            dorsal.transform.localRotation = Quaternion.Euler(0f, 0f, 22f);
            dorsal.transform.localScale = new Vector3(0.9f, 0.08f, 0.68f);
            Destroy(dorsal.GetComponent<Collider>());
            dorsal.GetComponent<Renderer>().material = sharkBody;
            dorsalFin = dorsal.transform;

            GameObject tailObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tailObject.name = "Tail";
            tailObject.transform.SetParent(ModelRoot);
            tailObject.transform.localPosition = new Vector3(-2.05f, 0f, 0f);
            tailObject.transform.localRotation = Quaternion.Euler(0f, 0f, 32f);
            tailObject.transform.localScale = new Vector3(0.28f, 1.1f, 0.12f);
            Destroy(tailObject.GetComponent<Collider>());
            tailObject.GetComponent<Renderer>().material = sharkAccent;
            tail = tailObject.transform;
        }

        private void AnimateVisuals()
        {
            float tailSway = Mathf.Sin(Time.time * 9.6f + seed) * 28f;
            float bodyRoll = Mathf.Clamp(Vector3.Dot(Velocity.normalized, transform.right) * -10f, -10f, 10f);

            ModelRoot.localRotation = Quaternion.Euler(0f, 0f, bodyRoll);
            tail.localRotation = Quaternion.Euler(0f, tailSway, 32f);
            dorsalFin.localRotation = Quaternion.Euler(0f, tailSway * 0.15f, 22f);
        }
    }
}
