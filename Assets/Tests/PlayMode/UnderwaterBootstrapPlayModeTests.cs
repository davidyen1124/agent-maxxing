using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Underwater.Tests
{
    public sealed class UnderwaterBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator TerrainDemoSceneBootstrapsUnderwaterSlice()
        {
            SceneManager.LoadScene("TerrainDemoScene");
            yield return null;
            yield return null;

            UnderwaterGameDirector director = Object.FindAnyObjectByType<UnderwaterGameDirector>();

            Assert.That(director, Is.Not.Null, "Runtime bootstrap should create the underwater director.");
            Assert.That(director.Player, Is.Not.Null, "Runtime bootstrap should create the player rig.");
            Assert.That(director.GetComponent<AquariumDirectorBridge>(), Is.Not.Null, "Runtime bootstrap should attach the Codex aquarium bridge.");
            Assert.That(Object.FindAnyObjectByType<Terrain>(), Is.Not.Null, "Expected the forest terrain scene to be present.");
            Assert.That(Camera.main, Is.Not.Null, "Expected a main camera for first-person movement.");
            Assert.That(director.Player.SprintEnergyNormalized, Is.GreaterThan(0.99f), "Player should start with full sprint energy.");
            Assert.That(director.ActiveThreadCount, Is.GreaterThanOrEqualTo(0), "Thread count should be readable on boot.");
            Assert.That(director.ArchivedPetCount, Is.GreaterThanOrEqualTo(0), "Archived pet count should be readable on boot.");
        }

        [Test]
        public void ThreadPetBubbleShowsIdleTitleAndRunningMessage()
        {
            GameObject gameObject = new GameObject("Thread Pet Test");
            ThreadPetAI pet = gameObject.AddComponent<ThreadPetAI>();

            try
            {
                pet.ApplySnapshot(new AquariumThreadSnapshot
                {
                    id = "thread-1",
                    title = "Review recent conversations",
                    statusMessage = "Idle",
                    phase = "idle"
                });

                Assert.That(pet.BubbleMessage, Is.EqualTo("Review recent conversations"));

                pet.ApplySnapshot(new AquariumThreadSnapshot
                {
                    id = "thread-1",
                    title = "Review recent conversations",
                    statusMessage = "Comparing implementation options",
                    phase = "working"
                });

                Assert.That(pet.BubbleMessage, Is.EqualTo("Comparing implementation options"));

                pet.ApplySnapshot(new AquariumThreadSnapshot
                {
                    id = "thread-1",
                    title = "Review recent conversations",
                    statusMessage = "Thinking",
                    phase = "working"
                });

                Assert.That(pet.BubbleMessage, Is.EqualTo("Thinking"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ThreadPetRandomActionsNeverChooseFailure()
        {
            GameObject gameObject = new GameObject("Thread Pet Random Action Test");
            ThreadPetAI pet = gameObject.AddComponent<ThreadPetAI>();

            try
            {
                MethodInfo method = typeof(ThreadPetAI).GetMethod(
                    "PickRandomActionState",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(method, Is.Not.Null);

                for (int i = 0; i < 200; i++)
                {
                    object state = method.Invoke(pet, null);
                    Assert.That(state, Is.Not.EqualTo(CodexPetAnimationState.Failed));
                }
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BridgeReadsReasoningSummaryTextObjects()
        {
            MethodInfo method = typeof(AquariumDirectorBridge).GetMethod(
                "ReadLatestReasoningOrAgentMessage",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            List<object> items = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "reasoning",
                    ["summary"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "summary_text",
                            ["text"] = "Comparing renderer paths"
                        }
                    }
                }
            };

            string message = method.Invoke(null, new object[] { items }) as string;

            Assert.That(message, Is.EqualTo("Comparing renderer paths"));
        }
    }
}
