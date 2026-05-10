using System;
using UnityEngine;

namespace Forest
{
    public static class AudioSampleUtility
    {
        public static float[] ExtractMonoSamples(AudioClip clip, int sampleCount)
        {
            if (clip == null)
            {
                return Array.Empty<float>();
            }

            int channels = Mathf.Max(1, clip.channels);
            int clampedSampleCount = Mathf.Clamp(sampleCount, 0, clip.samples);
            float[] interleaved = new float[clampedSampleCount * channels];
            clip.GetData(interleaved, 0);

            if (channels == 1)
            {
                return interleaved;
            }

            float[] mono = new float[clampedSampleCount];

            for (int sample = 0; sample < clampedSampleCount; sample++)
            {
                float sum = 0f;
                int offset = sample * channels;

                for (int channel = 0; channel < channels; channel++)
                {
                    sum += interleaved[offset + channel];
                }

                mono[sample] = sum / channels;
            }

            return mono;
        }

        public static byte[] FloatToPcm16(float[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] bytes = new byte[samples.Length * 2];

            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = Math.Max(-1f, Math.Min(1f, samples[i]));
                short value = (short)(clamped < 0f ? clamped * 32768f : clamped * 32767f);
                int offset = i * 2;
                bytes[offset] = (byte)(value & 0xff);
                bytes[offset + 1] = (byte)((value >> 8) & 0xff);
            }

            return bytes;
        }

        public static float[] Pcm16ToFloat(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2)
            {
                return Array.Empty<float>();
            }

            int sampleCount = bytes.Length / 2;
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                int offset = i * 2;
                short value = (short)(bytes[offset] | (bytes[offset + 1] << 8));
                samples[i] = value < 0 ? value / 32768f : value / 32767f;
            }

            return samples;
        }

        public static float[] ResampleMono(float[] samples, int sourceRate, int targetRate)
        {
            if (samples == null || samples.Length == 0)
            {
                return Array.Empty<float>();
            }

            if (sourceRate == targetRate)
            {
                return samples;
            }

            int outputLength = Math.Max(1, (int)Math.Round(samples.Length * (targetRate / (double)sourceRate)));
            float[] output = new float[outputLength];

            for (int i = 0; i < output.Length; i++)
            {
                double sourcePosition = i * (sourceRate / (double)targetRate);
                int index = (int)Math.Floor(sourcePosition);
                int nextIndex = Math.Min(samples.Length - 1, index + 1);
                float blend = (float)(sourcePosition - index);
                output[i] = samples[index] + ((samples[nextIndex] - samples[index]) * blend);
            }

            return output;
        }
    }
}
