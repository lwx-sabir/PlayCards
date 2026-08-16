using System;
using System.IO;
using System.Net.Http;              // HttpMethod — the internal method selector (no HttpClient)
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Best.HTTP;
using Khela.Common.Pass;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Net;            // reuse ApiResult<T> (transport result shape is game-agnostic)
using UnityEngine;

namespace PlayCard.Pass.Net
{
    /// <summary>
    /// REST client for the Monthly Pass (<c>/api/pass/*</c>). Mirrors the other clients exactly — Best.HTTP transport,
    /// System.Text.Json camelCase, the <see cref="AccountManager"/> JWT with a one-shot 401 refresh,
    /// <see cref="ApiResult{T}"/> results.
    ///
    /// Every decision (what's claimable, what a missed day costs, when the day flips) is already made server-side;
    /// this only carries snapshots and intents.
    /// </summary>
    public sealed class PassRestClient
    {
        private static PassRestClient _instance;
        public static PassRestClient Instance => _instance ??= new PassRestClient();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string Base => AppConfig.Instance.BaseApiUrl;

        /// <summary>The whole pass screen: ladder, per-day state, golden state, countdowns.</summary>
        public Task<ApiResult<PassStateDto>> GetStateAsync(string passKey = null)
            => SendAsync<PassStateDto>(HttpMethod.Get, string.IsNullOrEmpty(passKey) ? "/api/pass" : $"/api/pass?passKey={Uri.EscapeDataString(passKey)}");

        /// <summary>Claim one day. Omit <paramref name="node"/> for today; pass an earlier one to catch up, with
        /// <paramref name="useAds"/> to spend rewarded-ad credits on it.</summary>
        public Task<ApiResult<PassClaimResultDto>> ClaimAsync(int? node = null, bool useAds = false, string passKey = null)
            => SendAsync<PassClaimResultDto>(HttpMethod.Post, "/api/pass/claim",
                new ClaimBody { passKey = passKey, node = node, useAds = useAds });

        /// <summary>Claim everything currently free, oldest first. Never spends ad credits.</summary>
        public Task<ApiResult<PassClaimResultDto>> ClaimAllAsync(string passKey = null)
            => SendAsync<PassClaimResultDto>(HttpMethod.Post, "/api/pass/claim-all", new ClaimBody { passKey = passKey });

        /// <summary>Ask for a single-use token to hand the rewarded-ad SDK as custom data. The SERVER decides whether
        /// this day may be unlocked with ads — a refusal here is the answer, not something to work around.</summary>
        public Task<ApiResult<PassAdIntentDto>> AdIntentAsync(int node, string passKey = null)
            => SendAsync<PassAdIntentDto>(HttpMethod.Post, "/api/pass/ad-intent", new AdIntentBody { passKey = passKey, node = node });

        // Request shapes are the controller's own, so they live here rather than in the shared DTO assembly.
        private sealed class ClaimBody
        {
            public string passKey { get; set; }
            public int? node { get; set; }
            public bool useAds { get; set; }
        }

        private sealed class AdIntentBody
        {
            public string passKey { get; set; }
            public int node { get; set; }
        }

        // ---------- core (mirrors VideoPokerRestClient / TcpRestClient) ----------

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
                Debug.LogError($"[PassRestClient] {method} {path} failed: {ex.Message}");
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
