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
            Assert.That(RenderSettings.sun, Is.Not.Null, "Terrain mode should register a realtime directional sun.");
            Assert.That(RenderSettings.sun.enabled, Is.True, "The terrain sun should be enabled at runtime.");
            Assert.That(RenderSettings.sun.type, Is.EqualTo(LightType.Directional), "The terrain sun should render as the URP main light.");
            Assert.That(RenderSettings.sun.intensity, Is.GreaterThanOrEqualTo(4.5f), "The terrain scene should boot in full daylight.");
            Assert.That(RenderSettings.ambientSkyColor.maxColorComponent, Is.GreaterThan(0.35f), "Terrain mode needs enough ambient fill for dense vegetation.");
            Assert.That(RenderSettings.fogDensity, Is.LessThanOrEqualTo(0.001f), "Terrain mode daylight fog should not black out the foreground.");

            FieldInfo timeOfDayField = typeof(UnderwaterGameDirector).GetField("atmosphereTimeOfDay", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo weatherField = typeof(UnderwaterGameDirector).GetField("atmosphereWeather", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo intensityField = typeof(UnderwaterGameDirector).GetField("atmosphereIntensity", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo applyAtmosphereMethod = typeof(UnderwaterGameDirector).GetMethod("ApplyAtmosphereProfile", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(timeOfDayField, Is.Not.Null);
            Assert.That(weatherField, Is.Not.Null);
            Assert.That(intensityField, Is.Not.Null);
            Assert.That(applyAtmosphereMethod, Is.Not.Null);

            timeOfDayField.SetValue(director, "night");
            weatherField.SetValue(director, "clear");
            intensityField.SetValue(director, 0.55f);
            applyAtmosphereMethod.Invoke(director, null);

            Assert.That(RenderSettings.sun.intensity, Is.GreaterThanOrEqualTo(1.2f), "Terrain night should be playable, not pitch black.");
            Assert.That(RenderSettings.ambientSkyColor.maxColorComponent, Is.GreaterThan(0.3f), "Terrain night needs enough moonlit ambient fill for navigation.");
            Assert.That(RenderSettings.fogDensity, Is.LessThanOrEqualTo(0.0015f), "Terrain night fog should not hide nearby vegetation.");
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

        [Test]
        public void AtmosphereCommandAliasesCoverCommonTimeAndWeatherPhrases()
        {
            MethodInfo normalizeTime = typeof(UnderwaterGameDirector).GetMethod(
                "NormalizeTimeOfDayOption",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo normalizeWeather = typeof(UnderwaterGameDirector).GetMethod(
                "NormalizeWeatherOption",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(normalizeTime, Is.Not.Null);
            Assert.That(normalizeWeather, Is.Not.Null);

            Assert.That(normalizeTime.Invoke(null, new object[] { "first light", "night" }), Is.EqualTo("dawn"));
            Assert.That(normalizeTime.Invoke(null, new object[] { "midday", "night" }), Is.EqualTo("day"));
            Assert.That(normalizeTime.Invoke(null, new object[] { "golden-hour", "night" }), Is.EqualTo("sunset"));
            Assert.That(normalizeTime.Invoke(null, new object[] { "moonlit", "day" }), Is.EqualTo("night"));
            Assert.That(normalizeTime.Invoke(null, new object[] { "preserve", "sunset" }), Is.EqualTo("sunset"));

            Assert.That(normalizeWeather.Invoke(null, new object[] { "clear sky", "storm" }), Is.EqualTo("clear"));
            Assert.That(normalizeWeather.Invoke(null, new object[] { "overcast", "clear" }), Is.EqualTo("fog"));
            Assert.That(normalizeWeather.Invoke(null, new object[] { "drizzle", "clear" }), Is.EqualTo("rain"));
            Assert.That(normalizeWeather.Invoke(null, new object[] { "lightning", "clear" }), Is.EqualTo("storm"));
            Assert.That(normalizeWeather.Invoke(null, new object[] { "flurries", "clear" }), Is.EqualTo("snow"));
            Assert.That(normalizeWeather.Invoke(null, new object[] { "submerged", "clear" }), Is.EqualTo("bubbles"));
            Assert.That(normalizeWeather.Invoke(null, new object[] { "same", "rain" }), Is.EqualTo("rain"));
        }
    }
}
