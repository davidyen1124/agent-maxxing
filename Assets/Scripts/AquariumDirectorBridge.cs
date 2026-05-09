using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Underwater
{
    public sealed class AquariumDirectorBridge : MonoBehaviour
    {
        [SerializeField] private string codexServerUrl = "ws://127.0.0.1:4500";
        [SerializeField] private float snapshotIntervalSeconds = 1.5f;
        [SerializeField] private float reconnectDelaySeconds = 3f;
        [SerializeField] private bool autoConnect = true;

        private readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pendingResponses =
            new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);

        private UnderwaterGameDirector director;
        private CancellationTokenSource lifecycleCts;
        private Task connectionLoopTask;
        private ClientWebSocket socket;
        private AquariumDirectorSnapshot queuedSnapshot;
        private int requestId;
        private float nextSnapshotAt;

        private string sessionId = "-";
        private string threadId = "-";
        private string turnId = "-";
        private string lastSnapshotSentAtUtc = "never";
        private string lastActionSummary = "none";
        private string lastCodexPhase = "offline";
        private string lastCodexText = "Codex director offline";
        private string lastTool = "-";
        private string lastActionId = "-";
        private int lastSnapshotSequence;
        private bool appServerSocketOpen;
        private bool codexConnected;
        private bool threadReady;
        private bool turnInFlight;

        public string BridgeUrl => codexServerUrl;

        public bool IsUnitySocketOpen => appServerSocketOpen;

        public bool IsThreadReady => threadReady;

        public string SessionId => sessionId;

        public string LastSnapshotSentAtUtc => lastSnapshotSentAtUtc;

        public int LastSnapshotSequence => lastSnapshotSequence;

        public string LastActionSummary => lastActionSummary;

        public string LastCodexPhase => lastCodexPhase;

        public string LastCodexText => lastCodexText;

        public string LastThreadId => threadId;

        public string LastTurnId => turnId;

        public string LastTool => lastTool;

        public string LastActionId => lastActionId;

        public bool IsCodexConnected => codexConnected;

        public void Initialize(UnderwaterGameDirector owningDirector)
        {
            director = owningDirector;
        }

        private void Start()
        {
            if (autoConnect)
            {
                StartBridge();
            }
        }

        private void Update()
        {
            DrainMainThreadActions();

            if (director == null || !threadReady)
            {
                return;
            }

            if (Time.unscaledTime < nextSnapshotAt)
            {
                return;
            }

            nextSnapshotAt = Time.unscaledTime + Mathf.Max(0.5f, snapshotIntervalSeconds);
            AquariumDirectorSnapshot snapshot = director.CreateSnapshot();
            queuedSnapshot = snapshot;
            lastSnapshotSequence = snapshot.sequence;
            lastSnapshotSentAtUtc = snapshot.capturedAtUtc ?? DateTime.UtcNow.ToString("o");

            if (!turnInFlight)
            {
                _ = StartTurnFromSnapshotAsync(snapshot, lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None);
            }
        }

        private void OnDestroy()
        {
            StopBridge();
        }

        public void StartBridge()
        {
            if (connectionLoopTask != null)
            {
                return;
            }

            lifecycleCts = new CancellationTokenSource();
            connectionLoopTask = RunConnectionLoopAsync(lifecycleCts.Token);
        }

        public void StopBridge()
        {
            if (lifecycleCts == null)
            {
                return;
            }

            lifecycleCts.Cancel();
            lifecycleCts.Dispose();
            lifecycleCts = null;

            if (socket != null)
            {
                socket.Dispose();
                socket = null;
            }

            connectionLoopTask = null;
        }

        private async Task RunConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket localSocket = new ClientWebSocket();

                try
                {
                    sessionId = Guid.NewGuid().ToString("N");
                    EnqueueStatus("connecting", $"Connecting to Codex app-server at {codexServerUrl}");
                    Debug.Log($"[AquariumDirectorBridge] Connecting to {codexServerUrl}");
                    await localSocket.ConnectAsync(new Uri(codexServerUrl), token);
                    socket = localSocket;
                    EnqueueMainThread(() =>
                    {
                        appServerSocketOpen = true;
                        SetStatus("socket-open", "Codex app-server socket open");
                        Debug.Log("[AquariumDirectorBridge] WebSocket connected to Codex app-server.");
                    });

                    Task receiveTask = ReceiveLoopAsync(localSocket, token);
                    await InitializeCodexSessionAsync(token);
                    nextSnapshotAt = 0f;
                    await receiveTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    EnqueueMainThread(() =>
                    {
                        codexConnected = false;
                        threadReady = false;
                        turnInFlight = false;
                        SetStatus("reconnecting", $"Codex reconnect pending: {ex.Message}");
                        Debug.LogError($"[AquariumDirectorBridge] Connection loop failure: {ex}");
                    });
                }
                finally
                {
                    if (socket == localSocket)
                    {
                        socket = null;
                    }

                    localSocket.Dispose();

                    EnqueueMainThread(() =>
                    {
                        appServerSocketOpen = false;
                        codexConnected = false;
                        threadReady = false;
                        turnInFlight = false;
                        threadId = "-";
                        turnId = "-";
                    });
                }

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(1f, reconnectDelaySeconds)), token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            EnqueueStatus("offline", "Codex director offline");
        }

        private async Task InitializeCodexSessionAsync(CancellationToken token)
        {
            Dictionary<string, object> initializeResponse = await SendRequestAsync(
                "initialize",
                new Dictionary<string, object>
                {
                    ["clientInfo"] = new Dictionary<string, object>
                    {
                        ["name"] = "underwater_unity_client",
                        ["title"] = "Underwater Unity Client",
                        ["version"] = "0.1.0"
                    },
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["experimentalApi"] = true
                    }
                },
                token);

            string platformFamily = ReadString(initializeResponse, "result", "platformFamily");
            EnqueueStatus("initializing", $"Codex initialized on {platformFamily ?? "unknown-platform"}");
            Debug.Log($"[AquariumDirectorBridge] initialize succeeded on {platformFamily ?? "unknown-platform"}.");
            await SendNotificationAsync("initialized", null, token);
            Debug.Log("[AquariumDirectorBridge] initialized notification sent.");

            Dictionary<string, object> threadResponse = await SendRequestAsync(
                "thread/start",
                new Dictionary<string, object>
                {
                    ["serviceName"] = "aquarium_director",
                    ["baseInstructions"] = BuildDirectorInstructions(),
                    ["dynamicTools"] = BuildDynamicTools(),
                    ["experimentalRawEvents"] = false,
                    ["persistExtendedHistory"] = false
                },
                token);

            string createdThreadId = ReadString(threadResponse, "result", "thread", "id") ?? "-";
            EnqueueMainThread(() =>
            {
                threadId = createdThreadId;
                codexConnected = true;
                threadReady = true;
                SetStatus("connected", $"Aquarium director thread ready ({threadId})");
                Debug.Log($"[AquariumDirectorBridge] thread/start succeeded. Thread id: {threadId}");
            });
        }

        private async Task ReceiveLoopAsync(ClientWebSocket localSocket, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            ArraySegment<byte> segment = new ArraySegment<byte>(buffer);

            while (!token.IsCancellationRequested && localSocket.State == WebSocketState.Open)
            {
                using MemoryStream messageStream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await localSocket.ReceiveAsync(segment, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (localSocket.State == WebSocketState.Open || localSocket.State == WebSocketState.CloseReceived)
                        {
                            await localSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", token);
                        }

                        return;
                    }

                    messageStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string json = Encoding.UTF8.GetString(messageStream.ToArray());

                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                HandleIncomingMessage(json);
            }
        }

        private void HandleIncomingMessage(string json)
        {
            if (!(MiniJson.Deserialize(json) is Dictionary<string, object> root))
            {
                return;
            }

            bool hasId = root.ContainsKey("id");
            bool hasMethod = root.ContainsKey("method");

            if (hasId && !hasMethod)
            {
                ResolvePendingResponse(root);
                return;
            }

            if (!hasMethod)
            {
                return;
            }

            string method = root["method"] as string;

            if (hasId)
            {
                HandleServerRequest(root, method);
                return;
            }

            HandleNotification(root, method);
        }

        private void ResolvePendingResponse(Dictionary<string, object> root)
        {
            int id = Convert.ToInt32(root["id"], CultureInfo.InvariantCulture);

            if (!pendingResponses.TryRemove(id, out TaskCompletionSource<Dictionary<string, object>> responseSource))
            {
                return;
            }

            if (root.ContainsKey("error"))
            {
                string errorMessage = ReadString(root, "error", "message") ?? "Unknown JSON-RPC error";
                responseSource.TrySetException(new InvalidOperationException(errorMessage));
                return;
            }

            responseSource.TrySetResult(root);
        }

        private void HandleServerRequest(Dictionary<string, object> root, string method)
        {
            int serverRequestId = Convert.ToInt32(root["id"], CultureInfo.InvariantCulture);

            if (method != "item/tool/call")
            {
                _ = SendJsonAsync(
                    MiniJson.Serialize(
                        new Dictionary<string, object>
                        {
                            ["id"] = serverRequestId,
                            ["error"] = new Dictionary<string, object>
                            {
                                ["code"] = -32601,
                                ["message"] = $"Unsupported server request: {method}"
                            }
                        }),
                    lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None);
                return;
            }

            Dictionary<string, object> parameters = root["params"] as Dictionary<string, object>;

            if (parameters == null)
            {
                return;
            }

            string tool = parameters.ContainsKey("tool") ? parameters["tool"] as string : string.Empty;
            string callId = parameters.ContainsKey("callId") ? parameters["callId"] as string : "-";
            object arguments = parameters.ContainsKey("arguments") ? parameters["arguments"] : null;
            EnqueueMainThread(() => HandleToolCallOnMainThread(serverRequestId, tool, callId, arguments));
        }

        private void HandleNotification(Dictionary<string, object> root, string method)
        {
            Dictionary<string, object> parameters = root.ContainsKey("params")
                ? root["params"] as Dictionary<string, object>
                : null;

            switch (method)
            {
                case "turn/started":
                    EnqueueMainThread(() =>
                    {
                        turnInFlight = true;
                        turnId = ReadString(parameters, "turn", "id") ?? turnId;
                        SetStatus("thinking", $"Codex is reviewing snapshot #{lastSnapshotSequence}");
                        Debug.Log($"[AquariumDirectorBridge] turn started: {turnId}");
                    });
                    break;
                case "turn/completed":
                    EnqueueMainThread(() =>
                    {
                        turnId = ReadString(parameters, "turn", "id") ?? turnId;
                        turnInFlight = false;
                        SetStatus("ready", string.IsNullOrWhiteSpace(lastCodexText) ? "Turn completed" : lastCodexText);
                        Debug.Log($"[AquariumDirectorBridge] turn completed: {turnId}");

                        if (queuedSnapshot != null)
                        {
                            AquariumDirectorSnapshot pendingSnapshot = queuedSnapshot;
                            queuedSnapshot = null;
                            _ = StartTurnFromSnapshotAsync(pendingSnapshot, lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None);
                        }
                    });
                    break;
                case "item/agentMessage/delta":
                    EnqueueMainThread(() =>
                    {
                        string delta = ReadString(parameters, "delta");

                        if (!string.IsNullOrWhiteSpace(delta))
                        {
                            SetStatus("responding", delta);
                        }
                    });
                    break;
                case "error":
                    EnqueueMainThread(() =>
                    {
                        SetStatus("error", ReadString(parameters, "message") ?? "Codex app-server error");
                    });
                    break;
            }
        }

        private void HandleToolCallOnMainThread(int serverRequestId, string tool, string callId, object arguments)
        {
            lastTool = string.IsNullOrWhiteSpace(tool) ? "-" : tool;
            lastActionId = string.IsNullOrWhiteSpace(callId) ? "-" : callId;
            Debug.Log($"[AquariumDirectorBridge] tool call received: {tool} ({callId})");

            switch (tool)
            {
                case "get_world_state":
                {
                    AquariumDirectorSnapshot snapshot = director.CreateSnapshot();
                    string snapshotJson = JsonUtility.ToJson(snapshot, true);
                    SetStatus("acting", "Codex requested the latest world state");
                    _ = SendToolResponseAsync(serverRequestId, true, snapshotJson, lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None);
                    break;
                }
                case "command_lobsters":
                case "command_sharks":
                {
                    AquariumDirectorAction action = ParseAction(arguments);
                    action.actionId = callId;
                    action.species = tool == "command_lobsters" ? "lobsters" : "sharks";

                    AquariumDirectorActionResult result = director.ApplyDirectorAction(action);
                    lastActionSummary = result.message ?? "Action applied";
                    SetStatus(result.success ? "acting" : "warning", lastActionSummary);
                    _ = SendToolResponseAsync(serverRequestId, result.success, result.message ?? "No result message", lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None);
                    break;
                }
                default:
                    SetStatus("warning", $"Unknown dynamic tool: {tool}");
                    _ = SendToolResponseAsync(serverRequestId, false, $"Unknown dynamic tool: {tool}", lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None);
                    break;
            }
        }

        private AquariumDirectorAction ParseAction(object arguments)
        {
            Dictionary<string, object> data = arguments as Dictionary<string, object>;
            AquariumDirectorAction action = new AquariumDirectorAction
            {
                scope = ReadString(data, "scope") ?? "all",
                directive = ReadString(data, "directive") ?? "Autonomous",
                count = ReadInt(data, "count"),
                radius = ReadFloat(data, "radius", 4f),
                durationSeconds = ReadFloat(data, "durationSeconds", 8f),
                creatureIds = ReadStringArray(data, "creatureIds"),
                target = ReadVector3(data, "target")
            };

            return action;
        }

        private async Task StartTurnFromSnapshotAsync(AquariumDirectorSnapshot snapshot, CancellationToken token)
        {
            if (!threadReady || director == null || snapshot == null)
            {
                return;
            }

            turnInFlight = true;
            SetStatus("thinking", $"Sending world snapshot #{snapshot.sequence} to Codex");

            try
            {
                Dictionary<string, object> response = await SendRequestAsync(
                    "turn/start",
                    new Dictionary<string, object>
                    {
                        ["threadId"] = threadId,
                        ["input"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "text",
                                ["text"] = BuildTurnText(snapshot),
                                ["text_elements"] = new List<object>()
                            }
                        }
                    },
                    token);

                string createdTurnId = ReadString(response, "result", "turn", "id");
                EnqueueMainThread(() =>
                {
                    turnId = string.IsNullOrWhiteSpace(createdTurnId) ? turnId : createdTurnId;
                    lastSnapshotSequence = snapshot.sequence;
                    lastSnapshotSentAtUtc = snapshot.capturedAtUtc ?? lastSnapshotSentAtUtc;
                    queuedSnapshot = null;
                    Debug.Log($"[AquariumDirectorBridge] turn/start sent for snapshot #{snapshot.sequence}, turn id: {turnId}");
                });
            }
            catch (Exception ex)
            {
                EnqueueMainThread(() =>
                {
                    turnInFlight = false;
                    SetStatus("error", $"turn/start failed: {ex.Message}");
                    Debug.LogError($"[AquariumDirectorBridge] turn/start failed for snapshot #{snapshot.sequence}: {ex}");
                });
            }
        }

        private async Task<Dictionary<string, object>> SendRequestAsync(string method, Dictionary<string, object> parameters, CancellationToken token)
        {
            int currentRequestId = Interlocked.Increment(ref requestId);
            TaskCompletionSource<Dictionary<string, object>> responseSource =
                new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingResponses[currentRequestId] = responseSource;

            await SendJsonAsync(
                MiniJson.Serialize(
                    new Dictionary<string, object>
                    {
                        ["id"] = currentRequestId,
                        ["method"] = method,
                        ["params"] = parameters
                    }),
                token);

            using CancellationTokenRegistration registration = token.Register(() =>
            {
                if (pendingResponses.TryRemove(currentRequestId, out TaskCompletionSource<Dictionary<string, object>> cancelledSource))
                {
                    cancelledSource.TrySetCanceled(token);
                }
            });

            return await responseSource.Task;
        }

        private Task SendNotificationAsync(string method, Dictionary<string, object> parameters, CancellationToken token)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["method"] = method
            };

            if (parameters != null)
            {
                payload["params"] = parameters;
            }

            return SendJsonAsync(MiniJson.Serialize(payload), token);
        }

        private Task SendToolResponseAsync(int serverRequestId, bool success, string text, CancellationToken token)
        {
            string safeText = string.IsNullOrWhiteSpace(text) ? "No result text." : text;

            return SendJsonAsync(
                MiniJson.Serialize(
                    new Dictionary<string, object>
                    {
                        ["id"] = serverRequestId,
                        ["result"] = new Dictionary<string, object>
                        {
                            ["contentItems"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["type"] = "inputText",
                                    ["text"] = safeText
                                }
                            },
                            ["success"] = success
                        }
                    }),
                token);
        }

        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            ClientWebSocket localSocket = socket;

            if (localSocket == null || localSocket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] payload = Encoding.UTF8.GetBytes(json);
            await sendGate.WaitAsync(token);

            try
            {
                if (localSocket.State == WebSocketState.Open)
                {
                    await localSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AquariumDirectorBridge] SendJsonAsync failed: {ex}");
                throw;
            }
            finally
            {
                sendGate.Release();
            }
        }

        private void DrainMainThreadActions()
        {
            while (mainThreadActions.TryDequeue(out Action action))
            {
                action?.Invoke();
            }
        }

        private void EnqueueMainThread(Action action)
        {
            if (action != null)
            {
                mainThreadActions.Enqueue(action);
            }
        }

        private void EnqueueStatus(string phase, string text)
        {
            EnqueueMainThread(() => SetStatus(phase, text));
        }

        private void SetStatus(string phase, string text)
        {
            bool changed = lastCodexPhase != phase || lastCodexText != text;
            lastCodexPhase = string.IsNullOrWhiteSpace(phase) ? lastCodexPhase : phase;
            lastCodexText = string.IsNullOrWhiteSpace(text) ? lastCodexText : text;

            if (director != null)
            {
                director.UpdateBridgeState(lastCodexPhase, lastCodexText);
            }

            if (changed)
            {
                Debug.Log($"[AquariumDirectorBridge] Status -> {lastCodexPhase}: {lastCodexText}");
            }
        }

        private string BuildTurnText(AquariumDirectorSnapshot snapshot)
        {
            return
                $"World snapshot #{snapshot.sequence}\n" +
                $"{snapshot.summary}\n\n" +
                "You are live inside the aquarium. Inspect the state, use dynamic tools if action is needed, then briefly explain what you are doing.\n" +
                "Latest state:\n" +
                JsonUtility.ToJson(snapshot, true);
        }

        private List<object> BuildDynamicTools()
        {
            Dictionary<string, object> toolSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["directive"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new List<object>
                        {
                            "Autonomous",
                            "MoveToPoint",
                            "GuardZone",
                            "PressurePlayer",
                            "RetreatFromPlayer",
                            "HoldPosition"
                        }
                    },
                    ["scope"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new List<object> { "all", "nearest_to_player", "nearest_to_point", "ids" }
                    },
                    ["count"] = new Dictionary<string, object>
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0
                    },
                    ["creatureIds"] = new Dictionary<string, object>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "string"
                        }
                    },
                    ["target"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["x"] = new Dictionary<string, object> { ["type"] = "number" },
                            ["y"] = new Dictionary<string, object> { ["type"] = "number" },
                            ["z"] = new Dictionary<string, object> { ["type"] = "number" }
                        },
                        ["required"] = new List<object> { "x", "y", "z" },
                        ["additionalProperties"] = false
                    },
                    ["radius"] = new Dictionary<string, object>
                    {
                        ["type"] = "number",
                        ["minimum"] = 0
                    },
                    ["durationSeconds"] = new Dictionary<string, object>
                    {
                        ["type"] = "number",
                        ["minimum"] = 0
                    }
                },
                ["required"] = new List<object> { "directive" },
                ["additionalProperties"] = false
            };

            return new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "command_lobsters",
                    ["description"] = "Direct one or more lobsters with a high-level directive.",
                    ["inputSchema"] = toolSchema
                },
                new Dictionary<string, object>
                {
                    ["name"] = "command_sharks",
                    ["description"] = "Direct one or more sharks with a high-level directive.",
                    ["inputSchema"] = toolSchema
                },
                new Dictionary<string, object>
                {
                    ["name"] = "get_world_state",
                    ["description"] = "Return the latest aquarium snapshot and summary without changing the world.",
                    ["inputSchema"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["additionalProperties"] = false
                    }
                }
            };
        }

        private string BuildDirectorInstructions()
        {
            return
                "You are the Aquarium Director for an underwater sandbox. " +
                "Unity owns movement, animation, spawning, and simulation. " +
                "You control behavior only through the dynamic tools on this thread. " +
                "Keep decisions tactical and continuous. " +
                "Prefer high-level directives like GuardZone, MoveToPoint, PressurePlayer, RetreatFromPlayer, HoldPosition, or Autonomous. " +
                "When you act, explain your intent briefly.";
        }

        private static string ReadString(Dictionary<string, object> root, params string[] path)
        {
            object value = Traverse(root, path);
            return value as string;
        }

        private static int ReadInt(Dictionary<string, object> root, string key)
        {
            object value = Traverse(root, key);

            if (value == null)
            {
                return 0;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static float ReadFloat(Dictionary<string, object> root, string key, float defaultValue)
        {
            object value = Traverse(root, key);

            if (value == null)
            {
                return defaultValue;
            }

            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static string[] ReadStringArray(Dictionary<string, object> root, string key)
        {
            if (!(Traverse(root, key) is List<object> list))
            {
                return Array.Empty<string>();
            }

            string[] values = new string[list.Count];

            for (int i = 0; i < list.Count; i++)
            {
                values[i] = list[i] as string ?? string.Empty;
            }

            return values;
        }

        private static SerializableVector3 ReadVector3(Dictionary<string, object> root, string key)
        {
            if (!(Traverse(root, key) is Dictionary<string, object> vector))
            {
                return null;
            }

            return new SerializableVector3
            {
                x = Convert.ToSingle(Traverse(vector, "x") ?? 0d, CultureInfo.InvariantCulture),
                y = Convert.ToSingle(Traverse(vector, "y") ?? 0d, CultureInfo.InvariantCulture),
                z = Convert.ToSingle(Traverse(vector, "z") ?? 0d, CultureInfo.InvariantCulture)
            };
        }

        private static object Traverse(Dictionary<string, object> root, params string[] path)
        {
            object current = root;

            for (int i = 0; i < path.Length; i++)
            {
                if (!(current is Dictionary<string, object> dictionary) || !dictionary.TryGetValue(path[i], out object next))
                {
                    return null;
                }

                current = next;
            }

            return current;
        }

        private static class MiniJson
        {
            public static object Deserialize(string json)
            {
                if (json == null)
                {
                    return null;
                }

                return Parser.Parse(json);
            }

            public static string Serialize(object obj)
            {
                return Serializer.Serialize(obj);
            }

            private sealed class Parser : IDisposable
            {
                private const string WordBreak = "{}[],:\"";
                private readonly StringReader json;

                private Parser(string jsonString)
                {
                    json = new StringReader(jsonString);
                }

                public static object Parse(string jsonString)
                {
                    using Parser instance = new Parser(jsonString);
                    return instance.ParseValue();
                }

                public void Dispose()
                {
                    json.Dispose();
                }

                private enum Token
                {
                    None,
                    CurlyOpen,
                    CurlyClose,
                    SquaredOpen,
                    SquaredClose,
                    Colon,
                    Comma,
                    String,
                    Number,
                    True,
                    False,
                    Null
                }

                private Dictionary<string, object> ParseObject()
                {
                    Dictionary<string, object> table = new Dictionary<string, object>();
                    json.Read();

                    while (true)
                    {
                        switch (NextToken)
                        {
                            case Token.None:
                                return null;
                            case Token.Comma:
                                continue;
                            case Token.CurlyClose:
                                return table;
                            default:
                                string name = ParseString();

                                if (name == null || NextToken != Token.Colon)
                                {
                                    return null;
                                }

                                json.Read();
                                table[name] = ParseValue();
                                break;
                        }
                    }
                }

                private List<object> ParseArray()
                {
                    List<object> array = new List<object>();
                    json.Read();
                    bool parsing = true;

                    while (parsing)
                    {
                        Token nextToken = NextToken;

                        switch (nextToken)
                        {
                            case Token.None:
                                return null;
                            case Token.Comma:
                                continue;
                            case Token.SquaredClose:
                                parsing = false;
                                break;
                            default:
                                array.Add(ParseByToken(nextToken));
                                break;
                        }
                    }

                    return array;
                }

                private object ParseValue()
                {
                    Token nextToken = NextToken;
                    return ParseByToken(nextToken);
                }

                private object ParseByToken(Token token)
                {
                    switch (token)
                    {
                        case Token.String:
                            return ParseString();
                        case Token.Number:
                            return ParseNumber();
                        case Token.CurlyOpen:
                            return ParseObject();
                        case Token.SquaredOpen:
                            return ParseArray();
                        case Token.True:
                            return true;
                        case Token.False:
                            return false;
                        case Token.Null:
                            return null;
                        default:
                            return null;
                    }
                }

                private string ParseString()
                {
                    StringBuilder builder = new StringBuilder();
                    char c;
                    json.Read();
                    bool parsing = true;

                    while (parsing)
                    {
                        if (json.Peek() == -1)
                        {
                            break;
                        }

                        c = NextChar;

                        switch (c)
                        {
                            case '"':
                                parsing = false;
                                break;
                            case '\\':
                                if (json.Peek() == -1)
                                {
                                    parsing = false;
                                    break;
                                }

                                c = NextChar;

                                switch (c)
                                {
                                    case '"':
                                    case '\\':
                                    case '/':
                                        builder.Append(c);
                                        break;
                                    case 'b':
                                        builder.Append('\b');
                                        break;
                                    case 'f':
                                        builder.Append('\f');
                                        break;
                                    case 'n':
                                        builder.Append('\n');
                                        break;
                                    case 'r':
                                        builder.Append('\r');
                                        break;
                                    case 't':
                                        builder.Append('\t');
                                        break;
                                    case 'u':
                                        char[] hex = new char[4];

                                        for (int i = 0; i < 4; i++)
                                        {
                                            hex[i] = NextChar;
                                        }

                                        builder.Append((char)Convert.ToInt32(new string(hex), 16));
                                        break;
                                }

                                break;
                            default:
                                builder.Append(c);
                                break;
                        }
                    }

                    return builder.ToString();
                }

                private object ParseNumber()
                {
                    string number = NextWord;

                    if (number.IndexOf('.') == -1 && number.IndexOf('e') == -1 && number.IndexOf('E') == -1)
                    {
                        if (long.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out long parsedInt))
                        {
                            return parsedInt;
                        }
                    }

                    if (double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedDouble))
                    {
                        return parsedDouble;
                    }

                    return 0d;
                }

                private void EatWhitespace()
                {
                    while (char.IsWhiteSpace(PeekChar))
                    {
                        json.Read();

                        if (json.Peek() == -1)
                        {
                            break;
                        }
                    }
                }

                private char PeekChar => Convert.ToChar(json.Peek());

                private char NextChar => Convert.ToChar(json.Read());

                private string NextWord
                {
                    get
                    {
                        StringBuilder word = new StringBuilder();

                        while (json.Peek() != -1 && !IsWordBreak(PeekChar))
                        {
                            word.Append(NextChar);
                        }

                        return word.ToString();
                    }
                }

                private Token NextToken
                {
                    get
                    {
                        EatWhitespace();

                        if (json.Peek() == -1)
                        {
                            return Token.None;
                        }

                        switch (PeekChar)
                        {
                            case '{':
                                return Token.CurlyOpen;
                            case '}':
                                json.Read();
                                return Token.CurlyClose;
                            case '[':
                                return Token.SquaredOpen;
                            case ']':
                                json.Read();
                                return Token.SquaredClose;
                            case ',':
                                json.Read();
                                return Token.Comma;
                            case '"':
                                return Token.String;
                            case ':':
                                return Token.Colon;
                            case '0':
                            case '1':
                            case '2':
                            case '3':
                            case '4':
                            case '5':
                            case '6':
                            case '7':
                            case '8':
                            case '9':
                            case '-':
                                return Token.Number;
                        }

                        string word = NextWord;

                        switch (word)
                        {
                            case "false":
                                return Token.False;
                            case "true":
                                return Token.True;
                            case "null":
                                return Token.Null;
                            default:
                                return Token.None;
                        }
                    }
                }

                private static bool IsWordBreak(char c)
                {
                    return char.IsWhiteSpace(c) || WordBreak.IndexOf(c) != -1;
                }
            }

            private sealed class Serializer
            {
                private readonly StringBuilder builder = new StringBuilder();

                public static string Serialize(object obj)
                {
                    Serializer instance = new Serializer();
                    instance.SerializeValue(obj);
                    return instance.builder.ToString();
                }

                private void SerializeValue(object value)
                {
                    switch (value)
                    {
                        case null:
                            builder.Append("null");
                            break;
                        case string text:
                            SerializeString(text);
                            break;
                        case bool boolean:
                            builder.Append(boolean ? "true" : "false");
                            break;
                        case IDictionary dictionary:
                            SerializeObject(dictionary);
                            break;
                        case IList list:
                            SerializeArray(list);
                            break;
                        case char character:
                            SerializeString(character.ToString());
                            break;
                        default:
                            SerializeOther(value);
                            break;
                    }
                }

                private void SerializeObject(IDictionary dictionary)
                {
                    bool first = true;
                    builder.Append('{');

                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        SerializeString(entry.Key.ToString());
                        builder.Append(':');
                        SerializeValue(entry.Value);
                        first = false;
                    }

                    builder.Append('}');
                }

                private void SerializeArray(IList array)
                {
                    builder.Append('[');
                    bool first = true;

                    for (int i = 0; i < array.Count; i++)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        SerializeValue(array[i]);
                        first = false;
                    }

                    builder.Append(']');
                }

                private void SerializeString(string str)
                {
                    builder.Append('"');

                    for (int i = 0; i < str.Length; i++)
                    {
                        char c = str[i];

                        switch (c)
                        {
                            case '"':
                                builder.Append("\\\"");
                                break;
                            case '\\':
                                builder.Append("\\\\");
                                break;
                            case '\b':
                                builder.Append("\\b");
                                break;
                            case '\f':
                                builder.Append("\\f");
                                break;
                            case '\n':
                                builder.Append("\\n");
                                break;
                            case '\r':
                                builder.Append("\\r");
                                break;
                            case '\t':
                                builder.Append("\\t");
                                break;
                            default:
                                if (c < ' ' || c > 126)
                                {
                                    builder.Append("\\u");
                                    builder.Append(((int)c).ToString("x4"));
                                }
                                else
                                {
                                    builder.Append(c);
                                }

                                break;
                        }
                    }

                    builder.Append('"');
                }

                private void SerializeOther(object value)
                {
                    switch (value)
                    {
                        case float floatValue:
                            builder.Append(floatValue.ToString("R", CultureInfo.InvariantCulture));
                            break;
                        case int intValue:
                            builder.Append(intValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case uint uintValue:
                            builder.Append(uintValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case long longValue:
                            builder.Append(longValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case sbyte sbyteValue:
                            builder.Append(sbyteValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case byte byteValue:
                            builder.Append(byteValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case short shortValue:
                            builder.Append(shortValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case ushort ushortValue:
                            builder.Append(ushortValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case ulong ulongValue:
                            builder.Append(ulongValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case decimal decimalValue:
                            builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
                            break;
                        case double doubleValue:
                            builder.Append(doubleValue.ToString("R", CultureInfo.InvariantCulture));
                            break;
                        default:
                            SerializeString(value.ToString());
                            break;
                    }
                }
            }
        }
    }
}
