using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forest
{
    public static class ForestBootstrap
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
            if (Object.FindAnyObjectByType<ForestGameDirector>() == null)
            {
                GameObject directorObject = new GameObject("Forest Director");
                directorObject.AddComponent<ForestGameDirector>();
            }
        }
    }
}
