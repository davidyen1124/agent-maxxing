using System;
using System.IO;
using UnityEngine;

namespace Forest
{
#pragma warning disable 0649
    [Serializable]
    internal sealed class ForestUserSettings
    {
        public const string RelativePath = "UserSettings/UnderwaterApiSettings.json";

        public string openAiApiKey;
        public string openAiRealtimeModel;
        public string openAiRealtimeVoice;
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

        public string OpenAiRealtimeModelOr(string defaultValue)
        {
            return string.IsNullOrWhiteSpace(openAiRealtimeModel) ? defaultValue : openAiRealtimeModel.Trim();
        }

        public string OpenAiRealtimeVoiceOr(string defaultValue)
        {
            return string.IsNullOrWhiteSpace(openAiRealtimeVoice) ? defaultValue : openAiRealtimeVoice.Trim();
        }

        public int VoiceSampleRateOr(int defaultValue)
        {
            return voiceSampleRate > 0 ? voiceSampleRate : defaultValue;
        }

        public float VoiceMaxCaptureSecondsOr(float defaultValue)
        {
            return voiceMaxCaptureSeconds > 0f ? voiceMaxCaptureSeconds : defaultValue;
        }
    }
#pragma warning restore 0649
}
