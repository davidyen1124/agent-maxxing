using System;
using UnityEngine;

namespace Forest
{
    [Serializable]
    public sealed class ForestDirectorSnapshot
    {
        public int sequence;
        public string capturedAtUtc;
        public string summary;
        public ForestDirectorMetrics metrics;
        public ForestPlayerSnapshot player;
        public ForestThreadSnapshot[] threads;
        public ForestArchivedThreadSnapshot[] archivedAnimals;
    }

    [Serializable]
    public sealed class ForestDirectorMetrics
    {
        public int activeThreads;
        public int archivedAnimals;
        public string bridgeState;
    }

    [Serializable]
    public sealed class ForestPlayerSnapshot
    {
        public SerializableVector3 position;
        public SerializableVector3 forward;
        public float boostNormalized;
        public bool hasPointerLock;
    }

    [Serializable]
    public sealed class ForestThreadSnapshot
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
    public sealed class ForestArchivedThreadSnapshot
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
