using System.Collections.Generic;
using UnityEngine;

namespace Underwater
{
    public enum CreatureKind
    {
        Shark,
        Lobster
    }

    public abstract class SeaCreature : MonoBehaviour
    {
        private readonly List<Material> runtimeMaterials = new List<Material>();
        private readonly List<Color> baseEmissionColors = new List<Color>();

        private float hitFlash;
        private float health;

        protected UnderwaterGameDirector Director { get; private set; }

        protected Transform ModelRoot { get; set; }

        protected Vector3 Velocity { get; set; }

        protected float MaxHealth { get; private set; }

        public CreatureKind Kind { get; private set; }

        public bool IsAlive { get; private set; }

        protected virtual void Update()
        {
            hitFlash = Mathf.MoveTowards(hitFlash, 0f, Time.deltaTime * 3.8f);
            UpdateRuntimeMaterialState();

            if (!IsAlive)
            {
                transform.position += Vector3.down * 1.3f * Time.deltaTime;
                transform.Rotate(6f * Time.deltaTime, 0f, 9f * Time.deltaTime, Space.Self);
                return;
            }

            TickAlive();
        }

        public void InitializeBase(UnderwaterGameDirector director, CreatureKind kind, float maxHealth)
        {
            Director = director;
            Kind = kind;
            MaxHealth = maxHealth;
            health = maxHealth;
            IsAlive = true;
            Director.RegisterCreature(this);
        }

        public virtual void TakeDamage(float damage, Vector3 hitPoint, Vector3 hitDirection)
        {
            if (!IsAlive)
            {
                return;
            }

            health -= damage;
            hitFlash = Mathf.Clamp01(hitFlash + 1f);
            Velocity += hitDirection.normalized * 1.5f;

            if (health <= 0f)
            {
                Die();
            }
        }

        protected Material CreateRuntimeMaterial(Material sourceMaterial, Color emissionColor)
        {
            Material material = new Material(sourceMaterial);
            TrackMaterial(material, emissionColor);
            return material;
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

        protected abstract void TickAlive();

        private void Die()
        {
            IsAlive = false;
            Director.UnregisterCreature(this);

            Collider collider = GetComponent<Collider>();

            if (collider != null)
            {
                collider.enabled = false;
            }

            Destroy(gameObject, 4f);
        }

        private void TrackMaterial(Material material, Color emissionColor)
        {
            runtimeMaterials.Add(material);
            baseEmissionColors.Add(emissionColor);

            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emissionColor);
            }
        }

        private void UpdateRuntimeMaterialState()
        {
            if (runtimeMaterials.Count == 0)
            {
                return;
            }

            for (int i = 0; i < runtimeMaterials.Count; i++)
            {
                Material material = runtimeMaterials[i];
                Color baseEmission = baseEmissionColors[i];
                Color flashEmission = Color.Lerp(baseEmission, Color.white * 1.6f, hitFlash);

                if (material.HasProperty("_EmissionColor"))
                {
                    material.SetColor("_EmissionColor", flashEmission);
                }
            }
        }
    }
}
