using System;
using UnityEngine;

namespace Underwater
{
    [Serializable]
    public sealed class AquariumBridgeEnvelope
    {
        public string type;
        public string bridgeVersion;
        public AquariumBridgeHello hello;
        public AquariumDirectorSnapshot snapshot;
        public AquariumDirectorAction action;
        public AquariumDirectorActionResult actionResult;
        public AquariumDirectorStatusUpdate status;
    }

    [Serializable]
    public sealed class AquariumBridgeHello
    {
        public string source;
        public string worldName;
        public string sessionId;
    }

    [Serializable]
    public sealed class AquariumDirectorSnapshot
    {
        public int sequence;
        public string capturedAtUtc;
        public string summary;
        public AquariumDirectorMetrics metrics;
        public AquariumPlayerSnapshot player;
        public AquariumCreatureSnapshot[] sharks;
        public AquariumCreatureSnapshot[] lobsters;
    }

    [Serializable]
    public sealed class AquariumDirectorMetrics
    {
        public int sharkCount;
        public int lobsterCount;
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
    public sealed class AquariumCreatureSnapshot
    {
        public string id;
        public string kind;
        public string directive;
        public SerializableVector3 position;
        public SerializableVector3 velocity;
    }

    [Serializable]
    public sealed class AquariumDirectorAction
    {
        public string actionId;
        public string species;
        public string directive;
        public string scope;
        public string[] creatureIds;
        public int count;
        public SerializableVector3 target;
        public float radius;
        public float durationSeconds;
    }

    [Serializable]
    public sealed class AquariumDirectorActionResult
    {
        public string actionId;
        public bool success;
        public string message;
        public string[] affectedCreatureIds;
    }

    [Serializable]
    public sealed class AquariumDirectorStatusUpdate
    {
        public string phase;
        public string text;
        public bool unityConnected;
        public bool codexConnected;
        public string threadId;
        public string turnId;
        public int lastSnapshotSequence;
        public string lastTool;
        public string lastActionId;
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
