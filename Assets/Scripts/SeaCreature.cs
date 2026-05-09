using UnityEngine;

namespace Underwater
{
    public enum CreatureKind
    {
        Shark,
        Lobster
    }

    public enum CreatureDirectiveMode
    {
        Autonomous,
        MoveToPoint,
        GuardZone,
        PressurePlayer,
        RetreatFromPlayer,
        HoldPosition
    }

    public abstract class SeaCreature : MonoBehaviour
    {
        private CreatureDirectiveMode directiveMode;
        private Vector3 directiveTarget;
        private float directiveRadius;
        private float directiveExpiresAt;
        protected UnderwaterGameDirector Director { get; private set; }

        protected Transform ModelRoot { get; set; }

        protected Vector3 Velocity { get; set; }

        public string CreatureId { get; private set; }

        public CreatureKind Kind { get; private set; }

        public CreatureDirectiveMode DirectiveMode
        {
            get
            {
                RefreshDirectiveLifetime();
                return directiveMode;
            }
        }

        protected Vector3 DirectiveTarget => directiveTarget;

        protected float DirectiveRadius => directiveRadius;

        protected virtual void Update()
        {
            TickBehavior();
        }

        protected virtual void OnDestroy()
        {
            if (Director != null)
            {
                Director.UnregisterCreature(this);
            }
        }

        public void InitializeBase(UnderwaterGameDirector director, CreatureKind kind)
        {
            Director = director;
            Kind = kind;
            CreatureId = director.AllocateCreatureId(kind);
            directiveMode = CreatureDirectiveMode.Autonomous;
            Director.RegisterCreature(this);
        }

        public void ApplyDirective(CreatureDirectiveMode mode, Vector3 target, float radius, float durationSeconds)
        {
            directiveMode = mode;
            directiveTarget = target;
            directiveRadius = Mathf.Max(0.5f, radius);
            directiveExpiresAt = durationSeconds > 0.05f ? Time.time + durationSeconds : 0f;
        }

        public void ClearDirective()
        {
            directiveMode = CreatureDirectiveMode.Autonomous;
            directiveTarget = transform.position;
            directiveRadius = 0f;
            directiveExpiresAt = 0f;
        }

        public AquariumCreatureSnapshot CreateSnapshot()
        {
            return new AquariumCreatureSnapshot
            {
                id = CreatureId,
                kind = Kind.ToString().ToLowerInvariant(),
                directive = DirectiveMode.ToString(),
                position = SerializableVector3.FromVector3(transform.position),
                velocity = SerializableVector3.FromVector3(Velocity)
            };
        }

        protected Material CreateRuntimeMaterial(Material sourceMaterial)
        {
            return new Material(sourceMaterial);
        }

        protected void AddBuoyancyBand(float centerHeight, float strength)
        {
            float offset = centerHeight - transform.position.y;
            Velocity += Vector3.up * offset * strength * Time.deltaTime;
        }

        protected void MoveCreature(Vector3 desiredVelocity, float acceleration, float turnSpeed, float clampPadding)
        {
            Velocity = Vector3.MoveTowards(Velocity, desiredVelocity, acceleration * Time.deltaTime);
            transform.position += Velocity * Time.deltaTime;
            transform.position = Director.ClampPoint(transform.position, clampPadding);

            if (Velocity.sqrMagnitude > 0.06f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(Velocity.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * turnSpeed);
            }
        }

        protected bool HasDirective(CreatureDirectiveMode mode)
        {
            return DirectiveMode == mode;
        }

        protected void RefreshDirectiveLifetime()
        {
            if (directiveMode == CreatureDirectiveMode.Autonomous || directiveExpiresAt <= 0f)
            {
                return;
            }

            if (Time.time >= directiveExpiresAt)
            {
                ClearDirective();
            }
        }

        protected abstract void TickBehavior();
    }
}
