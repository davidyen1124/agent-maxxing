using System;
using System.IO;
using UnityEngine;

namespace Forest
{
    [Serializable]
    internal sealed class ForestUserSettings
    {
        public const string RelativePath = "UserSettings/ForestApiSettings.json";

        public string openAiApiKey;
        public string openAiRealtimeModel;
        public string openAiRealtimeVoice;
        public string niaApiKey;
        public string niaBaseUrl;
        public string niaDefaultSearchMode;
        public string[] niaRepositories;
        public string[] niaDataSources;
        public int niaMaxTokens;
        public int voiceSampleRate;
        public float voiceMaxCaptureSeconds;

        public static string FilePath => Path.Combine(Directory.GetCurrentDirectory(), RelativePath);

        public static ForestUserSettings Load()
        {
            string path = FilePath;

            if (!File.Exists(path))
            {
                return new ForestUserSettings();
            }

            try
            {
                string json = File.ReadAllText(path);
                ForestUserSettings settings = JsonUtility.FromJson<ForestUserSettings>(json);
                return settings ?? new ForestUserSettings();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not read {RelativePath}: {ex.Message}");
                return new ForestUserSettings();
            }
        }

        public string OpenAiRealtimeModelOr(string fallback)
        {
            return string.IsNullOrWhiteSpace(openAiRealtimeModel) ? fallback : openAiRealtimeModel.Trim();
        }

        public string OpenAiRealtimeVoiceOr(string fallback)
        {
            return string.IsNullOrWhiteSpace(openAiRealtimeVoice) ? fallback : openAiRealtimeVoice.Trim();
        }

        public string NiaBaseUrlOr(string fallback)
        {
            return string.IsNullOrWhiteSpace(niaBaseUrl) ? fallback : niaBaseUrl.Trim();
        }

        public string NiaDefaultSearchModeOr(string fallback)
        {
            return string.IsNullOrWhiteSpace(niaDefaultSearchMode) ? fallback : niaDefaultSearchMode.Trim();
        }

        public int NiaMaxTokensOr(int fallback)
        {
            return niaMaxTokens > 0 ? niaMaxTokens : fallback;
        }

        public int VoiceSampleRateOr(int fallback)
        {
            return voiceSampleRate > 0 ? voiceSampleRate : fallback;
        }

        public float VoiceMaxCaptureSecondsOr(float fallback)
        {
            return voiceMaxCaptureSeconds > 0f ? voiceMaxCaptureSeconds : fallback;
        }
    }
}
