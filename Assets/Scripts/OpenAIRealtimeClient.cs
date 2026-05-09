using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
        private readonly SemaphoreSlim socketLock = new SemaphoreSlim(1, 1);
        private NiaApiClient niaClient;
        private ClientWebSocket answerSocket;
        private string answerSessionVoice;
        private string answerSessionNiaConfigurationKey;

        public OpenAIRealtimeClient(string apiKey, string model, NiaApiClient niaClient)
        {
            this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
            this.model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            this.niaClient = niaClient;
            Log($"Client configured. model={this.model}, openAiKeySet={!string.IsNullOrWhiteSpace(this.apiKey)}, niaEnabled={CanUseNiaSearch()}");
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ReadApiKey());

        public bool Matches(string apiKey, string model)
        {
            string normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
            string normalizedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            return string.Equals(this.apiKey, normalizedApiKey, StringComparison.Ordinal)
                && string.Equals(this.model, normalizedModel, StringComparison.Ordinal);
        }

        public void SetNiaClient(NiaApiClient niaClient)
        {
            this.niaClient = niaClient;
            Log($"NIA client updated. niaEnabled={CanUseNiaSearch()}");
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
            Log($"Warm-up requested. model={model}, voice={safeVoice}, niaEnabled={CanUseNiaSearch()}");

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
            Log($"Voice question started. inputSamples={monoSamples.Length}, inputRate={sampleRate}, realtimeSamples={realtimeSamples.Length}, model={model}, voice={safeVoice}, niaEnabled={CanUseNiaSearch()}");

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
            Log($"Transcription socket connecting. model={model}, inputSamples={monoSamples.Length}, inputRate={sampleRate}");
            await socket.ConnectAsync(uri, requestToken);
            Log($"Transcription socket connected. state={socket.State}");

            await WaitForEventTypeAsync(socket, "session.created", requestToken);
            await SendJsonAsync(socket, BuildSessionUpdate(), requestToken);
            await WaitForEventTypeAsync(socket, "session.updated", requestToken);
            Log("Transcription realtime session updated.");

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
                Log("Transcription socket closed normally.");
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
            Log($"Speech socket connecting. model={model}, voice={safeVoice}, textLength={text.Trim().Length}");
            await socket.ConnectAsync(uri, requestToken);
            Log($"Speech socket connected. state={socket.State}");

            await WaitForEventTypeAsync(socket, "session.created", requestToken);
            await SendJsonAsync(socket, BuildSpeechSessionUpdate(safeVoice), requestToken);
            await WaitForEventTypeAsync(socket, "session.updated", requestToken);
            Log("Speech realtime session updated.");
            await SendJsonAsync(socket, BuildSpeechResponseCreate(text.Trim()), requestToken);

            RealtimeAudioResult response = await ReadAudioResponseAsync(socket, null, requestToken);

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "speech complete", CancellationToken.None);
                Log("Speech socket closed normally.");
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

        private async Task EnsureAnswerSessionAsync(string apiKey, string voice, CancellationToken token)
        {
            string currentNiaConfigurationKey = CurrentNiaConfigurationKey();
            bool niaEnabled = CanUseNiaSearch();

            if (answerSocket != null
                && answerSocket.State == WebSocketState.Open
                && string.Equals(answerSessionVoice, voice, StringComparison.Ordinal)
                && string.Equals(answerSessionNiaConfigurationKey, currentNiaConfigurationKey, StringComparison.Ordinal))
            {
                Log($"Reusing realtime answer session. state={answerSocket.State}, voice={voice}, niaEnabled={niaEnabled}");
                return;
            }

            await CloseAnswerSessionAsync("answer session replacing");

            answerSocket = new ClientWebSocket();
            answerSocket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            Uri uri = new Uri($"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}");
            Log($"Answer socket connecting. model={model}, voice={voice}, niaEnabled={niaEnabled}");
            await answerSocket.ConnectAsync(uri, token);
            Log($"Answer socket connected. state={answerSocket.State}");

            await WaitForEventTypeAsync(answerSocket, "session.created", token);
            await SendJsonAsync(answerSocket, BuildAnswerSessionUpdate(voice), token);
            await WaitForEventTypeAsync(answerSocket, "session.updated", token);

            answerSessionVoice = voice;
            answerSessionNiaConfigurationKey = currentNiaConfigurationKey;
            Log($"Answer realtime session updated. voice={voice}, niaToolRegistered={niaEnabled}");
        }

        private async Task CloseAnswerSessionAsync(string reason)
        {
            ClientWebSocket socket = answerSocket;
            answerSocket = null;
            answerSessionVoice = null;
            answerSessionNiaConfigurationKey = null;

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

            if (CanUseNiaSearch())
            {
                session["tools"] = BuildNiaSearchTools();
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
            string instructions = "Answer short push-to-talk voice questions for the Unity game Underwater.";

            if (CanUseNiaSearch())
            {
                instructions += " Route questions about Codex threads, pets, archived pets, nearby/facing things, reef state, local app-server state, or the current Underwater world to the provided game context only. Never call nia_search for those local thread or pet questions. For all other external knowledge, current information, technical docs, code, libraries, research, or anything that benefits from search, call nia_search before answering.";
            }

            return instructions;
        }

        private static List<object> BuildNiaSearchTools()
        {
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["name"] = "nia_search",
                    ["description"] = "Search Nia for external knowledge only. Do not use for local Underwater/Codex app-server questions about threads, pets, archived pets, nearby/facing objects, reef state, or current game context. Use universal for Nia's pre-indexed repositories, docs, and papers; web for current web information; query for configured Nia workspace sources; deep for multi-step research.",
                    ["parameters"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["query"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["description"] = "A concise natural-language search query."
                            },
                            ["mode"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["enum"] = new List<object> { "universal", "web", "query", "deep" },
                                ["description"] = "Search mode. Prefer universal unless the user specifically needs live web/current information, configured workspace sources, or deep research."
                            }
                        },
                        ["required"] = new List<object> { "query" }
                    }
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

        private Dictionary<string, object> BuildSpeechSessionUpdate(string voice)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "session.update",
                ["session"] = new Dictionary<string, object>
                {
                    ["type"] = "realtime",
                    ["model"] = model,
                    ["output_modalities"] = new List<object> { "audio" },
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

        private static Dictionary<string, object> BuildSpeechResponseCreate(string text)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "response.create",
                ["response"] = new Dictionary<string, object>
                {
                    ["output_modalities"] = new List<object> { "audio" },
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
                            string output = await ExecuteFunctionCallAsync(functionCalls[i], token);
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
                    Log($"Realtime audio response done. pcmBytes={pcmBytes.Length}, transcriptLength={transcript.Length}, textLength={text.Length}, niaToolRounds={toolCallRounds}");
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

        private async Task<string> ExecuteFunctionCallAsync(RealtimeFunctionCall functionCall, CancellationToken token)
        {
            if (functionCall == null || !string.Equals(functionCall.Name, "nia_search", StringComparison.Ordinal))
            {
                string toolName = functionCall == null ? "null" : functionCall.Name;
                LogWarning($"Unsupported realtime tool call requested. name={toolName}");
                return AquariumDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "Unsupported realtime tool call."
                });
            }

            if (!CanUseNiaSearch())
            {
                LogWarning("Realtime requested NIA search, but niaApiKey is not configured.");
                return AquariumDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = $"Set niaApiKey in {UnderwaterUserSettings.RelativePath} to enable Nia search."
                });
            }

            Dictionary<string, object> arguments = AquariumDirectorBridge.MiniJson.Deserialize(functionCall.Arguments) as Dictionary<string, object>;
            string query = ReadString(arguments, "query") ?? ReadString(arguments, "question");
            string mode = ReadString(arguments, "mode");

            if (string.IsNullOrWhiteSpace(query))
            {
                LogWarning("Realtime requested NIA search without a query.");
                return AquariumDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
                {
                    ["error"] = "Nia search requires a non-empty query."
                });
            }

            try
            {
                Log($"NIA search started from realtime tool. mode={NormalizedModeForLog(mode)}, query=\"{Shorten(query.Trim(), 120)}\"");
                NiaSearchResult result = await niaClient.QueryAsync(query, mode, token);
                int sourceCount = result.SourceLabels == null ? 0 : result.SourceLabels.Length;
                Log($"NIA search completed. answerLength={(result.Answer ?? string.Empty).Length}, sourceCount={sourceCount}");
                Dictionary<string, object> output = new Dictionary<string, object>
                {
                    ["answer"] = result.Answer ?? string.Empty,
                    ["sources"] = ToObjectList(result.SourceLabels)
                };
                return AquariumDirectorBridge.MiniJson.Serialize(output);
            }
            catch (Exception ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                LogWarning($"NIA search failed. {Shorten(message, 240)}");
                return AquariumDirectorBridge.MiniJson.Serialize(new Dictionary<string, object>
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

        private bool CanUseNiaSearch()
        {
            return niaClient != null && niaClient.HasApiKey;
        }

        private string CurrentNiaConfigurationKey()
        {
            return CanUseNiaSearch() ? niaClient.ConfigurationKey : string.Empty;
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

        private static string NormalizedModeForLog(string mode)
        {
            return string.IsNullOrWhiteSpace(mode) ? NiaApiClient.DefaultSearchMode : mode.Trim();
        }

        private static List<object> ToObjectList(string[] values)
        {
            List<object> list = new List<object>();

            if (values == null)
            {
                return list;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    list.Add(values[i]);
                }
            }

            return list;
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
