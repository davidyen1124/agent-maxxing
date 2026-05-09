using System;
using System.IO;
using UnityEngine;

namespace Underwater
{
    [Serializable]
    internal sealed class UnderwaterUserSettings
    {
        public const string RelativePath = "UserSettings/UnderwaterApiSettings.json";

        public string openAiApiKey;
        public string openAiRealtimeModel;
        public string openAiRealtimeVoice;
        public int voiceSampleRate;
        public float voiceMaxCaptureSeconds;

        public static string FilePath => Path.Combine(Directory.GetCurrentDirectory(), RelativePath);

        public static UnderwaterUserSettings Load()
        {
            string path = FilePath;

            if (!File.Exists(path))
            {
                return new UnderwaterUserSettings();
            }

            try
            {
                string json = File.ReadAllText(path);
                UnderwaterUserSettings settings = JsonUtility.FromJson<UnderwaterUserSettings>(json);
                return settings ?? new UnderwaterUserSettings();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not read {RelativePath}: {ex.Message}");
                return new UnderwaterUserSettings();
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
