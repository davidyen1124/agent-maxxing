using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Underwater.Tests
{
    public sealed class UnderwaterBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator SampleSceneBootstrapsUnderwaterSlice()
        {
            SceneManager.LoadScene("SampleScene");
            yield return null;
            yield return null;

            UnderwaterGameDirector director = Object.FindFirstObjectByType<UnderwaterGameDirector>();

            Assert.That(director, Is.Not.Null, "Runtime bootstrap should create the underwater director.");
            Assert.That(director.Player, Is.Not.Null, "Runtime bootstrap should create the player rig.");
            Assert.That(director.CountAlive(CreatureKind.Shark), Is.EqualTo(5), "Expected the shark population to spawn.");
            Assert.That(director.CountAlive(CreatureKind.Lobster), Is.EqualTo(14), "Expected the lobster population to spawn.");
            Assert.That(GameObject.Find("Runtime Arena"), Is.Not.Null, "Expected the procedural arena root to be present.");
            Assert.That(Camera.main, Is.Not.Null, "Expected a main camera for first-person swimming.");
            Assert.That(director.Player.BoostNormalized, Is.GreaterThan(0.99f), "Player should start with full swim boost.");
        }
    }
}
