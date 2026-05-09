using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Underwater
{
    public sealed class AquariumDirectorBridge : MonoBehaviour
    {
        [SerializeField] private string codexServerUrl = "ws://127.0.0.1:4500";
        [SerializeField] private float reconnectDelaySeconds = 3f;
        [SerializeField] private bool autoConnect = true;

        private readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> pendingResponses =
            new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, PendingWorldThread> pendingWorldThreads = new Dictionary<string, PendingWorldThread>();
        private readonly Dictionary<string, AppServerThreadRecord> mirroredThreads = new Dictionary<string, AppServerThreadRecord>();
        private readonly Dictionary<string, AquariumArchivedPetSnapshot> mirroredArchivedPets = new Dictionary<string, AquariumArchivedPetSnapshot>();
        private readonly HashSet<string> subscribedThreadIds = new HashSet<string>();

        private UnderwaterGameDirector director;
        private CancellationTokenSource lifecycleCts;
        private Task connectionLoopTask;
        private ClientWebSocket socket;
        private int requestId;
        private bool bootstrapInFlight;
        private bool appServerSocketOpen;
        private bool codexConnected;
        private string lastCodexPhase = "offline";
        private string lastCodexText = "Thread mirror offline";

        private sealed class PendingWorldThread
        {
            public string id;
            public string title;
            public string createdAtUtc;
            public string source;
        }

        private sealed class AppServerThreadRecord
        {
            public string id;
            public string title;
            public string statusMessage;
            public string phase;
            public DateTime updatedAtUtc;
            public string source;
        }

        public string BridgeUrl => codexServerUrl;

        public bool IsConnected => codexConnected && appServerSocketOpen;

        public void Initialize(UnderwaterGameDirector owningDirector)
        {
            director = owningDirector;
        }

        public void SetAutoConnect(bool enabled)
        {
            autoConnect = enabled;
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

            pendingWorldThreads.Clear();
            mirroredThreads.Clear();
            mirroredArchivedPets.Clear();
            subscribedThreadIds.Clear();
            bootstrapInFlight = false;

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

            Dictionary<string, object> threadStartParameters = new Dictionary<string, object>
            {
                ["serviceName"] = "underwater_work_thread",
                ["baseInstructions"] =
                    "You are a Codex work thread spawned from the Underwater reef. " +
                    "Stay focused on the task that created this thread. " +
                    "Use the current workspace when relevant.",
                ["threadSource"] = "user",
                ["experimentalRawEvents"] = false,
                ["persistExtendedHistory"] = false
            };
            string projectRootPath = GetUnityProjectRootPath();

            if (!string.IsNullOrWhiteSpace(projectRootPath))
            {
                threadStartParameters["cwd"] = projectRootPath;
            }

            Dictionary<string, object> response = await SendRequestAsync("thread/start", threadStartParameters, token);

            string createdThreadId = ReadString(response, "result", "thread", "id") ?? Guid.NewGuid().ToString();
            string initialPrompt = string.IsNullOrWhiteSpace(prompt) ? safeTitle : prompt.Trim();

            EnqueueMainThread(() =>
            {
                Dictionary<string, object> thread = Traverse(response, "result", "thread") as Dictionary<string, object>;
                AppServerThreadRecord record = ReadAppServerThreadRecord(thread);

                if (record != null)
                {
                    UpsertMirroredThread(record);
                    subscribedThreadIds.Add(record.id);
                    SyncMirroredWorld("Websocket subscribed to spawned thread.");
                }
            });

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
                SyncMirroredWorld("Websocket subscribed to spawned thread.");
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
                BeginBootstrapWorldFromAppServer();
            });
        }

        private void BeginBootstrapWorldFromAppServer()
        {
            if (director == null)
            {
                return;
            }

            if (!IsConnected)
            {
                SyncPendingWorldThreads("Websocket offline.");
                return;
            }

            if (bootstrapInFlight)
            {
                return;
            }

            bootstrapInFlight = true;
            CancellationToken token = lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None;
            _ = BootstrapWorldFromAppServerAsync(token);
        }

        private async Task BootstrapWorldFromAppServerAsync(CancellationToken token)
        {
            try
            {
                Dictionary<string, object> activeResponse = await SendRequestAsync("thread/list", BuildThreadListParameters(false), token);
                Dictionary<string, object> archivedResponse = await SendRequestAsync("thread/list", BuildThreadListParameters(true), token);
                Dictionary<string, object> loadedResponse = await SendRequestAsync("thread/loaded/list", new Dictionary<string, object>(), token);
                List<AppServerThreadRecord> appThreads = ReadAppServerThreads(activeResponse);
                List<AppServerThreadRecord> archivedThreads = ReadAppServerThreads(archivedResponse);
                List<string> loadedThreadIds = ReadStringList(loadedResponse, "result", "data");

                EnqueueMainThread(() =>
                {
                    bootstrapInFlight = false;
                    SyncAppServerWorldThreads(appThreads, archivedThreads, "Websocket connected.");
                    SubscribeToLoadedThreads(loadedThreadIds);
                });
            }
            catch (OperationCanceledException)
            {
                EnqueueMainThread(() => bootstrapInFlight = false);
            }
            catch (Exception ex)
            {
                EnqueueMainThread(() =>
                {
                    bootstrapInFlight = false;

                    if (director != null)
                    {
                        director.UpdateBridgeState("warning", $"Thread mirror failed: {ex.Message}");
                    }

                    Debug.LogError($"[AquariumDirectorBridge] Thread mirror failed: {ex}");
                });
            }
        }

        private static Dictionary<string, object> BuildThreadListParameters(bool archived)
        {
            return new Dictionary<string, object>
            {
                ["limit"] = 50,
                ["sortKey"] = "updated_at",
                ["archived"] = archived,
                ["sourceKinds"] = new List<object>
                {
                    "cli",
                    "vscode",
                    "appServer"
                }
            };
        }

        private void SyncAppServerWorldThreads(List<AppServerThreadRecord> appThreads, List<AppServerThreadRecord> archivedThreads, string connectionLabel)
        {
            if (director == null)
            {
                return;
            }

            HashSet<string> appThreadIds = new HashSet<string>();
            List<string> resolvedPendingIds = new List<string>();

            mirroredThreads.Clear();
            mirroredArchivedPets.Clear();

            for (int i = 0; i < appThreads.Count; i++)
            {
                AppServerThreadRecord record = appThreads[i];

                if (string.IsNullOrWhiteSpace(record.id) || string.IsNullOrWhiteSpace(record.title))
                {
                    continue;
                }

                appThreadIds.Add(record.id);
                mirroredThreads[record.id] = record;
            }

            for (int i = 0; i < archivedThreads.Count; i++)
            {
                AppServerThreadRecord record = archivedThreads[i];

                if (string.IsNullOrWhiteSpace(record.id) || string.IsNullOrWhiteSpace(record.title))
                {
                    continue;
                }

                mirroredArchivedPets[record.id] = CreateArchivedPetSnapshot(record);
            }

            foreach (KeyValuePair<string, PendingWorldThread> pair in pendingWorldThreads)
            {
                if (appThreadIds.Contains(pair.Key))
                {
                    resolvedPendingIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < resolvedPendingIds.Count; i++)
            {
                pendingWorldThreads.Remove(resolvedPendingIds[i]);
            }

            SyncMirroredWorld(connectionLabel);
        }

        private void SubscribeToLoadedThreads(List<string> threadIds)
        {
            if (threadIds == null)
            {
                return;
            }

            for (int i = 0; i < threadIds.Count; i++)
            {
                SubscribeToThread(threadIds[i]);
            }
        }

        private void SubscribeToThread(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId) || subscribedThreadIds.Contains(threadId))
            {
                return;
            }

            subscribedThreadIds.Add(threadId);
            CancellationToken token = lifecycleCts != null ? lifecycleCts.Token : CancellationToken.None;
            _ = SubscribeToThreadAsync(threadId.Trim(), token);
        }

        private async Task SubscribeToThreadAsync(string threadId, CancellationToken token)
        {
            try
            {
                Dictionary<string, object> response = await SendRequestAsync(
                    "thread/resume",
                    new Dictionary<string, object>
                    {
                        ["threadId"] = threadId,
                        ["excludeTurns"] = true,
                        ["persistExtendedHistory"] = false
                    },
                    token);

                EnqueueMainThread(() =>
                {
                    Dictionary<string, object> thread = Traverse(response, "result", "thread") as Dictionary<string, object>;
                    AppServerThreadRecord record = ReadAppServerThreadRecord(thread);

                    if (record != null)
                    {
                        UpsertMirroredThread(record);
                        SyncMirroredWorld("Websocket subscribed to loaded threads.");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                EnqueueMainThread(() => subscribedThreadIds.Remove(threadId));
            }
            catch (Exception ex)
            {
                EnqueueMainThread(() =>
                {
                    subscribedThreadIds.Remove(threadId);
                    Debug.LogWarning($"[AquariumDirectorBridge] Could not subscribe to Codex thread '{threadId}': {ex.Message}");
                });
            }
        }

        private void UpsertMirroredThread(AppServerThreadRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.id))
            {
                return;
            }

            mirroredThreads[record.id] = record;
            mirroredArchivedPets.Remove(record.id);
            pendingWorldThreads.Remove(record.id);
        }

        private void SyncPendingWorldThreads(string connectionLabel)
        {
            List<AquariumThreadSnapshot> liveThreads = new List<AquariumThreadSnapshot>();
            List<AquariumArchivedPetSnapshot> archivedPets = new List<AquariumArchivedPetSnapshot>();

            AddPendingThreadSnapshots(liveThreads, new HashSet<string>());
            SortAndSyncWorld(liveThreads, archivedPets, connectionLabel);
        }

        private void AddPendingThreadSnapshots(List<AquariumThreadSnapshot> liveThreads, HashSet<string> knownThreadIds)
        {
            foreach (KeyValuePair<string, PendingWorldThread> pair in pendingWorldThreads)
            {
                if (knownThreadIds.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value.title))
                {
                    continue;
                }

                DateTime createdAt = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(pair.Value.createdAtUtc))
                {
                    DateTime.TryParse(pair.Value.createdAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out createdAt);
                }

                liveThreads.Add(new AquariumThreadSnapshot
                {
                    id = pair.Value.id,
                    title = pair.Value.title,
                    statusMessage = "Starting chat",
                    phase = "fresh",
                    source = pair.Value.source,
                    ageMinutes = Mathf.Max(0f, (float)(DateTime.UtcNow - createdAt).TotalMinutes)
                });
            }
        }

        private void SyncMirroredWorld(string connectionLabel)
        {
            if (director == null)
            {
                return;
            }

            List<AquariumThreadSnapshot> liveThreads = new List<AquariumThreadSnapshot>(mirroredThreads.Count + pendingWorldThreads.Count);
            List<AquariumArchivedPetSnapshot> archivedPets = new List<AquariumArchivedPetSnapshot>(mirroredArchivedPets.Count);
            HashSet<string> knownThreadIds = new HashSet<string>();

            foreach (KeyValuePair<string, AppServerThreadRecord> pair in mirroredThreads)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                knownThreadIds.Add(pair.Key);
                liveThreads.Add(CreateThreadSnapshot(pair.Value));
            }

            foreach (KeyValuePair<string, AquariumArchivedPetSnapshot> pair in mirroredArchivedPets)
            {
                if (pair.Value != null)
                {
                    archivedPets.Add(pair.Value);
                }
            }

            AddPendingThreadSnapshots(liveThreads, knownThreadIds);
            SortAndSyncWorld(liveThreads, archivedPets, connectionLabel);
        }

        private void SortAndSyncWorld(List<AquariumThreadSnapshot> liveThreads, List<AquariumArchivedPetSnapshot> archivedPets, string connectionLabel)
        {
            if (director == null)
            {
                return;
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
            archivedPets.Sort((left, right) => string.CompareOrdinal(left.title, right.title));

            string detail = $"{connectionLabel} Mirroring {liveThreads.Count} active threads and {archivedPets.Count} archived pets.";
            director.SyncThreadWorld(liveThreads, archivedPets, detail);
            director.UpdateBridgeState(codexConnected ? "ready" : "offline", detail);
        }

        private AquariumThreadSnapshot CreateThreadSnapshot(AppServerThreadRecord record)
        {
            float ageMinutes = Mathf.Max(0f, (float)(DateTime.UtcNow - record.updatedAtUtc).TotalMinutes);

            return new AquariumThreadSnapshot
            {
                id = record.id,
                title = record.title,
                statusMessage = record.statusMessage,
                phase = string.IsNullOrWhiteSpace(record.phase) ? DetermineThreadPhase(ageMinutes) : record.phase,
                source = string.IsNullOrWhiteSpace(record.source) ? "app-server" : record.source,
                ageMinutes = ageMinutes
            };
        }

        private static AquariumArchivedPetSnapshot CreateArchivedPetSnapshot(AppServerThreadRecord record)
        {
            return new AquariumArchivedPetSnapshot
            {
                id = record.id,
                title = record.title,
                statusMessage = string.IsNullOrWhiteSpace(record.statusMessage) ? "Archived" : record.statusMessage
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

        private static List<AppServerThreadRecord> ReadAppServerThreads(Dictionary<string, object> response)
        {
            List<AppServerThreadRecord> records = new List<AppServerThreadRecord>();
            object data = Traverse(response, "result", "data");

            if (!(data is List<object> threads))
            {
                return records;
            }

            for (int i = 0; i < threads.Count; i++)
            {
                if (!(threads[i] is Dictionary<string, object> thread))
                {
                    continue;
                }

                AppServerThreadRecord record = ReadAppServerThreadRecord(thread);

                if (record == null)
                {
                    continue;
                }

                records.Add(record);
            }

            return records;
        }

        private static AppServerThreadRecord ReadAppServerThreadRecord(Dictionary<string, object> thread)
        {
            if (thread == null)
            {
                return null;
            }

            string id = ReadString(thread, "id") ?? ReadString(thread, "sessionId");

            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            DateTime updatedAtUtc = ReadUnixTimestampSeconds(thread, "updatedAt")
                ?? ReadUnixTimestampSeconds(thread, "updated_at")
                ?? ReadUnixTimestampSeconds(thread, "createdAt")
                ?? ReadUnixTimestampSeconds(thread, "created_at")
                ?? DateTime.UtcNow;
            string statusId = ReadThreadStatusId(thread);
            float ageMinutes = Mathf.Max(0f, (float)(DateTime.UtcNow - updatedAtUtc).TotalMinutes);

            return new AppServerThreadRecord
            {
                id = id.Trim(),
                title = ReadThreadTitle(thread).Trim(),
                statusMessage = BuildThreadStatusMessage(thread, statusId),
                phase = DetermineThreadPhase(statusId, ageMinutes),
                updatedAtUtc = updatedAtUtc,
                source = ReadSourceLabel(thread)
            };
        }

        private static string ReadThreadTitle(Dictionary<string, object> thread)
        {
            string title = ReadString(thread, "name");

            if (string.IsNullOrWhiteSpace(title))
            {
                title = ReadString(thread, "title");
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = ReadString(thread, "preview");
            }

            string cleaned = CleanBubbleText(title, 80);
            return string.IsNullOrWhiteSpace(cleaned) ? "New chat" : cleaned;
        }

        private static string ReadSourceLabel(Dictionary<string, object> thread)
        {
            string source = ReadString(thread, "source");

            if (!string.IsNullOrWhiteSpace(source))
            {
                return source.Trim();
            }

            object sourceValue = Traverse(thread, "source");

            if (sourceValue is Dictionary<string, object> sourceObject)
            {
                string custom = ReadString(sourceObject, "custom");

                if (!string.IsNullOrWhiteSpace(custom))
                {
                    return custom.Trim();
                }

                string subAgent = ReadString(sourceObject, "subAgent");

                if (!string.IsNullOrWhiteSpace(subAgent))
                {
                    return "subAgent";
                }
            }

            return "app-server";
        }

        private static string BuildThreadStatusMessage(Dictionary<string, object> thread, string statusId)
        {
            string activityMessage = ReadLatestActivityMessage(thread);

            if (!string.IsNullOrWhiteSpace(activityMessage))
            {
                return activityMessage;
            }

            switch ((statusId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "waiting":
                    return "Needs input";
                case "failed":
                    return "Blocked";
                case "review":
                    return "Ready";
                case "running":
                    return "Thinking";
                case "idle":
                    return "Idle";
                default:
                    return "Info";
            }
        }

        private static string ReadTurnCompletionMessage(Dictionary<string, object> turn)
        {
            string status = ReadString(turn, "status");

            switch ((status ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "failed":
                    string errorMessage = ReadString(turn, "error", "message");
                    return string.IsNullOrWhiteSpace(errorMessage) ? "Blocked" : CleanBubbleText(errorMessage, 72);
                case "interrupted":
                    return "Interrupted";
                default:
                    return "Idle";
            }
        }

        private static string BuildItemStatusMessage(Dictionary<string, object> item, bool started)
        {
            if (item == null)
            {
                return null;
            }

            string type = ReadString(item, "type");

            switch (type)
            {
                case "reasoning":
                    string reasoning = ReadLatestReasoningOrAgentMessage(new List<object> { item });
                    return string.IsNullOrWhiteSpace(reasoning) ? "Thinking" : reasoning;
                case "agentMessage":
                    string message = CleanBubbleText(ReadString(item, "text"), 72);
                    return string.IsNullOrWhiteSpace(message) ? started ? "Writing" : "Responded" : message;
                case "plan":
                    string plan = CleanBubbleText(ReadString(item, "text"), 72);
                    return string.IsNullOrWhiteSpace(plan) ? "Planning" : plan;
                case "commandExecution":
                    return BuildCommandActivityMessage(item, started || string.Equals(ReadString(item, "status"), "inProgress", StringComparison.Ordinal));
                case "fileChange":
                    return BuildFileChangeActivityMessage(item, started || string.Equals(ReadString(item, "status"), "inProgress", StringComparison.Ordinal));
                case "mcpToolCall":
                    return BuildMcpToolActivityMessage(item, started || string.Equals(ReadString(item, "status"), "inProgress", StringComparison.Ordinal));
                case "webSearch":
                    return BuildWebSearchActivityMessage(item);
                case "contextCompaction":
                    return "Compacting context";
                case "userMessage":
                    return started ? "Reading prompt" : "Prompt received";
                default:
                    return null;
            }
        }

        private static string DetermineItemPhase(Dictionary<string, object> item, bool started)
        {
            string type = ReadString(item, "type");

            switch (type)
            {
                case "agentMessage":
                    return "responding";
                case "reasoning":
                case "plan":
                    return started ? "working" : "responding";
                case "commandExecution":
                case "fileChange":
                case "mcpToolCall":
                case "webSearch":
                case "contextCompaction":
                    return started ? "working" : "responding";
                default:
                    return started ? "working" : "idle";
            }
        }

        private static string DetermineThreadPhase(string statusId, float ageMinutes)
        {
            switch ((statusId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "failed":
                    return "failed";
                case "waiting":
                    return "warning";
                case "review":
                    return "responding";
                case "running":
                    return ageMinutes <= 2f ? "fresh" : "working";
                default:
                    return DetermineThreadPhase(ageMinutes);
            }
        }

        private static string ReadThreadStatusId(Dictionary<string, object> thread)
        {
            string cloudTurnStatus = ReadString(thread, "task_status_display", "latest_turn_status_display", "turn_status");

            if (!string.IsNullOrWhiteSpace(cloudTurnStatus))
            {
                switch (cloudTurnStatus.Trim().ToLowerInvariant())
                {
                    case "failed":
                    case "cancelled":
                        return "failed";
                    case "in_progress":
                    case "pending":
                        return "running";
                }
            }

            if (HasRequestUserInput(thread))
            {
                return "waiting";
            }

            Dictionary<string, object> status = Traverse(thread, "status") as Dictionary<string, object>;
            string type = ReadString(status, "type");

            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "active":
                    return HasActiveFlag(status, "waitingOnUserInput") || HasActiveFlag(status, "waitingOnApproval")
                        ? "waiting"
                        : "running";
                case "systemerror":
                    return "failed";
            }

            List<object> turns = Traverse(thread, "turns") as List<object>;

            if (turns != null && turns.Count > 0)
            {
                Dictionary<string, object> lastTurn = turns[turns.Count - 1] as Dictionary<string, object>;
                string turnStatus = ReadString(lastTurn, "status");

                switch ((turnStatus ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "inprogress":
                    case "in_progress":
                        return "running";
                    case "failed":
                        return "failed";
                }
            }

            if (ReadBool(thread, "has_unread_turn") || ReadBool(thread, "hasUnreadTurn"))
            {
                return "review";
            }

            return "idle";
        }

        private static bool HasActiveFlag(Dictionary<string, object> status, string flag)
        {
            List<object> flags = Traverse(status, "activeFlags") as List<object>;

            if (flags == null)
            {
                flags = Traverse(status, "active_flags") as List<object>;
            }

            if (flags == null)
            {
                return false;
            }

            for (int i = 0; i < flags.Count; i++)
            {
                if (string.Equals(flags[i] as string, flag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRequestUserInput(Dictionary<string, object> thread)
        {
            List<object> requests = Traverse(thread, "requests") as List<object>;

            if (requests == null)
            {
                return false;
            }

            for (int i = 0; i < requests.Count; i++)
            {
                Dictionary<string, object> request = requests[i] as Dictionary<string, object>;

                if (string.Equals(ReadString(request, "method"), "item/tool/requestUserInput", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReadLatestActivityMessage(Dictionary<string, object> thread)
        {
            List<object> turns = Traverse(thread, "turns") as List<object>;

            if (turns == null)
            {
                return null;
            }

            for (int turnIndex = turns.Count - 1; turnIndex >= 0; turnIndex--)
            {
                Dictionary<string, object> turn = turns[turnIndex] as Dictionary<string, object>;
                List<object> items = Traverse(turn, "items") as List<object>;
                string message = ReadLatestReasoningOrAgentMessage(items);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }

                message = ReadLatestToolActivityMessage(items);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }

            return null;
        }

        private static string ReadLatestReasoningOrAgentMessage(List<object> items)
        {
            if (items == null)
            {
                return null;
            }

            for (int i = items.Count - 1; i >= 0; i--)
            {
                Dictionary<string, object> item = items[i] as Dictionary<string, object>;
                string type = ReadString(item, "type");

                if (string.Equals(type, "reasoning", StringComparison.Ordinal))
                {
                    List<object> summary = Traverse(item, "summary") as List<object>;

                    if (summary != null)
                    {
                        for (int summaryIndex = summary.Count - 1; summaryIndex >= 0; summaryIndex--)
                        {
                            string cleaned = CleanBubbleText(summary[summaryIndex] as string, 72);

                            if (!string.IsNullOrWhiteSpace(cleaned))
                            {
                                return cleaned;
                            }
                        }
                    }
                }

                if (string.Equals(type, "agentMessage", StringComparison.Ordinal))
                {
                    string cleaned = CleanBubbleText(ReadString(item, "text"), 72);

                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        return cleaned;
                    }
                }
            }

            return null;
        }

        private static string ReadLatestToolActivityMessage(List<object> items)
        {
            if (items == null)
            {
                return null;
            }

            for (int i = items.Count - 1; i >= 0; i--)
            {
                Dictionary<string, object> item = items[i] as Dictionary<string, object>;
                string message = BuildActivityMessage(item);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }

            return null;
        }

        private static string BuildActivityMessage(Dictionary<string, object> item)
        {
            if (item == null)
            {
                return null;
            }

            string type = ReadString(item, "type");
            bool inProgress = string.Equals(ReadString(item, "status"), "inProgress", StringComparison.Ordinal);

            switch (type)
            {
                case "commandExecution":
                    return BuildCommandActivityMessage(item, inProgress);
                case "fileChange":
                    return BuildFileChangeActivityMessage(item, inProgress);
                case "mcpToolCall":
                    return BuildMcpToolActivityMessage(item, inProgress);
                case "webSearch":
                    return BuildWebSearchActivityMessage(item);
                default:
                    return null;
            }
        }

        private static string BuildCommandActivityMessage(Dictionary<string, object> item, bool inProgress)
        {
            List<object> commandActions = Traverse(item, "commandActions") as List<object>;
            Dictionary<string, object> action = commandActions != null && commandActions.Count > 0
                ? commandActions[commandActions.Count - 1] as Dictionary<string, object>
                : null;

            if (action == null)
            {
                return inProgress ? "Running command" : "Ran command";
            }

            switch (ReadString(action, "type"))
            {
                case "read":
                    string fileName = CleanBubbleText(ReadString(action, "name"), 36);
                    string fileLabel = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName;
                    return inProgress
                        ? $"Reading {fileLabel}"
                        : $"Read {fileLabel}";
                case "listFiles":
                    return inProgress ? "Listing files" : "Listed files";
                case "search":
                    string query = CleanBubbleText(ReadString(action, "query"), 36);
                    return string.IsNullOrWhiteSpace(query)
                        ? inProgress ? "Searching files" : "Searched files"
                        : inProgress ? $"Searching \"{query}\"" : $"Searched \"{query}\"";
                default:
                    return inProgress ? "Running command" : "Ran command";
            }
        }

        private static string BuildFileChangeActivityMessage(Dictionary<string, object> item, bool inProgress)
        {
            List<object> changes = Traverse(item, "changes") as List<object>;
            int fileCount = changes != null ? changes.Count : 0;
            string label = fileCount == 1 ? "1 file" : $"{fileCount} files";
            return inProgress ? $"Editing {label}" : $"Edited {label}";
        }

        private static string BuildMcpToolActivityMessage(Dictionary<string, object> item, bool inProgress)
        {
            string toolName = CleanBubbleText((ReadString(item, "tool") ?? string.Empty).Replace('_', ' ').Replace('-', ' '), 36);
            return string.IsNullOrWhiteSpace(toolName)
                ? inProgress ? "Calling tool" : "Called tool"
                : inProgress ? $"Calling {toolName}" : $"Called {toolName}";
        }

        private static string BuildWebSearchActivityMessage(Dictionary<string, object> item)
        {
            string query = CleanBubbleText(ReadString(item, "query"), 36);
            return string.IsNullOrWhiteSpace(query) ? "Searched web" : $"Searched \"{query}\"";
        }

        private static string CleanBubbleText(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string cleaned = Regex.Replace(text, @"\r?\n+", " ");
            cleaned = Regex.Replace(cleaned, @"^\s{0,3}#{1,6}\s+", string.Empty);
            cleaned = Regex.Replace(cleaned, @"\*\*([^*]+)\*\*", "$1");
            cleaned = Regex.Replace(cleaned, @"__([^_]+)__", "$1");
            cleaned = Regex.Replace(cleaned, @"`([^`]+)`", "$1");
            cleaned = Regex.Replace(cleaned, @"\*([^*]+)\*", "$1");
            cleaned = Regex.Replace(cleaned, @"_([^_]+)_", "$1");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            if (cleaned.Length <= maxLength)
            {
                return cleaned;
            }

            return cleaned.Substring(0, Mathf.Max(0, maxLength - 3)).TrimEnd() + "...";
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();

            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, Mathf.Max(0, maxLength - 3)).TrimEnd() + "...";
        }

        private static DateTime? ReadUnixTimestampSeconds(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value))
            {
                return null;
            }

            double seconds;

            if (value is long longValue)
            {
                seconds = longValue;
            }
            else if (value is double doubleValue)
            {
                seconds = doubleValue;
            }
            else if (value is string stringValue && double.TryParse(stringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
            {
                seconds = parsed;
            }
            else
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static bool ReadBool(Dictionary<string, object> root, string key)
        {
            if (root == null || !root.TryGetValue(key, out object value))
            {
                return false;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            return value is string stringValue && bool.TryParse(stringValue, out bool parsed) && parsed;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket localSocket, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            ArraySegment<byte> segment = new ArraySegment<byte>(buffer);

            try
            {
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
            finally
            {
                FailPendingResponses(new IOException("Codex app-server socket closed before a response was received."));
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
            Dictionary<string, object> parameters = root.ContainsKey("params")
                ? root["params"] as Dictionary<string, object>
                : null;

            if (string.Equals(method, "error", StringComparison.Ordinal))
            {
                EnqueueMainThread(() => SetStatus("warning", ReadString(parameters, "message") ?? "Codex app-server error"));
                return;
            }

            HandleServerNotification(method, parameters);
        }

        private void HandleServerNotification(string method, Dictionary<string, object> parameters)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                return;
            }

            switch (method)
            {
                case "thread/started":
                    EnqueueMainThread(() => HandleThreadStarted(parameters));
                    break;
                case "thread/status/changed":
                    EnqueueMainThread(() => HandleThreadStatusChanged(parameters));
                    break;
                case "thread/name/updated":
                    EnqueueMainThread(() => HandleThreadNameUpdated(parameters));
                    break;
                case "thread/archived":
                    EnqueueMainThread(() => HandleThreadArchived(parameters));
                    break;
                case "thread/unarchived":
                    EnqueueMainThread(() => HandleThreadUnarchived(parameters));
                    break;
                case "thread/closed":
                    EnqueueMainThread(() => HandleThreadClosed(parameters));
                    break;
                case "turn/started":
                    EnqueueMainThread(() => HandleTurnLifecycle(parameters, true));
                    break;
                case "turn/completed":
                    EnqueueMainThread(() => HandleTurnLifecycle(parameters, false));
                    break;
                case "item/started":
                    EnqueueMainThread(() => HandleThreadItem(parameters, true));
                    break;
                case "item/completed":
                case "rawResponseItem/completed":
                    EnqueueMainThread(() => HandleThreadItem(parameters, false));
                    break;
                case "item/agentMessage/delta":
                case "item/plan/delta":
                case "command/exec/outputDelta":
                case "process/outputDelta":
                case "item/commandExecution/outputDelta":
                case "item/fileChange/outputDelta":
                case "item/fileChange/patchUpdated":
                case "item/mcpToolCall/progress":
                case "serverRequest/resolved":
                case "thread/tokenUsage/updated":
                    break;
                case "warning":
                case "guardianWarning":
                case "configWarning":
                case "deprecationNotice":
                    EnqueueMainThread(() => SetStatus("warning", ReadString(parameters, "message") ?? method));
                    break;
            }
        }

        private void HandleThreadStarted(Dictionary<string, object> parameters)
        {
            AppServerThreadRecord record = ReadAppServerThreadRecord(Traverse(parameters, "thread") as Dictionary<string, object>);

            if (record == null)
            {
                return;
            }

            UpsertMirroredThread(record);
            subscribedThreadIds.Add(record.id);
            SyncMirroredWorld("Websocket thread event received.");
        }

        private void HandleThreadStatusChanged(Dictionary<string, object> parameters)
        {
            string threadId = ReadString(parameters, "threadId");

            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            AppServerThreadRecord record = GetOrCreateMirroredThread(threadId);
            Dictionary<string, object> thread = new Dictionary<string, object>
            {
                ["status"] = Traverse(parameters, "status")
            };
            string statusId = ReadThreadStatusId(thread);
            float ageMinutes = Mathf.Max(0f, (float)(DateTime.UtcNow - record.updatedAtUtc).TotalMinutes);
            record.statusMessage = BuildThreadStatusMessage(thread, statusId);
            record.phase = DetermineThreadPhase(statusId, ageMinutes);
            record.updatedAtUtc = DateTime.UtcNow;
            SyncMirroredWorld("Websocket thread status updated.");
        }

        private void HandleThreadNameUpdated(Dictionary<string, object> parameters)
        {
            string threadId = ReadString(parameters, "threadId");

            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            AppServerThreadRecord record = GetOrCreateMirroredThread(threadId);
            string threadName = ReadString(parameters, "threadName");

            if (!string.IsNullOrWhiteSpace(threadName))
            {
                record.title = CleanBubbleText(threadName, 80);
            }

            record.updatedAtUtc = DateTime.UtcNow;
            SyncMirroredWorld("Websocket thread name updated.");
        }

        private void HandleThreadArchived(Dictionary<string, object> parameters)
        {
            string threadId = ReadString(parameters, "threadId");

            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            if (mirroredThreads.TryGetValue(threadId, out AppServerThreadRecord record))
            {
                mirroredArchivedPets[threadId] = CreateArchivedPetSnapshot(record);
                mirroredThreads.Remove(threadId);
            }
            else
            {
                mirroredArchivedPets[threadId] = new AquariumArchivedPetSnapshot
                {
                    id = threadId,
                    title = Shorten(threadId, 12),
                    statusMessage = "Archived"
                };
            }

            pendingWorldThreads.Remove(threadId);
            subscribedThreadIds.Remove(threadId);
            SyncMirroredWorld("Websocket thread archived.");
        }

        private void HandleThreadUnarchived(Dictionary<string, object> parameters)
        {
            string threadId = ReadString(parameters, "threadId");

            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            if (mirroredArchivedPets.TryGetValue(threadId, out AquariumArchivedPetSnapshot archivedPet))
            {
                mirroredArchivedPets.Remove(threadId);
                UpsertMirroredThread(new AppServerThreadRecord
                {
                    id = threadId,
                    title = archivedPet.title,
                    statusMessage = "Idle",
                    phase = "idle",
                    updatedAtUtc = DateTime.UtcNow,
                    source = "app-server"
                });
            }

            SubscribeToThread(threadId);
            SyncMirroredWorld("Websocket thread unarchived.");
        }

        private void HandleThreadClosed(Dictionary<string, object> parameters)
        {
            string threadId = ReadString(parameters, "threadId");

            if (string.IsNullOrWhiteSpace(threadId) || !mirroredThreads.TryGetValue(threadId, out AppServerThreadRecord record))
            {
                return;
            }

            subscribedThreadIds.Remove(threadId);
            record.statusMessage = "Idle";
            record.phase = "idle";
            record.updatedAtUtc = DateTime.UtcNow;
            SyncMirroredWorld("Websocket thread closed.");
        }

        private void HandleTurnLifecycle(Dictionary<string, object> parameters, bool started)
        {
            string threadId = ReadString(parameters, "threadId");

            if (string.IsNullOrWhiteSpace(threadId))
            {
                return;
            }

            AppServerThreadRecord record = GetOrCreateMirroredThread(threadId);
            record.statusMessage = started ? "Thinking" : ReadTurnCompletionMessage(Traverse(parameters, "turn") as Dictionary<string, object>);
            record.phase = started ? "working" : DetermineThreadPhase(0f);
            record.updatedAtUtc = DateTime.UtcNow;
            SyncMirroredWorld(started ? "Websocket turn started." : "Websocket turn completed.");
        }

        private void HandleThreadItem(Dictionary<string, object> parameters, bool started)
        {
            string threadId = ReadString(parameters, "threadId");
            Dictionary<string, object> item = Traverse(parameters, "item") as Dictionary<string, object>;

            if (string.IsNullOrWhiteSpace(threadId) || item == null)
            {
                return;
            }

            string message = BuildItemStatusMessage(item, started);

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            AppServerThreadRecord record = GetOrCreateMirroredThread(threadId);
            record.statusMessage = message;
            record.phase = DetermineItemPhase(item, started);
            record.updatedAtUtc = DateTime.UtcNow;
            SyncMirroredWorld(started ? "Websocket item started." : "Websocket item completed.");
        }

        private AppServerThreadRecord GetOrCreateMirroredThread(string threadId)
        {
            if (mirroredThreads.TryGetValue(threadId, out AppServerThreadRecord record))
            {
                return record;
            }

            record = new AppServerThreadRecord
            {
                id = threadId.Trim(),
                title = Shorten(threadId, 12),
                statusMessage = "Idle",
                phase = "idle",
                updatedAtUtc = DateTime.UtcNow,
                source = "app-server"
            };
            mirroredThreads[record.id] = record;
            mirroredArchivedPets.Remove(record.id);
            pendingWorldThreads.Remove(record.id);
            return record;
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

            try
            {
                await SendJsonAsync(
                    MiniJson.Serialize(
                        new Dictionary<string, object>
                        {
                            ["id"] = currentRequestId,
                            ["method"] = method,
                            ["params"] = parameters
                        }),
                    token);
            }
            catch
            {
                pendingResponses.TryRemove(currentRequestId, out _);
                throw;
            }

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
                if (localSocket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("Codex app-server socket is not open.");
                }

                await localSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, token);
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

        private void FailPendingResponses(Exception exception)
        {
            foreach (KeyValuePair<int, TaskCompletionSource<Dictionary<string, object>>> pair in pendingResponses)
            {
                if (pendingResponses.TryRemove(pair.Key, out TaskCompletionSource<Dictionary<string, object>> responseSource))
                {
                    responseSource.TrySetException(exception);
                }
            }
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

        private static List<string> ReadStringList(Dictionary<string, object> root, params string[] path)
        {
            List<string> values = new List<string>();
            object value = Traverse(root, path);

            if (!(value is List<object> items))
            {
                return values;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is string item && !string.IsNullOrWhiteSpace(item))
                {
                    values.Add(item.Trim());
                }
            }

            return values;
        }

        private static string GetUnityProjectRootPath()
        {
            try
            {
                string assetsPath = Application.dataPath;

                if (!string.IsNullOrWhiteSpace(assetsPath))
                {
                    DirectoryInfo assetsDirectory = new DirectoryInfo(assetsPath);

                    if (string.Equals(assetsDirectory.Name, "Assets", StringComparison.OrdinalIgnoreCase) && assetsDirectory.Parent != null)
                    {
                        return assetsDirectory.Parent.FullName;
                    }
                }

                string currentDirectory = Directory.GetCurrentDirectory();
                return Path.IsPathRooted(currentDirectory) ? currentDirectory : null;
            }
            catch (Exception)
            {
                return null;
            }
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
