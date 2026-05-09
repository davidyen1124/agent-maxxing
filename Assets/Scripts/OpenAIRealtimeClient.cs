using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Underwater
{
    internal sealed class OpenAIRealtimeClient
    {
        private const int RealtimeInputSampleRate = 24000;
        private const int RealtimeOutputSampleRate = 24000;
        private const int ReceiveBufferSize = 8192;
        private const string DefaultModel = "gpt-realtime-2";
        private const string DefaultVoice = "marin";

        private readonly string apiKey;
        private readonly string model;

        public OpenAIRealtimeClient(string apiKey, string model)
        {
            this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
            this.model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ReadApiKey());

        public async Task<RealtimeAudioResult> AskQuestionAsync(float[] monoSamples, int sampleRate, string instructions, string voice, CancellationToken token)
        {
            string apiKey = ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"Set openAiApiKey in {UnderwaterUserSettings.RelativePath} to enable voice questions.");
            }

            if (monoSamples == null || monoSamples.Length == 0)
            {
                throw new ArgumentException("Voice audio cannot be empty.", nameof(monoSamples));
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Voice audio sample rate must be positive.");
            }

            string safeVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();
            string safeInstructions = string.IsNullOrWhiteSpace(instructions)
                ? "Answer the user's spoken question clearly and briefly."
                : instructions.Trim();
            float[] realtimeSamples = ResampleMono(monoSamples, sampleRate, RealtimeInputSampleRate);
            string base64Audio = Convert.ToBase64String(FloatToPcm16(realtimeSamples));

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
            CancellationToken requestToken = timeoutCts.Token;

            using ClientWebSocket socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            Uri uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
            await socket.ConnectAsync(uri, requestToken);

            await WaitForEventTypeAsync(socket, "session.created", requestToken);
            await SendJsonAsync(socket, BuildAnswerSessionUpdate(safeInstructions, safeVoice), requestToken);
            await WaitForEventTypeAsync(socket, "session.updated", requestToken);

            await SendJsonAsync(
                socket,
                new Dictionary<string, object>
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = base64Audio
                },
                requestToken);

            await SendJsonAsync(socket, new Dictionary<string, object> { ["type"] = "input_audio_buffer.commit" }, requestToken);
            await SendJsonAsync(socket, BuildAnswerResponseCreate(safeInstructions, safeVoice), requestToken);

            RealtimeAudioResult response = await ReadAudioResponseAsync(socket, requestToken);

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "answer complete", CancellationToken.None);
            }

            if (response.Samples == null || response.Samples.Length == 0)
            {
                throw new InvalidOperationException("OpenAI Realtime returned no playable audio.");
            }

            return response;
        }

        public async Task<string> TranscribeQuestionAsync(float[] monoSamples, int sampleRate, CancellationToken token)
        {
            string apiKey = ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"Set openAiApiKey in {UnderwaterUserSettings.RelativePath} to enable voice questions.");
            }

            if (monoSamples == null || monoSamples.Length == 0)
            {
                throw new ArgumentException("Voice audio cannot be empty.", nameof(monoSamples));
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Voice audio sample rate must be positive.");
            }

            float[] realtimeSamples = ResampleMono(monoSamples, sampleRate, RealtimeInputSampleRate);
            string base64Audio = Convert.ToBase64String(FloatToPcm16(realtimeSamples));

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
            CancellationToken requestToken = timeoutCts.Token;

            using ClientWebSocket socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            Uri uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
            await socket.ConnectAsync(uri, requestToken);

            await WaitForEventTypeAsync(socket, "session.created", requestToken);
            await SendJsonAsync(socket, BuildSessionUpdate(), requestToken);
            await WaitForEventTypeAsync(socket, "session.updated", requestToken);

            await SendJsonAsync(
                socket,
                new Dictionary<string, object>
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = base64Audio
                },
                requestToken);

            await SendJsonAsync(socket, new Dictionary<string, object> { ["type"] = "input_audio_buffer.commit" }, requestToken);
            await SendJsonAsync(
                socket,
                new Dictionary<string, object>
                {
                    ["type"] = "response.create",
                    ["response"] = new Dictionary<string, object>
                    {
                        ["output_modalities"] = new List<object> { "text" },
                        ["instructions"] = "Listen to the user's audio and return only the user's question or request as concise text. Do not answer it."
                    }
                },
                requestToken);

            string response = await ReadResponseTextAsync(socket, requestToken);

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "voice question complete", CancellationToken.None);
            }

            string cleaned = CleanTranscript(response);

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                throw new InvalidOperationException("OpenAI Realtime returned no usable question text.");
            }

            return cleaned;
        }

        public async Task<RealtimeAudioResult> GenerateSpeechAsync(string text, string voice, CancellationToken token)
        {
            string apiKey = ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"Set openAiApiKey in {UnderwaterUserSettings.RelativePath} to enable voice questions.");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Speech text cannot be empty.", nameof(text));
            }

            string safeVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
            CancellationToken requestToken = timeoutCts.Token;

            using ClientWebSocket socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            Uri uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
            await socket.ConnectAsync(uri, requestToken);

            await WaitForEventTypeAsync(socket, "session.created", requestToken);
            await SendJsonAsync(socket, BuildSpeechSessionUpdate(safeVoice), requestToken);
            await WaitForEventTypeAsync(socket, "session.updated", requestToken);
            await SendJsonAsync(socket, BuildSpeechResponseCreate(text.Trim(), safeVoice), requestToken);

            RealtimeAudioResult response = await ReadAudioResponseAsync(socket, requestToken);

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "speech complete", CancellationToken.None);
            }

            if (response.Samples == null || response.Samples.Length == 0)
            {
                throw new InvalidOperationException("OpenAI Realtime returned no playable audio.");
            }

            return response;
        }

        private Dictionary<string, object> BuildSessionUpdate()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "session.update",
                ["session"] = new Dictionary<string, object>
                {
                    ["type"] = "realtime",
                    ["model"] = model,
                    ["output_modalities"] = new List<object> { "text" },
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["input"] = new Dictionary<string, object>
                        {
                            ["format"] = new Dictionary<string, object>
                            {
                                ["type"] = "audio/pcm",
                                ["rate"] = RealtimeInputSampleRate
                            },
                            ["turn_detection"] = null
                        }
                    },
                    ["instructions"] = "You convert short player voice clips into clean text questions for a Unity game assistant."
                }
            };
        }

        private Dictionary<string, object> BuildAnswerSessionUpdate(string instructions, string voice)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "session.update",
                ["session"] = new Dictionary<string, object>
                {
                    ["type"] = "realtime",
                    ["model"] = model,
                    ["output_modalities"] = new List<object> { "audio", "text" },
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["input"] = new Dictionary<string, object>
                        {
                            ["format"] = new Dictionary<string, object>
                            {
                                ["type"] = "audio/pcm",
                                ["rate"] = RealtimeInputSampleRate
                            },
                            ["turn_detection"] = null
                        },
                        ["output"] = new Dictionary<string, object>
                        {
                            ["format"] = new Dictionary<string, object>
                            {
                                ["type"] = "audio/pcm",
                                ["rate"] = RealtimeOutputSampleRate
                            },
                            ["voice"] = voice
                        }
                    },
                    ["instructions"] = instructions
                }
            };
        }

        private static Dictionary<string, object> BuildAnswerResponseCreate(string instructions, string voice)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "response.create",
                ["response"] = new Dictionary<string, object>
                {
                    ["output_modalities"] = new List<object> { "audio", "text" },
                    ["voice"] = voice,
                    ["instructions"] = instructions,
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["output"] = new Dictionary<string, object>
                        {
                            ["format"] = new Dictionary<string, object>
                            {
                                ["type"] = "audio/pcm",
                                ["rate"] = RealtimeOutputSampleRate
                            }
                        }
                    }
                }
            };
        }

        private Dictionary<string, object> BuildSpeechSessionUpdate(string voice)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "session.update",
                ["session"] = new Dictionary<string, object>
                {
                    ["type"] = "realtime",
                    ["model"] = model,
                    ["output_modalities"] = new List<object> { "audio", "text" },
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["output"] = new Dictionary<string, object>
                        {
                            ["format"] = new Dictionary<string, object>
                            {
                                ["type"] = "audio/pcm",
                                ["rate"] = RealtimeOutputSampleRate
                            },
                            ["voice"] = voice
                        }
                    },
                    ["instructions"] = "Speak short Unity game assistant answers warmly and clearly."
                }
            };
        }

        private static Dictionary<string, object> BuildSpeechResponseCreate(string text, string voice)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "response.create",
                ["response"] = new Dictionary<string, object>
                {
                    ["output_modalities"] = new List<object> { "audio", "text" },
                    ["voice"] = voice,
                    ["input"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "message",
                            ["role"] = "user",
                            ["content"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["type"] = "input_text",
                                    ["text"] = $"Speak this answer exactly, naturally, and without adding extra words: {text}"
                                }
                            }
                        }
                    },
                    ["audio"] = new Dictionary<string, object>
                    {
                        ["output"] = new Dictionary<string, object>
                        {
                            ["format"] = new Dictionary<string, object>
                            {
                                ["type"] = "audio/pcm",
                                ["rate"] = RealtimeOutputSampleRate
                            }
                        }
                    }
                }
            };
        }

        private static async Task<RealtimeAudioResult> ReadAudioResponseAsync(ClientWebSocket socket, CancellationToken token)
        {
            using MemoryStream audioBytes = new MemoryStream();
            StringBuilder transcript = new StringBuilder();
            StringBuilder text = new StringBuilder();

            while (socket.State == WebSocketState.Open)
            {
                Dictionary<string, object> message = await ReceiveJsonAsync(socket, token);
                string type = ReadString(message, "type");

                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    string detail = ReadString(message, "error", "message") ?? "OpenAI Realtime audio request failed.";
                    throw new InvalidOperationException(detail);
                }

                if (string.Equals(type, "response.output_audio.delta", StringComparison.Ordinal))
                {
                    string delta = ReadString(message, "delta");

                    if (!string.IsNullOrWhiteSpace(delta))
                    {
                        byte[] chunk = Convert.FromBase64String(delta);
                        audioBytes.Write(chunk, 0, chunk.Length);
                    }

                    continue;
                }

                if (string.Equals(type, "response.output_audio_transcript.delta", StringComparison.Ordinal))
                {
                    transcript.Append(ReadString(message, "delta"));
                    continue;
                }

                if (string.Equals(type, "response.output_text.delta", StringComparison.Ordinal))
                {
                    text.Append(ReadString(message, "delta"));
                    continue;
                }

                if (string.Equals(type, "response.output_text.done", StringComparison.Ordinal))
                {
                    string finalText = ReadString(message, "text");

                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        text.Clear();
                        text.Append(finalText);
                    }

                    continue;
                }

                if (string.Equals(type, "response.output_audio_transcript.done", StringComparison.Ordinal))
                {
                    string finalTranscript = ReadString(message, "transcript");

                    if (!string.IsNullOrWhiteSpace(finalTranscript))
                    {
                        transcript.Clear();
                        transcript.Append(finalTranscript);
                    }

                    continue;
                }

                if (string.Equals(type, "response.done", StringComparison.Ordinal))
                {
                    byte[] pcmBytes = audioBytes.ToArray();
                    return new RealtimeAudioResult
                    {
                        Samples = Pcm16ToFloat(pcmBytes),
                        SampleRate = RealtimeOutputSampleRate,
                        Transcript = string.IsNullOrWhiteSpace(transcript.ToString())
                            ? text.ToString().Trim()
                            : transcript.ToString().Trim()
                    };
                }
            }

            throw new InvalidOperationException("OpenAI Realtime socket closed before returning audio.");
        }

        private static async Task<string> ReadResponseTextAsync(ClientWebSocket socket, CancellationToken token)
        {
            StringBuilder incrementalText = new StringBuilder();

            while (socket.State == WebSocketState.Open)
            {
                Dictionary<string, object> message = await ReceiveJsonAsync(socket, token);
                string type = ReadString(message, "type");

                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    string detail = ReadString(message, "error", "message") ?? "OpenAI Realtime request failed.";
                    throw new InvalidOperationException(detail);
                }

                if (string.Equals(type, "response.output_text.delta", StringComparison.Ordinal))
                {
                    incrementalText.Append(ReadString(message, "delta"));
                    continue;
                }

                if (string.Equals(type, "response.output_text.done", StringComparison.Ordinal))
                {
                    string text = ReadString(message, "text");

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                if (string.Equals(type, "response.done", StringComparison.Ordinal))
                {
                    string finalText = ExtractResponseText(message);
                    return string.IsNullOrWhiteSpace(finalText) ? incrementalText.ToString() : finalText;
                }
            }

            throw new InvalidOperationException("OpenAI Realtime socket closed before returning a response.");
        }

        private static async Task WaitForEventTypeAsync(ClientWebSocket socket, string expectedType, CancellationToken token)
        {
            while (socket.State == WebSocketState.Open)
            {
                Dictionary<string, object> message = await ReceiveJsonAsync(socket, token);
                string type = ReadString(message, "type");

                if (string.Equals(type, expectedType, StringComparison.Ordinal))
                {
                    return;
                }

                if (string.Equals(type, "error", StringComparison.Ordinal))
                {
                    string detail = ReadString(message, "error", "message") ?? $"OpenAI Realtime failed before {expectedType}.";
                    throw new InvalidOperationException(detail);
                }
            }

            throw new InvalidOperationException($"OpenAI Realtime socket closed before {expectedType}.");
        }

        private static async Task SendJsonAsync(ClientWebSocket socket, Dictionary<string, object> payload, CancellationToken token)
        {
            string json = AquariumDirectorBridge.MiniJson.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private static async Task<Dictionary<string, object>> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[ReceiveBufferSize];
            using MemoryStream stream = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("OpenAI Realtime socket closed unexpectedly.");
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(stream.ToArray());

            if (!(AquariumDirectorBridge.MiniJson.Deserialize(json) is Dictionary<string, object> message))
            {
                throw new InvalidOperationException("OpenAI Realtime returned an unreadable event.");
            }

            return message;
        }

        private static string ExtractResponseText(Dictionary<string, object> message)
        {
            object response = Traverse(message, "response");
            string firstText = FindFirstStringByKey(response, "text", 6);

            if (!string.IsNullOrWhiteSpace(firstText))
            {
                return firstText;
            }

            return FindFirstStringByKey(response, "transcript", 6);
        }

        private static object Traverse(Dictionary<string, object> root, params string[] path)
        {
            object current = root;

            for (int i = 0; i < path.Length; i++)
            {
                if (!(current is Dictionary<string, object> dictionary) || !dictionary.TryGetValue(path[i], out current))
                {
                    return null;
                }
            }

            return current;
        }

        private static string FindFirstStringByKey(object value, string keyName, int depth)
        {
            if (value == null || depth < 0)
            {
                return null;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (string.Equals(pair.Key, keyName, StringComparison.Ordinal) && pair.Value is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    string nested = FindFirstStringByKey(pair.Value, keyName, depth - 1);

                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }
            else if (value is List<object> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string nested = FindFirstStringByKey(list[i], keyName, depth - 1);

                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        private static string ReadString(Dictionary<string, object> root, params string[] path)
        {
            if (root == null)
            {
                return null;
            }

            object current = root;

            for (int i = 0; i < path.Length; i++)
            {
                if (!(current is Dictionary<string, object> dictionary) || !dictionary.TryGetValue(path[i], out current))
                {
                    return null;
                }
            }

            return current as string;
        }

        private static byte[] FloatToPcm16(float[] samples)
        {
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

        private static float[] Pcm16ToFloat(byte[] bytes)
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

        private static float[] ResampleMono(float[] samples, int sourceRate, int targetRate)
        {
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

        private static string CleanTranscript(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string cleaned = text.Trim().Trim('"', '\'', ' ', '\n', '\r', '\t');
            return cleaned;
        }

        private string ReadApiKey()
        {
            return apiKey;
        }
    }

    internal sealed class RealtimeAudioResult
    {
        public float[] Samples;
        public int SampleRate;
        public string Transcript;
    }
}
