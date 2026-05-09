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
        [SerializeField] private float reconnectDelaySeconds = 3f;
        [SerializeField] private float worldSyncIntervalSeconds = 3f;
        [SerializeField] private bool autoConnect = true;

        private readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pendingResponses =
            new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, PendingWorldThread> pendingWorldThreads = new Dictionary<string, PendingWorldThread>();

        private UnderwaterGameDirector director;
        private CancellationTokenSource lifecycleCts;
        private Task connectionLoopTask;
        private ClientWebSocket socket;
        private int requestId;
        private float nextWorldSyncAt;
        private bool appServerSocketOpen;
        private bool codexConnected;
        private string codexHomePath;
        private string sessionIndexPath;
        private string sessionsRootPath;
        private string archivedSessionsRootPath;
        private string lastCodexPhase = "offline";
        private string lastCodexText = "Thread mirror offline";

        private sealed class PendingWorldThread
        {
            public string id;
            public string title;
            public string createdAtUtc;
            public string source;
        }

        #pragma warning disable 0649
        [Serializable]
        private sealed class SessionIndexEntry
        {
            public string id;
            public string thread_name;
            public string updated_at;
        }
        #pragma warning restore 0649

        private sealed class SessionIndexRecord
        {
            public string id;
            public string title;
            public string updatedAtUtc;

            public static SessionIndexRecord CreateFallback(string id, string title, string updatedAtUtc)
            {
                return new SessionIndexRecord
                {
                    id = id,
                    title = title,
                    updatedAtUtc = updatedAtUtc
                };
            }
        }

        public string BridgeUrl => codexServerUrl;

        public bool IsConnected => codexConnected && appServerSocketOpen;

        public void Initialize(UnderwaterGameDirector owningDirector)
        {
            director = owningDirector;
            codexHomePath = Environment.GetEnvironmentVariable("CODEX_HOME");

            if (string.IsNullOrWhiteSpace(codexHomePath))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                codexHomePath = Path.Combine(homeDirectory, ".codex");
            }

            sessionIndexPath = Path.Combine(codexHomePath, "session_index.jsonl");
            sessionsRootPath = Path.Combine(codexHomePath, "sessions");
            archivedSessionsRootPath = Path.Combine(codexHomePath, "archived_sessions");
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

            if (director == null || Time.unscaledTime < nextWorldSyncAt)
            {
                return;
            }

            nextWorldSyncAt = Time.unscaledTime + Mathf.Max(1f, worldSyncIntervalSeconds);
            RefreshWorldFromCodexState();
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
            appServerSocketOpen = false;
            codexConnected = false;
        }

        public Task<string> CreateWorkThreadAsync(string title, string prompt)
        {
            CancellationToken token = lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None;
            return CreateWorkThreadInternalAsync(title, prompt, token);
        }

        private async Task<string> CreateWorkThreadInternalAsync(string title, string prompt, CancellationToken token)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Codex app-server is not connected.");
            }

            string safeTitle = string.IsNullOrWhiteSpace(title) ? "Underwater work item" : title.Trim();
            SetStatus("acting", $"Creating task '{safeTitle}'");

            Dictionary<string, object> response = await SendRequestAsync(
                "thread/start",
                new Dictionary<string, object>
                {
                    ["serviceName"] = "underwater_work_thread",
                    ["baseInstructions"] =
                        "You are a Codex work thread spawned from the Underwater reef. " +
                        "Stay focused on the task that created this thread. " +
                        "Use the current workspace when relevant.",
                    ["experimentalRawEvents"] = false,
                    ["persistExtendedHistory"] = true
                },
                token);

            string createdThreadId = ReadString(response, "result", "thread", "id") ?? Guid.NewGuid().ToString();
            string initialPrompt = string.IsNullOrWhiteSpace(prompt) ? safeTitle : prompt.Trim();

            if (!string.IsNullOrWhiteSpace(initialPrompt))
            {
                await SendRequestAsync(
                    "turn/start",
                    new Dictionary<string, object>
                    {
                        ["threadId"] = createdThreadId,
                        ["input"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "text",
                                ["text"] = initialPrompt,
                                ["text_elements"] = new List<object>()
                            }
                        }
                    },
                    token);
            }

            EnqueueMainThread(() =>
            {
                pendingWorldThreads[createdThreadId] = new PendingWorldThread
                {
                    id = createdThreadId,
                    title = safeTitle,
                    createdAtUtc = DateTime.UtcNow.ToString("o"),
                    source = "spawned"
                };

                SetStatus("ready", $"Created task '{safeTitle}'");
                RefreshWorldFromCodexState();
            });

            return createdThreadId;
        }

        private async Task RunConnectionLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket localSocket = new ClientWebSocket();

                try
                {
                    EnqueueStatus("connecting", $"Connecting to Codex app-server at {codexServerUrl}");
                    await localSocket.ConnectAsync(new Uri(codexServerUrl), token);
                    socket = localSocket;
                    EnqueueMainThread(() =>
                    {
                        appServerSocketOpen = true;
                        SetStatus("connecting", "Codex app-server socket open");
                    });

                    Task receiveTask = ReceiveLoopAsync(localSocket, token);
                    await InitializeAppServerAsync(token);
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
                        appServerSocketOpen = false;
                        codexConnected = false;
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

            EnqueueStatus("offline", "Thread mirror offline");
        }

        private async Task InitializeAppServerAsync(CancellationToken token)
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
            await SendNotificationAsync("initialized", null, token);

            EnqueueMainThread(() =>
            {
                codexConnected = true;
                SetStatus("connected", $"Codex app-server ready on {platformFamily ?? "unknown-platform"}");
            });
        }

        private void RefreshWorldFromCodexState()
        {
            if (director == null)
            {
                return;
            }

            try
            {
                Dictionary<string, SessionIndexRecord> indexedThreads = ReadSessionIndex();
                HashSet<string> liveSessionIds = ReadSessionIdsFromDirectory(sessionsRootPath);
                HashSet<string> archivedSessionIds = ReadSessionIdsFromDirectory(archivedSessionsRootPath);
                List<AquariumThreadSnapshot> liveThreads = new List<AquariumThreadSnapshot>();
                List<AquariumArchivedRollSnapshot> rolls = new List<AquariumArchivedRollSnapshot>();
                List<string> resolvedPendingIds = new List<string>();

                foreach (KeyValuePair<string, PendingWorldThread> pair in pendingWorldThreads)
                {
                    if (liveSessionIds.Contains(pair.Key) || archivedSessionIds.Contains(pair.Key))
                    {
                        resolvedPendingIds.Add(pair.Key);
                    }
                }

                for (int i = 0; i < resolvedPendingIds.Count; i++)
                {
                    pendingWorldThreads.Remove(resolvedPendingIds[i]);
                }

                foreach (string id in liveSessionIds)
                {
                    if (archivedSessionIds.Contains(id))
                    {
                        continue;
                    }

                    SessionIndexRecord record = indexedThreads.TryGetValue(id, out SessionIndexRecord indexed)
                        ? indexed
                        : SessionIndexRecord.CreateFallback(id, "Active thread", DateTime.UtcNow.ToString("o"));
                    liveThreads.Add(CreateThreadSnapshot(record, "filesystem"));
                }

                foreach (KeyValuePair<string, PendingWorldThread> pair in pendingWorldThreads)
                {
                    if (!liveSessionIds.Contains(pair.Key))
                    {
                        DateTime createdAt = DateTime.UtcNow;

                        if (!string.IsNullOrWhiteSpace(pair.Value.createdAtUtc))
                        {
                            DateTime.TryParse(pair.Value.createdAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out createdAt);
                        }

                        liveThreads.Add(new AquariumThreadSnapshot
                        {
                            id = pair.Value.id,
                            title = pair.Value.title,
                            phase = "fresh",
                            source = pair.Value.source,
                            ageMinutes = Mathf.Max(0f, (float)(DateTime.UtcNow - createdAt).TotalMinutes)
                        });
                    }
                }

                foreach (string id in archivedSessionIds)
                {
                    if (indexedThreads.TryGetValue(id, out SessionIndexRecord record))
                    {
                        rolls.Add(new AquariumArchivedRollSnapshot
                        {
                            id = record.id,
                            title = record.title
                        });
                    }
                    else
                    {
                        rolls.Add(new AquariumArchivedRollSnapshot
                        {
                            id = id,
                            title = "Archived thread"
                        });
                    }
                }

                liveThreads.Sort((left, right) =>
                {
                    int ageComparison = left.ageMinutes.CompareTo(right.ageMinutes);

                    if (ageComparison != 0)
                    {
                        return ageComparison;
                    }

                    return string.CompareOrdinal(left.title, right.title);
                });
                rolls.Sort((left, right) => string.CompareOrdinal(left.title, right.title));

                string connectionLabel = IsConnected ? "Websocket connected." : "Websocket offline.";
                string detail = $"{connectionLabel} Mirroring {liveThreads.Count} active threads and {rolls.Count} archived rolls.";
                director.SyncThreadWorld(liveThreads, rolls, detail);
                director.UpdateBridgeState(codexConnected ? "ready" : "offline", detail);
            }
            catch (Exception ex)
            {
                director.UpdateBridgeState("warning", $"Thread mirror failed: {ex.Message}");
                Debug.LogError($"[AquariumDirectorBridge] Thread mirror failed: {ex}");
            }
        }

        private AquariumThreadSnapshot CreateThreadSnapshot(SessionIndexRecord record, string source)
        {
            DateTime updatedAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(record.updatedAtUtc))
            {
                DateTime.TryParse(record.updatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out updatedAt);
            }

            float ageMinutes = Mathf.Max(0f, (float)(DateTime.UtcNow - updatedAt).TotalMinutes);

            return new AquariumThreadSnapshot
            {
                id = record.id,
                title = string.IsNullOrWhiteSpace(record.title) ? "Untitled thread" : record.title,
                phase = DetermineThreadPhase(ageMinutes),
                source = source,
                ageMinutes = ageMinutes
            };
        }

        private static string DetermineThreadPhase(float ageMinutes)
        {
            if (ageMinutes <= 2f)
            {
                return "fresh";
            }

            if (ageMinutes <= 12f)
            {
                return "responding";
            }

            if (ageMinutes <= 45f)
            {
                return "working";
            }

            return "idle";
        }

        private Dictionary<string, SessionIndexRecord> ReadSessionIndex()
        {
            Dictionary<string, SessionIndexRecord> records = new Dictionary<string, SessionIndexRecord>();

            if (string.IsNullOrWhiteSpace(sessionIndexPath) || !File.Exists(sessionIndexPath))
            {
                return records;
            }

            string[] lines = File.ReadAllLines(sessionIndexPath);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                SessionIndexEntry entry;

                try
                {
                    entry = JsonUtility.FromJson<SessionIndexEntry>(line);
                }
                catch
                {
                    continue;
                }

                if (entry == null || string.IsNullOrWhiteSpace(entry.id))
                {
                    continue;
                }

                records[entry.id] = new SessionIndexRecord
                {
                    id = entry.id,
                    title = string.IsNullOrWhiteSpace(entry.thread_name) ? "Untitled thread" : entry.thread_name,
                    updatedAtUtc = string.IsNullOrWhiteSpace(entry.updated_at) ? DateTime.UtcNow.ToString("o") : entry.updated_at
                };
            }

            return records;
        }

        private HashSet<string> ReadSessionIdsFromDirectory(string rootPath)
        {
            HashSet<string> ids = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return ids;
            }

            string[] files = Directory.GetFiles(rootPath, "*.jsonl", SearchOption.AllDirectories);

            for (int i = 0; i < files.Length; i++)
            {
                string id = ExtractThreadIdFromPath(files[i]);

                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static string ExtractThreadIdFromPath(string path)
        {
            string filename = Path.GetFileNameWithoutExtension(path);

            if (string.IsNullOrWhiteSpace(filename) || filename.Length < 36)
            {
                return string.Empty;
            }

            string maybeId = filename.Substring(filename.Length - 36);
            Guid parsed;
            return Guid.TryParse(maybeId, out parsed) ? maybeId : string.Empty;
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

                if (!string.IsNullOrWhiteSpace(json))
                {
                    HandleIncomingMessage(json);
                }
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

            if (string.Equals(method, "error", StringComparison.Ordinal))
            {
                Dictionary<string, object> parameters = root.ContainsKey("params")
                    ? root["params"] as Dictionary<string, object>
                    : null;
                EnqueueMainThread(() => SetStatus("warning", ReadString(parameters, "message") ?? "Codex app-server error"));
            }
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

        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            ClientWebSocket localSocket = socket;

            if (localSocket == null || localSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("Codex app-server socket is not open.");
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

            if (changed && ShouldLogStatus(lastCodexPhase))
            {
                Debug.Log($"[AquariumDirectorBridge] Status -> {lastCodexPhase}: {lastCodexText}");
            }
        }

        private static bool ShouldLogStatus(string phase)
        {
            switch ((phase ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "connecting":
                case "connected":
                case "acting":
                case "warning":
                case "error":
                case "reconnecting":
                    return true;
                default:
                    return false;
            }
        }

        private static string ReadString(Dictionary<string, object> root, params string[] path)
        {
            object value = Traverse(root, path);
            return value as string;
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
