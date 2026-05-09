using System;
using UnityEngine;

namespace Underwater
{
    [Serializable]
    public sealed class AquariumDirectorSnapshot
    {
        public int sequence;
        public string capturedAtUtc;
        public string summary;
        public AquariumDirectorMetrics metrics;
        public AquariumPlayerSnapshot player;
        public AquariumThreadSnapshot[] threads;
        public AquariumArchivedPetSnapshot[] archivedPets;
    }

    [Serializable]
    public sealed class AquariumDirectorMetrics
    {
        public int activeThreads;
        public int archivedPets;
        public string bridgeState;
    }

    [Serializable]
    public sealed class AquariumPlayerSnapshot
    {
        public SerializableVector3 position;
        public SerializableVector3 forward;
        public float boostNormalized;
        public bool hasPointerLock;
    }

    [Serializable]
    public sealed class AquariumThreadSnapshot
    {
        public string id;
        public string title;
        public string statusMessage;
        public string phase;
        public string source;
        public float ageMinutes;
        public SerializableVector3 position;
        public SerializableVector3 velocity;
    }

    [Serializable]
    public sealed class AquariumArchivedPetSnapshot
    {
        public string id;
        public string title;
        public string statusMessage;
        public SerializableVector3 position;
    }

    [Serializable]
    public sealed class SerializableVector3
    {
        public float x;
        public float y;
        public float z;

        public static SerializableVector3 FromVector3(Vector3 value)
        {
            return new SerializableVector3
            {
                x = value.x,
                y = value.y,
                z = value.z
            };
        }

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
    }
}
