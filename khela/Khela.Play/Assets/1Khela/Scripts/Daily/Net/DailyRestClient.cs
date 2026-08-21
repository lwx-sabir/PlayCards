using System;
using System.IO;
using System.Net.Http;              // HttpMethod — the internal method selector (no HttpClient)
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Best.HTTP;
using Khela.Common.Daily;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Net;            // reuse ApiResult<T> (transport result shape is game-agnostic)
using UnityEngine;

namespace PlayCard.Daily.Net
{
    /// <summary>
    /// REST client for the daily login reward (<c>/api/daily/*</c>). Mirrors <c>PassRestClient</c> exactly —
    /// Best.HTTP transport, System.Text.Json camelCase, the <see cref="AccountManager"/> JWT with a one-shot 401
    /// refresh, <see cref="ApiResult{T}"/> results.
    ///
    /// Every decision (which day is claimable, what a missed one costs, when the day flips) is already made
    /// server-side; this only carries snapshots and intents.
    /// </summary>
    public sealed class DailyRestClient
    {
        private static DailyRestClient _instance;
        public static DailyRestClient Instance => _instance ??= new DailyRestClient();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string Base => AppConfig.Instance.BaseApiUrl;

        /// <summary>The whole daily screen: the ladder, per-day state and the countdowns.</summary>
        public Task<ApiResult<DailyStateDto>> GetStateAsync()
            => SendAsync<DailyStateDto>(HttpMethod.Get, "/api/daily");

        /// <summary>Claim a day. Omit <paramref name="node"/> for the first one currently claimable; pass an earlier
        /// one with <paramref name="useAds"/> to spend rewarded-ad credits on it.</summary>
        public Task<ApiResult<DailyClaimResultDto>> ClaimAsync(int? node = null, bool useAds = false)
            => SendAsync<DailyClaimResultDto>(HttpMethod.Post, "/api/daily/claim",
                new ClaimBody { node = node, useAds = useAds });

        /// <summary>Ask for a single-use token to hand the rewarded-ad SDK as custom data. The SERVER decides whether
        /// this day may be unlocked with ads — a refusal here is the answer, not something to work around.</summary>
        public Task<ApiResult<DailyAdIntentDto>> AdIntentAsync(int node)
            => SendAsync<DailyAdIntentDto>(HttpMethod.Post, "/api/daily/ad-intent", new AdIntentBody { node = node });

        // Request shapes are the controller's own, so they live here rather than in the shared DTO assembly.
        private sealed class ClaimBody
        {
            public int? node { get; set; }
            public bool useAds { get; set; }
        }

        private sealed class AdIntentBody
        {
            public int node { get; set; }
        }

        // ---------- core (mirrors PassRestClient) ----------

        private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object body = null)
        {
            var raw = await SendRawAsync(method, path, body);
            if (!raw.Ok) return ApiResult<T>.Fail(raw.Status, raw.Error);
            try
            {
                return ApiResult<T>.Success(JsonSerializer.Deserialize<T>(raw.Body, JsonOpts), raw.Status);
            }
            catch (Exception ex)
            {
                return ApiResult<T>.Fail(raw.Status, $"Parse error: {ex.Message}");
            }
        }

        private readonly struct Raw
        {
            public readonly bool Ok;
            public readonly int Status;
            public readonly string Body;
            public readonly string Error;
            public Raw(bool ok, int status, string body, string error) { Ok = ok; Status = status; Body = body; Error = error; }
        }

        private async Task<Raw> SendRawAsync(HttpMethod method, string path, object body, bool isRetry = false)
        {
            try
            {
                var req = new HTTPRequest(new Uri(Base + path), ToBest(method));
                req.TimeoutSettings.Timeout = TimeSpan.FromSeconds(AppConfig.Instance.RequestTimeoutSeconds);

                var token = AccountManager.Instance != null ? AccountManager.Instance.JwtToken : null;
                if (!string.IsNullOrEmpty(token))
                    req.SetHeader("Authorization", "Bearer " + token);

                if (body != null)
                {
                    var json = JsonSerializer.Serialize(body, JsonOpts);
                    req.SetHeader("Content-Type", "application/json; charset=utf-8");
                    req.UploadSettings.UploadStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                }

                var resp = await req.GetHTTPResponseAsync();
                return new Raw(true, resp.StatusCode, resp.DataAsText, null);
            }
            catch (AsyncHTTPException hex)
            {
                if (hex.StatusCode == 401 && !isRetry && AccountManager.Instance != null)
                {
                    if (await AccountManager.Instance.HandleAuthFailureAsync())
                        return await SendRawAsync(method, path, body, isRetry: true);
                }
                return new Raw(false, hex.StatusCode, hex.Content, ExtractMessage(hex.Content) ?? hex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DailyRestClient] {method} {path} failed: {ex.Message}");
                return new Raw(false, 0, null, ex.Message);
            }
        }

        private static HTTPMethods ToBest(HttpMethod m)
        {
            if (m == HttpMethod.Get) return HTTPMethods.Get;
            if (m == HttpMethod.Post) return HTTPMethods.Post;
            if (m == HttpMethod.Put) return HTTPMethods.Put;
            if (m == HttpMethod.Delete) return HTTPMethods.Delete;
            return HTTPMethods.Get;
        }

        /// <summary>Pull the human-readable message out of an ASP.NET problem/JSON error body, if there is one.</summary>
        private static string ExtractMessage(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            try
            {
                using var doc = JsonDocument.Parse(content);
                foreach (var name in new[] { "error", "message", "title", "detail" })
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString();
            }
            catch { }
            return null;
        }
    }
}
