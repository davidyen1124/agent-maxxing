using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Forest
{
    internal sealed class DirectWebsiteBuildClient
    {
        private const string TensorlakeApiBaseUrl = "https://api.tensorlake.ai";
        private const string OpenAiResponsesUrl = "https://api.openai.com/v1/responses";
        private const string DefaultWebsiteModel = "gpt-5.5";
        private const string DefaultSandboxPrefix = "forest-site";
        private const int DefaultPreviewPort = 8080;

        private readonly ForestUserSettings settings;

        public DirectWebsiteBuildClient(ForestUserSettings settings)
        {
            this.settings = settings ?? new ForestUserSettings();
        }

        public bool CanBuild => !string.IsNullOrWhiteSpace(settings.openAiApiKey)
            && !string.IsNullOrWhiteSpace(settings.tensorlakeApiKey);

        public async Task<WebsiteBuildResult> BuildAsync(
            string idea,
            string style,
            string siteType,
            Action<string> progress,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(settings.openAiApiKey))
            {
                throw new InvalidOperationException($"Set openAiApiKey in {ForestUserSettings.RelativePath}.");
            }

            if (string.IsNullOrWhiteSpace(settings.tensorlakeApiKey))
            {
                throw new InvalidOperationException($"Set tensorlakeApiKey in {ForestUserSettings.RelativePath}.");
            }

            string safeIdea = string.IsNullOrWhiteSpace(idea) ? "a small polished demo website" : idea.Trim();
            int previewPort = settings.TensorlakePreviewPortOr(DefaultPreviewPort);
            progress?.Invoke("Generating website files");
            WebsiteFiles files = await GenerateWebsiteFilesAsync(safeIdea, style, siteType, token);

            progress?.Invoke("Creating Tensorlake sandbox");
            string sandboxId = await CreateSandboxAsync(token);
            string previewUrl = $"https://{previewPort}-{sandboxId}.sandbox.tensorlake.ai/";

            progress?.Invoke("Uploading files to sandbox");
            await WriteSandboxFileAsync(sandboxId, "/workspace/site/index.html", files.indexHtml, token);
            await WriteSandboxFileAsync(sandboxId, "/workspace/site/styles.css", files.stylesCss, token);
            await WriteSandboxFileAsync(sandboxId, "/workspace/site/script.js", files.scriptJs, token);
            await WriteSandboxFileAsync(sandboxId, "/workspace/site/package.json", BuildStaticPackageJson(), token);

            progress?.Invoke("Starting sandbox preview");
            await StartSandboxProcessAsync(
                sandboxId,
                "bash",
                new List<object>
                {
                    "-lc",
                    $"cd /workspace/site && python3 -m http.server {previewPort} --bind 0.0.0.0"
                },
                token);
            await ExposeSandboxPortAsync(sandboxId, previewPort, token);

            string deployedUrl = string.Empty;
            if (HasInsForgeDeploymentCredentials())
            {
                progress?.Invoke("Deploying to InsForge");
                deployedUrl = await TryDeployToInsForgeAsync(files, token);
            }
            else
            {
                progress?.Invoke("InsForge credentials missing; keeping Tensorlake preview");
            }

            progress?.Invoke(string.IsNullOrWhiteSpace(deployedUrl) ? "Preview ready" : "Deployment ready");
            return new WebsiteBuildResult
            {
                title = files.title,
                sandboxId = sandboxId,
                previewUrl = previewUrl,
                deployedUrl = deployedUrl,
                indexHtml = files.indexHtml
            };
        }

        private bool HasInsForgeDeploymentCredentials()
        {
            return !string.IsNullOrWhiteSpace(settings.insforgeBaseUrl)
                && !string.IsNullOrWhiteSpace(settings.insforgeApiKey);
        }

        private async Task<WebsiteFiles> GenerateWebsiteFilesAsync(string idea, string style, string siteType, CancellationToken token)
        {
            string prompt =
                "Create a polished, demo-friendly static website. Return only JSON with keys title, indexHtml, stylesCss, scriptJs. " +
                "Do not use external build tools, external assets, forms that submit remotely, or inline SVG. " +
                "The site must work as plain files served by python3 -m http.server. " +
                $"Idea: {idea}\nStyle hints: {CleanOptional(style)}\nSite type: {CleanOptional(siteType)}";

            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["model"] = settings.OpenAiWebsiteModelOr(DefaultWebsiteModel),
                ["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = "none"
                },
                ["instructions"] = "You generate compact static website files. Output valid JSON only.",
                ["input"] = prompt
            };

            string response = await SendJsonAsync(
                OpenAiResponsesUrl,
                "POST",
                ForestDirectorBridge.MiniJson.Serialize(body),
                settings.openAiApiKey,
                token);
            string text = ExtractOpenAiOutputText(ForestDirectorBridge.MiniJson.Deserialize(response) as Dictionary<string, object>);
            Dictionary<string, object> json = ForestDirectorBridge.MiniJson.Deserialize(ExtractJsonObject(text)) as Dictionary<string, object>;

            if (json == null)
            {
                throw new InvalidOperationException("OpenAI did not return parseable website JSON.");
            }

            return new WebsiteFiles
            {
                title = ReadString(json, "title") ?? "Generated Website",
                indexHtml = RequireString(json, "indexHtml"),
                stylesCss = RequireString(json, "stylesCss"),
                scriptJs = ReadString(json, "scriptJs") ?? string.Empty
            };
        }

        private async Task<string> CreateSandboxAsync(CancellationToken token)
        {
            string name = $"{settings.TensorlakeSandboxNamePrefixOr(DefaultSandboxPrefix)}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["name"] = name,
                ["timeout_secs"] = 600,
                ["resources"] = new Dictionary<string, object>
                {
                    ["cpus"] = 1.0,
                    ["memory_mb"] = 1024,
                    ["disk_mb"] = 10240
                }
            };

            string response = await SendJsonAsync(
                $"{TensorlakeApiBaseUrl}/sandboxes",
                "POST",
                ForestDirectorBridge.MiniJson.Serialize(body),
                settings.tensorlakeApiKey,
                token);
            Dictionary<string, object> json = ForestDirectorBridge.MiniJson.Deserialize(response) as Dictionary<string, object>;
            string sandboxId = ReadString(json, "sandbox_id")
                ?? ReadString(json, "sandboxId")
                ?? ReadString(json, "id")
                ?? ReadString(json, "sandbox", "sandbox_id")
                ?? ReadString(json, "sandbox", "id");

            if (string.IsNullOrWhiteSpace(sandboxId))
            {
                throw new InvalidOperationException("Tensorlake did not return a sandbox id.");
            }

            return sandboxId.Trim();
        }

        private async Task WriteSandboxFileAsync(string sandboxId, string path, string content, CancellationToken token)
        {
            string url = $"https://{sandboxId}.sandbox.tensorlake.ai/api/v1/files?path={UnityWebRequest.EscapeURL(path)}";
            await SendBytesAsync(url, "PUT", Encoding.UTF8.GetBytes(content ?? string.Empty), settings.tensorlakeApiKey, token);
        }

        private async Task StartSandboxProcessAsync(string sandboxId, string command, List<object> args, CancellationToken token)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["command"] = command,
                ["args"] = args,
                ["working_dir"] = "/workspace/site"
            };
            await SendJsonAsync(
                $"https://{sandboxId}.sandbox.tensorlake.ai/api/v1/processes",
                "POST",
                ForestDirectorBridge.MiniJson.Serialize(body),
                settings.tensorlakeApiKey,
                token);
        }

        private async Task ExposeSandboxPortAsync(string sandboxId, int previewPort, CancellationToken token)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["allow_unauthenticated_access"] = true,
                ["exposed_ports"] = new List<object> { previewPort }
            };
            await SendJsonAsync(
                $"{TensorlakeApiBaseUrl}/sandboxes/{sandboxId}",
                "PATCH",
                ForestDirectorBridge.MiniJson.Serialize(body),
                settings.tensorlakeApiKey,
                token);
        }

        private async Task<string> TryDeployToInsForgeAsync(WebsiteFiles files, CancellationToken token)
        {
            string baseUrl = NormalizeBaseUrl(settings.insforgeBaseUrl);
            List<InsForgeFileEntry> entries = new List<InsForgeFileEntry>
            {
                new InsForgeFileEntry("index.html", files.indexHtml),
                new InsForgeFileEntry("styles.css", files.stylesCss),
                new InsForgeFileEntry("script.js", files.scriptJs),
                new InsForgeFileEntry("package.json", BuildStaticPackageJson())
            };
            List<object> manifest = new List<object>();

            foreach (InsForgeFileEntry entry in entries)
            {
                manifest.Add(new Dictionary<string, object>
                {
                    ["path"] = entry.path,
                    ["sha1"] = entry.sha1,
                    ["sha"] = entry.sha1,
                    ["size"] = entry.bytes.Length
                });
            }

            Dictionary<string, object> createBody = new Dictionary<string, object>
            {
                ["name"] = files.title,
                ["files"] = manifest
            };
            string createResponse = await SendJsonAsync(
                $"{baseUrl}/api/deployments/direct",
                "POST",
                ForestDirectorBridge.MiniJson.Serialize(createBody),
                settings.insforgeApiKey,
                token);
            Dictionary<string, object> createJson = ForestDirectorBridge.MiniJson.Deserialize(createResponse) as Dictionary<string, object>;
            string deploymentId = ReadString(createJson, "id")
                ?? ReadString(createJson, "deploymentId")
                ?? ReadString(createJson, "deployment", "id");

            if (string.IsNullOrWhiteSpace(deploymentId))
            {
                throw new InvalidOperationException("InsForge did not return a deployment id.");
            }

            foreach (InsForgeFileEntry entry in entries)
            {
                string fileId = FindInsForgeFileId(createJson, entry.path, entry.sha1);

                if (string.IsNullOrWhiteSpace(fileId))
                {
                    throw new InvalidOperationException($"InsForge did not return a file id for {entry.path}.");
                }

                await SendBytesAsync(
                    $"{baseUrl}/api/deployments/{deploymentId}/files/{fileId}/content",
                    "PUT",
                    entry.bytes,
                    settings.insforgeApiKey,
                    token);
            }

            string startResponse = await SendJsonAsync(
                $"{baseUrl}/api/deployments/{deploymentId}/start",
                "POST",
                "{}",
                settings.insforgeApiKey,
                token);
            Dictionary<string, object> startJson = ForestDirectorBridge.MiniJson.Deserialize(startResponse) as Dictionary<string, object>;
            return ReadString(startJson, "url")
                ?? ReadString(startJson, "deploymentUrl")
                ?? ReadString(startJson, "appUrl")
                ?? ReadString(startJson, "deployment", "url")
                ?? string.Empty;
        }

        private static async Task<string> SendJsonAsync(string url, string method, string body, string bearerToken, CancellationToken token)
        {
            using UnityWebRequest request = new UnityWebRequest(url, method);
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
            request.SetRequestHeader("Content-Type", "application/json");
            await SendAsync(request, token);
            return request.downloadHandler.text;
        }

        private static async Task SendBytesAsync(string url, string method, byte[] bytes, string bearerToken, CancellationToken token)
        {
            using UnityWebRequest request = new UnityWebRequest(url, method);
            request.uploadHandler = new UploadHandlerRaw(bytes ?? Array.Empty<byte>());
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
            request.SetRequestHeader("Content-Type", "application/octet-stream");
            await SendAsync(request, token);
        }

        private static async Task SendAsync(UnityWebRequest request, CancellationToken token)
        {
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                if (token.IsCancellationRequested)
                {
                    request.Abort();
                    token.ThrowIfCancellationRequested();
                }

                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                string detail = string.IsNullOrWhiteSpace(request.downloadHandler.text)
                    ? request.error
                    : request.downloadHandler.text;
                throw new InvalidOperationException($"{request.method} {request.url} failed ({request.responseCode}): {Shorten(detail, 240)}");
            }
        }

        private static string ExtractOpenAiOutputText(Dictionary<string, object> response)
        {
            object output = Traverse(response, "output");

            if (output is List<object> items)
            {
                StringBuilder builder = new StringBuilder();

                foreach (object item in items)
                {
                    if (!(item is Dictionary<string, object> dictionary))
                    {
                        continue;
                    }

                    object content = Traverse(dictionary, "content");
                    if (!(content is List<object> parts))
                    {
                        continue;
                    }

                    foreach (object part in parts)
                    {
                        if (part is Dictionary<string, object> partDictionary)
                        {
                            string text = ReadString(partDictionary, "text");
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                builder.Append(text);
                            }
                        }
                    }
                }

                if (builder.Length > 0)
                {
                    return builder.ToString();
                }
            }

            return ReadString(response, "output_text") ?? string.Empty;
        }

        private static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "{}";
            }

            string trimmed = text.Trim();
            int start = trimmed.IndexOf('{');
            int end = trimmed.LastIndexOf('}');
            return start >= 0 && end > start ? trimmed.Substring(start, end - start + 1) : trimmed;
        }

        private static string FindInsForgeFileId(Dictionary<string, object> root, string path, string sha1)
        {
            List<object> candidates = new List<object>();
            CollectLists(root, candidates, 0);

            foreach (object candidate in candidates)
            {
                if (!(candidate is Dictionary<string, object> dictionary))
                {
                    continue;
                }

                string candidatePath = ReadString(dictionary, "path")
                    ?? ReadString(dictionary, "relativePath")
                    ?? ReadString(dictionary, "filePath");
                string candidateSha = ReadString(dictionary, "sha1") ?? ReadString(dictionary, "sha");

                if (string.Equals(candidatePath, path, StringComparison.Ordinal)
                    || string.Equals(candidateSha, sha1, StringComparison.OrdinalIgnoreCase))
                {
                    return ReadString(dictionary, "fileId")
                        ?? ReadString(dictionary, "file_id")
                        ?? ReadString(dictionary, "id");
                }
            }

            return null;
        }

        private static void CollectLists(object value, List<object> output, int depth)
        {
            if (value == null || depth > 8)
            {
                return;
            }

            if (value is List<object> list)
            {
                output.AddRange(list);
                foreach (object item in list)
                {
                    CollectLists(item, output, depth + 1);
                }
            }
            else if (value is Dictionary<string, object> dictionary)
            {
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    CollectLists(pair.Value, output, depth + 1);
                }
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

        private static string ReadString(Dictionary<string, object> root, params string[] path)
        {
            object value = Traverse(root, path);
            return value == null ? null : Convert.ToString(value);
        }

        private static string RequireString(Dictionary<string, object> root, string key)
        {
            string value = ReadString(root, key);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Generated website JSON is missing {key}.");
            }

            return value;
        }

        private static string NormalizeBaseUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
        }

        private static string CleanOptional(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
        }

        private static string BuildStaticPackageJson()
        {
            return "{\"scripts\":{\"build\":\"rm -rf public && mkdir -p public && cp index.html styles.css script.js public/\"},\"dependencies\":{},\"devDependencies\":{}}";
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, Mathf.Max(0, maxLength - 3)) + "...";
        }

        private sealed class WebsiteFiles
        {
            public string title;
            public string indexHtml;
            public string stylesCss;
            public string scriptJs;
        }

        private sealed class InsForgeFileEntry
        {
            public readonly string path;
            public readonly byte[] bytes;
            public readonly string sha1;

            public InsForgeFileEntry(string path, string content)
            {
                this.path = path;
                bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);

                byte[] hash;
                using (SHA1 sha = SHA1.Create())
                {
                    hash = sha.ComputeHash(bytes);
                }
                StringBuilder builder = new StringBuilder(hash.Length * 2);

                for (int i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                sha1 = builder.ToString();
            }
        }
    }

    internal sealed class WebsiteBuildResult
    {
        public string title;
        public string sandboxId;
        public string previewUrl;
        public string deployedUrl;
        public string indexHtml;
    }
}
