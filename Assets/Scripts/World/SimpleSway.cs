using UnityEngine;

namespace Forest
{
    public sealed class SimpleSway : MonoBehaviour
    {
        private Quaternion initialRotation;
        private Vector3 initialPosition;

        public Vector3 Axis { get; set; } = Vector3.forward;

        public float Amplitude { get; set; } = 10f;

        public float Frequency { get; set; } = 1f;

        public float VerticalBob { get; set; } = 0.08f;

        public float Phase { get; set; }

        private void Awake()
        {
            initialRotation = transform.localRotation;
            initialPosition = transform.localPosition;
        }

        private void Update()
        {
            float sway = Mathf.Sin((Time.time * Frequency) + Phase);
            transform.localRotation = initialRotation * Quaternion.AngleAxis(sway * Amplitude, Axis);
            transform.localPosition = initialPosition + Vector3.up * sway * VerticalBob;
        }
    }
}
