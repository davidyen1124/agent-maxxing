using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Forest
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
        private readonly Func<Dictionary<string, object>, string> worldCommandHandler;
        private readonly Func<Dictionary<string, object>, string> workThreadCommandHandler;
        private readonly SemaphoreSlim socketLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket answerSocket;
        private string answerSessionVoice;

        public OpenAIRealtimeClient(
            string apiKey,
            string model,
            Func<Dictionary<string, object>, string> worldCommandHandler,
            Func<Dictionary<string, object>, string> workThreadCommandHandler)
        {
            this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
            this.model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            this.worldCommandHandler = worldCommandHandler;
            this.workThreadCommandHandler = workThreadCommandHandler;
            Log($"Client configured. model={this.model}, openAiKeySet={!string.IsNullOrWhiteSpace(this.apiKey)}, worldCommandsEnabled={CanUseWorldCommands()}, workThreadCommandsEnabled={CanUseWorkThreadCommands()}");
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ReadApiKey());

        public bool Matches(string apiKey, string model)
        {
            string normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
            string normalizedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            return string.Equals(this.apiKey, normalizedApiKey, StringComparison.Ordinal)
                && string.Equals(this.model, normalizedModel, StringComparison.Ordinal);
        }

        public async Task WarmUpAnswerSessionAsync(string voice, CancellationToken token)
        {
            string apiKey = ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogWarning("Warm-up skipped because openAiApiKey is not set.");
                return;
            }

            string safeVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();
            Log($"Warm-up requested. model={model}, voice={safeVoice}");

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            CancellationToken requestToken = timeoutCts.Token;

            await socketLock.WaitAsync(requestToken);

            try
            {
                await EnsureAnswerSessionAsync(apiKey, safeVoice, requestToken);
            }
            finally
            {
                socketLock.Release();
            }
        }

        public async Task<RealtimeAudioResult> AskQuestionAsync(float[] monoSamples, int sampleRate, string instructions, string voice, CancellationToken token)
        {
            string apiKey = ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"Set openAiApiKey in {ForestUserSettings.RelativePath} to enable voice questions.");
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
                ? "Answer the user's spoken question or request clearly and briefly."
                : instructions.Trim();
            float[] realtimeSamples = ResampleMono(monoSamples, sampleRate, RealtimeInputSampleRate);
            string base64Audio = Convert.ToBase64String(FloatToPcm16(realtimeSamples));
            Log($"Voice question started. inputSamples={monoSamples.Length}, inputRate={sampleRate}, realtimeSamples={realtimeSamples.Length}, model={model}, voice={safeVoice}");

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
            CancellationToken requestToken = timeoutCts.Token;

            await socketLock.WaitAsync(requestToken);

            RealtimeAudioResult response = null;
            Exception lastException = null;

            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await EnsureAnswerSessionAsync(apiKey, safeVoice, requestToken);

                        await SendJsonAsync(
                            answerSocket,
                            new Dictionary<string, object>
                            {
                                ["type"] = "input_audio_buffer.append",
                                ["audio"] = base64Audio
                            },
                            requestToken);

                        await SendJsonAsync(answerSocket, new Dictionary<string, object> { ["type"] = "input_audio_buffer.commit" }, requestToken);
                        await SendJsonAsync(answerSocket, BuildAnswerResponseCreate(safeInstructions), requestToken);

                        response = await ReadAudioResponseAsync(answerSocket, safeInstructions, requestToken);
                        break;
                    }
                    catch (Exception ex) when (attempt == 0 && IsRecoverableRealtimeSocketException(ex))
                    {
                        lastException = ex;
                        LogWarning($"Recoverable realtime socket issue; reconnecting once. {Shorten(ExceptionMessage(ex), 180)}");
                        await CloseAnswerSessionAsync("answer session reconnecting");
                    }
                }

                if (response == null)
                {
                    throw lastException ?? new InvalidOperationException("OpenAI Realtime returned no response.");
                }
            }
            catch
            {
                await CloseAnswerSessionAsync("answer session reset");
                throw;
            }
            finally
            {
                socketLock.Release();
            }

            if (response.Samples == null || response.Samples.Length == 0)
            {
                throw new InvalidOperationException("OpenAI Realtime returned no playable audio.");
            }

            Log($"Voice question completed. outputSamples={response.Samples.Length}, outputRate={response.SampleRate}, transcriptPresent={!string.IsNullOrWhiteSpace(response.Transcript)}");
            return response;
        }

        public async Task CloseAsync()
        {
            await socketLock.WaitAsync();

            try
            {
                await CloseAnswerSessionAsync("client closing");
            }
            finally
            {
                socketLock.Release();
            }
        }

        private async Task EnsureAnswerSessionAsync(string apiKey, string voice, CancellationToken token)
        {
            if (answerSocket != null
                && answerSocket.State == WebSocketState.Open
                && string.Equals(answerSessionVoice, voice, StringComparison.Ordinal))
            {
                Log($"Reusing realtime answer session. state={answerSocket.State}, voice={voice}");
                return;
            }

            await CloseAnswerSessionAsync("answer session replacing");

            answerSocket = new ClientWebSocket();
            answerSocket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            Uri uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
            Log($"Answer socket connecting. model={model}, voice={voice}");
            await answerSocket.ConnectAsync(uri, token);
            Log($"Answer socket connected. state={answerSocket.State}");

            await WaitForEventTypeAsync(answerSocket, "session.created", token);
            await SendJsonAsync(answerSocket, BuildAnswerSessionUpdate(voice), token);
            await WaitForEventTypeAsync(answerSocket, "session.updated", token);

            answerSessionVoice = voice;
            Log($"Answer realtime session updated. voice={voice}");
        }

        private async Task CloseAnswerSessionAsync(string reason)
        {
            ClientWebSocket socket = answerSocket;
            answerSocket = null;
            answerSessionVoice = null;

            if (socket == null)
            {
                return;
            }

            Log($"Closing realtime answer session. reason={reason}, state={socket.State}");
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                    Log("Realtime answer session closed normally.");
                }
            }
            catch (Exception ex)
            {
                LogWarning($"Realtime answer session close failed. {Shorten(ExceptionMessage(ex), 180)}");
            }
            finally
            {
                socket.Dispose();
            }
        }

        private Dictionary<string, object> BuildAnswerSessionUpdate(string voice)
        {
            Dictionary<string, object> session = new Dictionary<string, object>
            {
                ["type"] = "realtime",
                ["model"] = model,
                ["output_modalities"] = new List<object> { "audio" },
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
                ["instructions"] = BuildAnswerSessionInstructions()
            };

            List<object> tools = BuildRealtimeTools();

            if (tools.Count > 0)
            {
                session["tools"] = tools;
                session["tool_choice"] = "auto";
            }

            return new Dictionary<string, object>
            {
                ["type"] = "session.update",
                ["session"] = session
            };
        }

        private string BuildAnswerSessionInstructions()
        {
            string instructions = "Answer short push-to-talk voice questions and requests for the Unity game Forest.";

            if (CanUseWorldCommands())
            {
                instructions += " When the player asks to change the world, weather, fog, rain, storm, snow, clouds, drizzle, flurries, blizzards, lightning, lighting, morning, noon, afternoon, evening, day, dawn, sunset, or night, call set_world_atmosphere before speaking.";
            }

            if (CanUseWorkThreadCommands())
            {
                instructions += " When the player asks a question, reports a bug, requests an investigation, or asks for a new feature specifically about this game/project, collect their exact request and call create_game_thread before speaking.";
            }

            return instructions;
        }

        private List<object> BuildRealtimeTools()
        {
            List<object> tools = new List<object>();

            if (CanUseWorldCommands())
            {
                tools.Add(BuildWorldAtmosphereTool());
            }

            if (CanUseWorkThreadCommands())
            {
                tools.Add(BuildWorkThreadTool());
            }

            return tools;
        }

        private static Dictionary<string, object> BuildWorldAtmosphereTool()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "function",
                ["name"] = "set_world_atmosphere",
                ["description"] = "Change the visible Forest Unity world atmosphere. Use this for player requests about weather, rain, storms, fog, snow, clouds, drizzle, flurries, blizzards, lightning, lighting, or time of day.",
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["time_of_day"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = new List<object> { "preserve", "dawn", "day", "sunset", "night" },
                            ["description"] = "Requested time of day. Map morning, sunrise, daybreak, or first light to dawn; noon, midday, afternoon, sunny, or bright to day; evening, dusk, twilight, golden hour, or sundown to sunset; midnight, moonlight, moonlit, dark, or nighttime to night. Use preserve when the player only asks for weather."
                        },
                        ["weather"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["enum"] = new List<object> { "preserve", "clear", "fog", "rain", "storm", "snow" },
                            ["description"] = "Requested weather. Map sunny/clear sky to clear; cloudy, overcast, haze, or mist to fog; drizzle, showers, or downpour to rain; thunder, lightning, tempest, or squall to storm; flurries, blizzard, sleet, hail, icy, or frost to snow. Use preserve when the player only asks for time or lighting."
                        },
                        ["intensity"] = new Dictionary<string, object>
                        {
                            ["type"] = "number",
                            ["description"] = "Strength from 0 to 1. Use lower values for drizzle or light flurries and higher values for downpour, thick fog, blizzard, thunderstorm, or dramatic night."
                        },
                        ["mood"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional short atmosphere description, such as calm, spooky, cinematic, cozy, or dramatic."
                        }
                    }
                }
            };
        }

        private static Dictionary<string, object> BuildWorkThreadTool()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "function",
                ["name"] = "create_game_thread",
                ["description"] = "Create a Codex work thread from inside this Unity game for a player request specifically about this game/project. Use this instead of answering directly when the user wants game-specific work, investigation, debugging, or implementation, including new features.",
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["request"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The player's exact spoken request about this game/project, including questions, bug reports, investigations, or feature requests."
                        },
                        ["title"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional concise thread title based on the player's request."
                        }
                    },
                    ["required"] = new List<object> { "request" }
                }
            };
        }

        private static Dictionary<string, object> BuildAnswerResponseCreate(string instructions)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "response.create",
                ["response"] = new Dictionary<string, object>
                {
                    ["output_modalities"] = new List<object> { "audio" },
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

        private async Task<RealtimeAudioResult> ReadAudioResponseAsync(ClientWebSocket socket, string followUpInstructions, CancellationToken token)
        {
            using MemoryStream audioBytes = new MemoryStream();
            StringBuilder transcript = new StringBuilder();
            StringBuilder text = new StringBuilder();
            int toolCallRounds = 0;

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
                    List<RealtimeFunctionCall> functionCalls = ExtractFunctionCalls(message);

                    if (functionCalls.Count > 0)
                    {
                        Log($"Realtime response requested {functionCalls.Count} tool call(s). round={toolCallRounds + 1}");
                        if (toolCallRounds >= 2)
                        {
                            throw new InvalidOperationException("OpenAI Realtime requested too many consecutive tool calls.");
                        }

                        toolCallRounds++;

                        for (int i = 0; i < functionCalls.Count; i++)
                        {
                            string output = ExecuteFunctionCall(functionCalls[i]);
                            await SendJsonAsync(socket, BuildFunctionCallOutput(functionCalls[i].CallId, output), token);
                        }

                        audioBytes.SetLength(0);
                        transcript.Clear();
                        text.Clear();
                        await SendJsonAsync(
                            socket,
                            BuildAnswerResponseCreate(
                                string.IsNullOrWhiteSpace(followUpInstructions)
                                    ? "Use the tool result to answer the user clearly and briefly."
                                    : followUpInstructions),
                            token);
                        continue;
                    }

                    byte[] pcmBytes = audioBytes.ToArray();
                    Log($"Realtime audio response done. pcmBytes={pcmBytes.Length}, transcriptLength={transcript.Length}, textLength={text.Length}, toolRounds={toolCallRounds}");
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

        private string ExecuteFunctionCall(RealtimeFunctionCall functionCall)
        {
            if (functionCall == null)
            {
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "Unsupported realtime tool call."
                });
            }

            if (string.Equals(functionCall.Name, "set_world_atmosphere", StringComparison.Ordinal))
            {
                return ExecuteWorldAtmosphereCommand(functionCall);
            }

            if (string.Equals(functionCall.Name, "create_game_thread", StringComparison.Ordinal))
            {
                return ExecuteWorkThreadCommand(functionCall);
            }

            string toolName = functionCall == null ? "null" : functionCall.Name;
            LogWarning($"Unsupported realtime tool call requested. name={toolName}");
            return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
            {
                ["error"] = "Unsupported realtime tool call."
            });
        }

        private string ExecuteWorkThreadCommand(RealtimeFunctionCall functionCall)
        {
            if (!CanUseWorkThreadCommands())
            {
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "The work thread command bridge is unavailable."
                });
            }

            Dictionary<string, object> arguments = ForestDirectorBridge.MiniJson.Deserialize(functionCall.Arguments) as Dictionary<string, object>
                ?? new Dictionary<string, object>();

            try
            {
                string output = workThreadCommandHandler(arguments);
                return string.IsNullOrWhiteSpace(output) ? "{}" : output;
            }
            catch (Exception ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = Shorten(message, 400)
                });
            }
        }

        private string ExecuteWorldAtmosphereCommand(RealtimeFunctionCall functionCall)
        {
            if (!CanUseWorldCommands())
            {
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "The Unity world command bridge is unavailable."
                });
            }

            Dictionary<string, object> arguments = ForestDirectorBridge.MiniJson.Deserialize(functionCall.Arguments) as Dictionary<string, object>
                ?? new Dictionary<string, object>();

            try
            {
                string output = worldCommandHandler(arguments);
                return string.IsNullOrWhiteSpace(output) ? "{}" : output;
            }
            catch (Exception ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                return ForestDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = Shorten(message, 400)
                });
            }
        }

        private static Dictionary<string, object> BuildFunctionCallOutput(string callId, string output)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "conversation.item.create",
                ["item"] = new Dictionary<string, object>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = callId,
                    ["output"] = string.IsNullOrWhiteSpace(output) ? "{}" : output
                }
            };
        }

        private static bool IsRecoverableRealtimeSocketException(Exception ex)
        {
            if (ex is WebSocketException)
            {
                return true;
            }

            if (ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message))
            {
                return ex.Message.IndexOf("socket closed", StringComparison.OrdinalIgnoreCase) >= 0
                    || ex.Message.IndexOf("closed unexpectedly", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private bool CanUseWorldCommands()
        {
            return worldCommandHandler != null;
        }

        private bool CanUseWorkThreadCommands()
        {
            return workThreadCommandHandler != null;
        }

        private static void Log(string message)
        {
            Debug.Log($"[OpenAI Realtime] {message}");
        }

        private static void LogWarning(string message)
        {
            Debug.LogWarning($"[OpenAI Realtime] {message}");
        }

        private static string ExceptionMessage(Exception ex)
        {
            if (ex == null)
            {
                return "Unknown error";
            }

            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        private static List<RealtimeFunctionCall> ExtractFunctionCalls(Dictionary<string, object> message)
        {
            List<RealtimeFunctionCall> functionCalls = new List<RealtimeFunctionCall>();
            object output = Traverse(message, "response", "output");

            if (!(output is List<object> items))
            {
                return functionCalls;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (!(items[i] is Dictionary<string, object> item))
                {
                    continue;
                }

                if (!string.Equals(ReadString(item, "type"), "function_call", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = ReadString(item, "name");
                string callId = ReadString(item, "call_id");
                string arguments = ReadString(item, "arguments") ?? "{}";

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(callId))
                {
                    functionCalls.Add(new RealtimeFunctionCall
                    {
                        Name = name,
                        CallId = callId,
                        Arguments = arguments
                    });
                }
            }

            return functionCalls;
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
            string json = ForestDirectorBridge.MiniJson.Serialize(payload);
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

            if (!(ForestDirectorBridge.MiniJson.Deserialize(json) is Dictionary<string, object> message))
            {
                throw new InvalidOperationException("OpenAI Realtime returned an unreadable event.");
            }

            return message;
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

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";
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

    internal sealed class RealtimeFunctionCall
    {
        public string Name;
        public string CallId;
        public string Arguments;
    }
}
