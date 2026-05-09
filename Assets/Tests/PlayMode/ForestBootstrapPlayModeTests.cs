using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Forest.Tests
{
    public sealed class ForestBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator TerrainDemoSceneBootstrapsForestGame()
        {
            SceneManager.LoadScene("TerrainDemoScene");
            yield return null;
            yield return null;

            ForestGameDirector director = Object.FindAnyObjectByType<ForestGameDirector>();

            Assert.That(director, Is.Not.Null, "Runtime bootstrap should create the forest director.");
            Assert.That(director.Player, Is.Not.Null, "Runtime bootstrap should create the player rig.");
            Assert.That(director.GetComponent<ForestDirectorBridge>(), Is.Not.Null, "Runtime bootstrap should attach the Codex forest bridge.");
            Assert.That(GameObject.Find("Website Sandbox Marker"), Is.Null, "Website sandbox marker should not be visible until a voice website request starts.");
            Assert.That(GameObject.Find("Sandbox Website Box"), Is.Null, "Legacy sandbox box should not be spawned on boot.");
            Assert.That(Object.FindAnyObjectByType<Terrain>(), Is.Not.Null, "Expected the forest terrain scene to be present.");
            Assert.That(Camera.main, Is.Not.Null, "Expected a main camera for first-person movement.");
            Assert.That(director.Player.SprintEnergyNormalized, Is.GreaterThan(0.99f), "Player should start with full sprint energy.");
            Assert.That(director.ActiveThreadCount, Is.GreaterThanOrEqualTo(0), "Thread count should be readable on boot.");
            Assert.That(director.ArchivedAnimalCount, Is.GreaterThanOrEqualTo(0), "Archived animal count should be readable on boot.");
            Assert.That(RenderSettings.sun, Is.Not.Null, "Terrain mode should register a realtime directional sun.");
            Assert.That(RenderSettings.sun.enabled, Is.True, "The terrain sun should be enabled at runtime.");
            Assert.That(RenderSettings.sun.type, Is.EqualTo(LightType.Directional), "The terrain sun should render as the URP main light.");
            Assert.That(RenderSettings.sun.intensity, Is.GreaterThanOrEqualTo(4.5f), "The terrain scene should boot in full daylight.");
            Assert.That(RenderSettings.ambientSkyColor.maxColorComponent, Is.GreaterThan(0.35f), "Terrain mode needs enough ambient fill for dense vegetation.");
            Assert.That(RenderSettings.fogDensity, Is.LessThanOrEqualTo(0.001f), "Terrain mode daylight fog should not black out the foreground.");

            FieldInfo timeOfDayField = typeof(ForestGameDirector).GetField("atmosphereTimeOfDay", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo weatherField = typeof(ForestGameDirector).GetField("atmosphereWeather", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo intensityField = typeof(ForestGameDirector).GetField("atmosphereIntensity", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo applyAtmosphereMethod = typeof(ForestGameDirector).GetMethod("ApplyAtmosphereProfile", BindingFlags.NonPublic | BindingFlags.Instance);

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

            FieldInfo precipitationField = typeof(ForestGameDirector).GetField("precipitationParticles", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(precipitationField, Is.Not.Null);

            ParticleSystem precipitation = precipitationField.GetValue(director) as ParticleSystem;
            Assert.That(precipitation, Is.Not.Null, "Atmosphere setup should create the precipitation particle system.");
            Assert.That(precipitation.main.maxParticles, Is.GreaterThanOrEqualTo(6000), "Terrain precipitation needs enough particles to read at forest scale.");

            weatherField.SetValue(director, "rain");
            intensityField.SetValue(director, 0.75f);
            applyAtmosphereMethod.Invoke(director, null);

            ParticleSystemRenderer precipitationRenderer = precipitation.GetComponent<ParticleSystemRenderer>();
            Assert.That(precipitationRenderer, Is.Not.Null, "Precipitation particles should have a renderer.");
            Assert.That(precipitation.emission.rateOverTime.constantMax, Is.GreaterThan(1000f), "Terrain rain should emit densely enough to be visible near the camera.");
            Assert.That(precipitation.main.startSize.constantMax, Is.GreaterThan(0.08f), "Terrain rain streaks should be scaled above arena-mode drizzle.");
            Assert.That(precipitation.shape.scale.x, Is.InRange(40f, 80f), "Terrain precipitation should be camera-local instead of spread across the whole world.");
            Assert.That(precipitationRenderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Stretch), "Rain should render as streaks rather than tiny dots.");

            weatherField.SetValue(director, "snow");
            applyAtmosphereMethod.Invoke(director, null);

            Assert.That(precipitation.emission.rateOverTime.constantMax, Is.GreaterThan(500f), "Terrain snow should emit enough flakes to be visible.");
            Assert.That(precipitation.main.startSize.constantMax, Is.GreaterThan(0.2f), "Terrain snowflakes should be large enough to see against the terrain skybox.");
            Assert.That(precipitation.shape.scale.y, Is.GreaterThan(4f), "Terrain snow should spawn in a deeper volume so flakes drift through view.");
            Assert.That(precipitationRenderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Billboard), "Snow should render as flakes, not rain streaks.");
        }

        [Test]
        public void ThreadAnimalBubbleShowsIdleTitleAndRunningMessage()
        {
            GameObject gameObject = new GameObject("Thread Animal Test");
            ThreadAnimalAI animal = gameObject.AddComponent<ThreadAnimalAI>();

            try
            {
                animal.ApplySnapshot(new ForestThreadSnapshot
                {
                    id = "thread-1",
                    title = "Review recent conversations",
                    statusMessage = "Idle",
                    phase = "idle"
                });

                Assert.That(animal.BubbleMessage, Is.EqualTo("Review recent conversations"));

                animal.ApplySnapshot(new ForestThreadSnapshot
                {
                    id = "thread-1",
                    title = "Review recent conversations",
                    statusMessage = "Comparing implementation options",
                    phase = "working"
                });

                Assert.That(animal.BubbleMessage, Is.EqualTo("Comparing implementation options"));

                animal.ApplySnapshot(new ForestThreadSnapshot
                {
                    id = "thread-1",
                    title = "Review recent conversations",
                    statusMessage = "Thinking",
                    phase = "working"
                });

                Assert.That(animal.BubbleMessage, Is.EqualTo("Thinking"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ThreadAnimalRandomActionsNeverChooseFailure()
        {
            GameObject gameObject = new GameObject("Thread Animal Random Action Test");
            ThreadAnimalAI animal = gameObject.AddComponent<ThreadAnimalAI>();

            try
            {
                MethodInfo method = typeof(ThreadAnimalAI).GetMethod(
                    "PickRandomActionState",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(method, Is.Not.Null);

                for (int i = 0; i < 200; i++)
                {
                    object state = method.Invoke(animal, null);
                    Assert.That(state, Is.Not.EqualTo(CodexAnimalAnimationState.Failed));
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
            MethodInfo method = typeof(ForestDirectorBridge).GetMethod(
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
            MethodInfo normalizeTime = typeof(ForestGameDirector).GetMethod(
                "NormalizeTimeOfDayOption",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo normalizeWeather = typeof(ForestGameDirector).GetMethod(
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
            Assert.That(normalizeWeather.Invoke(null, new object[] { "same", "rain" }), Is.EqualTo("rain"));
        }
    }
}
