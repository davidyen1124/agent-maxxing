using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Underwater
{
    internal sealed class NiaApiClient
    {
        private const string DefaultBaseUrl = "https://apigcp.trynia.ai/v2";

        private readonly string baseUrl;
        private readonly string apiKeyEnvironmentVariable;
        private readonly string[] repositories;
        private readonly string[] dataSources;
        private readonly int maxTokens;

        public NiaApiClient(string baseUrl, string apiKeyEnvironmentVariable, string[] repositories, string[] dataSources, int maxTokens)
        {
            this.baseUrl = NormalizeBaseUrl(baseUrl);
            this.apiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable)
                ? "NIA_API_KEY"
                : apiKeyEnvironmentVariable.Trim();
            this.repositories = CleanFilters(repositories);
            this.dataSources = CleanFilters(dataSources);
            this.maxTokens = Mathf.Clamp(maxTokens, 100, 100000);
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ReadApiKey());

        public async Task<NiaSearchResult> QueryAsync(string query, CancellationToken token)
        {
            string apiKey = ReadApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException($"Set {apiKeyEnvironmentVariable} before launching Unity to enable Nia search.");
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("Nia query cannot be empty.", nameof(query));
            }

            Dictionary<string, object> payload = BuildSearchPayload(query.Trim());
            string body = AquariumDirectorBridge.MiniJson.Serialize(payload);
            string responseBody = await PostJsonAsync($"{baseUrl}/search", body, apiKey, token);
            Dictionary<string, object> response = AquariumDirectorBridge.MiniJson.Deserialize(responseBody) as Dictionary<string, object>;

            if (response == null)
            {
                throw new InvalidOperationException("Nia returned a response that could not be parsed.");
            }

            string answer = ReadFirstAnswer(response);

            if (string.IsNullOrWhiteSpace(answer))
            {
                answer = "Nia returned search results, but no synthesized answer field was found.";
            }

            return new NiaSearchResult
            {
                Answer = answer.Trim(),
                SourceLabels = ReadSourceLabels(response)
            };
        }

        private Dictionary<string, object> BuildSearchPayload(string query)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["mode"] = "query",
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = query
                    }
                },
                ["search_mode"] = "unified",
                ["stream"] = false,
                ["include_sources"] = true,
                ["fast_mode"] = false,
                ["skip_llm"] = false,
                ["max_tokens"] = maxTokens
            };

            if (repositories.Length > 0)
            {
                payload["repositories"] = new List<object>(repositories);
            }

            if (dataSources.Length > 0)
            {
                payload["data_sources"] = new List<object>(dataSources);
            }

            return payload;
        }

        private static async Task<string> PostJsonAsync(string url, string body, string apiKey, CancellationToken token)
        {
            using UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            request.SetRequestHeader("Content-Type", "application/json");

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
                throw new InvalidOperationException($"Nia request failed ({request.responseCode}): {Shorten(detail, 240)}");
            }

            return request.downloadHandler.text;
        }

        private static string ReadFirstAnswer(Dictionary<string, object> response)
        {
            return ReadString(response, "answer")
                ?? ReadString(response, "response")
                ?? ReadString(response, "summary")
                ?? ReadString(response, "result", "answer")
                ?? ReadString(response, "result", "response")
                ?? ReadString(response, "result", "summary")
                ?? ReadString(response, "data", "answer")
                ?? ReadString(response, "data", "response")
                ?? FindFirstStringByKey(response, new HashSet<string> { "answer", "response", "summary", "final" }, 6);
        }

        private static string[] ReadSourceLabels(Dictionary<string, object> response)
        {
            List<string> labels = new List<string>();
            CollectSourceLabels(response, labels, 0);
            return labels.ToArray();
        }

        private static void CollectSourceLabels(object value, List<string> labels, int depth)
        {
            if (value == null || labels.Count >= 4 || depth > 7)
            {
                return;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                string label = ReadString(dictionary, "display_name")
                    ?? ReadString(dictionary, "title")
                    ?? ReadString(dictionary, "url")
                    ?? ReadString(dictionary, "source_id");

                if (!string.IsNullOrWhiteSpace(label) && !labels.Contains(label))
                {
                    labels.Add(label);
                }

                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (labels.Count >= 4)
                    {
                        return;
                    }

                    CollectSourceLabels(pair.Value, labels, depth + 1);
                }
            }
            else if (value is List<object> list)
            {
                for (int i = 0; i < list.Count && labels.Count < 4; i++)
                {
                    CollectSourceLabels(list[i], labels, depth + 1);
                }
            }
        }

        private static string FindFirstStringByKey(object value, HashSet<string> keyNames, int depth)
        {
            if (value == null || depth < 0)
            {
                return null;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (keyNames.Contains(pair.Key) && pair.Value is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    string nested = FindFirstStringByKey(pair.Value, keyNames, depth - 1);

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
                    string nested = FindFirstStringByKey(list[i], keyNames, depth - 1);

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
            if (root == null || path == null || path.Length == 0)
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

        private string ReadApiKey()
        {
            return Environment.GetEnvironmentVariable(apiKeyEnvironmentVariable);
        }

        private static string NormalizeBaseUrl(string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim();
            return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized.Substring(0, normalized.Length - 1) : normalized;
        }

        private static string[] CleanFilters(string[] filters)
        {
            if (filters == null || filters.Length == 0)
            {
                return Array.Empty<string>();
            }

            List<string> cleaned = new List<string>();

            for (int i = 0; i < filters.Length; i++)
            {
                string filter = filters[i];

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    cleaned.Add(filter.Trim());
                }
            }

            return cleaned.ToArray();
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, Mathf.Max(0, maxLength - 3)).TrimEnd() + "...";
        }
    }

    internal sealed class NiaSearchResult
    {
        public string Answer;
        public string[] SourceLabels;
    }
}
