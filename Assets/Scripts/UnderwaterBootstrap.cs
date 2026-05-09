using UnityEngine;
using UnityEngine.SceneManagement;

namespace Underwater
{
    public static class UnderwaterBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterSceneHook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            EnsureDirector();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureDirector();
        }

        private static void EnsureDirector()
        {
            if (Object.FindAnyObjectByType<UnderwaterGameDirector>() == null)
            {
                GameObject directorObject = new GameObject("Underwater Director");
                directorObject.AddComponent<UnderwaterGameDirector>();
            }
        }
    }
}
