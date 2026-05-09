using UnityEngine;

namespace Underwater
{
    public sealed class ArchivedThreadRoll : MonoBehaviour
    {
        private string threadId = string.Empty;
        private string title = "Archived thread";

        public string ThreadId => threadId;

        public string Title => title;

        public void Initialize(UnderwaterGameDirector director, AquariumArchivedRollSnapshot snapshot, Material rollMaterial)
        {
            threadId = snapshot != null ? snapshot.id ?? string.Empty : string.Empty;
            title = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.title) ? snapshot.title.Trim() : "Archived thread";

            Vector3 position = snapshot != null && snapshot.position != null
                ? snapshot.position.ToVector3()
                : director.GetRandomSeafloorPoint(6f);

            transform.position = new Vector3(position.x, director.SeaFloorY + 0.18f, position.z);

            GameObject bunBottom = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bunBottom.name = "Bun Bottom";
            bunBottom.transform.SetParent(transform);
            bunBottom.transform.localPosition = new Vector3(0f, 0.18f, 0f);
            bunBottom.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            bunBottom.transform.localScale = new Vector3(0.7f, 0.22f, 1.2f);
            bunBottom.GetComponent<Renderer>().sharedMaterial = rollMaterial;
            Destroy(bunBottom.GetComponent<Collider>());

            GameObject filling = GameObject.CreatePrimitive(PrimitiveType.Cube);
            filling.name = "Filling";
            filling.transform.SetParent(transform);
            filling.transform.localPosition = new Vector3(0f, 0.32f, 0f);
            filling.transform.localRotation = Quaternion.identity;
            filling.transform.localScale = new Vector3(1.1f, 0.18f, 0.48f);
            filling.GetComponent<Renderer>().sharedMaterial = rollMaterial;
            Destroy(filling.GetComponent<Collider>());

            GameObject bunTop = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bunTop.name = "Bun Top";
            bunTop.transform.SetParent(transform);
            bunTop.transform.localPosition = new Vector3(0f, 0.46f, 0f);
            bunTop.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            bunTop.transform.localScale = new Vector3(0.78f, 0.2f, 1.3f);
            bunTop.GetComponent<Renderer>().sharedMaterial = rollMaterial;
            Destroy(bunTop.GetComponent<Collider>());
        }

        public AquariumArchivedRollSnapshot CreateSnapshot()
        {
            return new AquariumArchivedRollSnapshot
            {
                id = threadId,
                title = title,
                position = SerializableVector3.FromVector3(transform.position)
            };
        }
    }
}
