using System;
using System.IO;
using System.Text;
using System.Net.Http;              // HttpMethod — the internal method selector (no HttpClient)
using System.Text.Json;
using System.Threading.Tasks;
using Best.HTTP;
using Khela.Common.Piggy;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Net;            // reuse ApiResult<T> (the transport result shape is game-agnostic)
using UnityEngine;

namespace PlayCard.Piggy.Net
{
    /// <summary>
    /// REST client for the piggy bank (<c>/api/piggy/*</c>). Same shape as <c>DailyRestClient</c> — Best.HTTP
    /// transport, System.Text.Json camelCase, the <see cref="AccountManager"/> JWT with a one-shot 401 refresh.
    ///
    /// There is nothing to decide here. How full the bank is, whether it may be bought, and how long is left are all
    /// server answers; this only carries them.
    /// </summary>
    public sealed class PiggyRestClient
    {
        private static PiggyRestClient _instance;
        public static PiggyRestClient Instance => _instance ??= new PiggyRestClient();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string Base => AppConfig.Instance.BaseApiUrl;

        /// <summary>The bank: amount, capacity, tier, whether it can be bought, and any countdown already running.
        /// A pure read — it never starts a countdown.</summary>
        public Task<ApiResult<PiggyStateDto>> GetStateAsync()
            => SendAsync<PiggyStateDto>(HttpMethod.Get, "/api/piggy");

        /// <summary>
        /// Tell the server the player is LOOKING at a full bank, which starts their countdown.
        ///
        /// Call it only when the ready state is actually on screen. The server keeps this out of the GET on purpose:
        /// a read happens for all sorts of reasons the player never witnesses, and a deadline started by one of those
        /// would quietly destroy a bank they were never shown. Safe to call again — only the first sighting counts.
        /// </summary>
        public Task<ApiResult<PiggyStateDto>> MarkSeenAsync()
            => SendAsync<PiggyStateDto>(HttpMethod.Post, "/api/piggy/seen");

        /// <summary>The chips have finished flying in — the next celebration should measure from here.</summary>
        public Task<ApiResult<PiggyStateDto>> MarkCelebratedAsync()
            => SendAsync<PiggyStateDto>(HttpMethod.Post, "/api/piggy/celebrated");

        /// <summary>
        /// Buy the bank. The server decides the payout, the price and whether it is allowed at all; this sends only
        /// WHICH offer was taken and the order that paid for it.
        ///
        /// <paramref name="purchaseId"/> is the idempotency key. Send the store's real order id in production - a
        /// fresh id per attempt only makes sense for testing, because a repeated id is exactly what protects a
        /// player from being charged twice and paid once.
        /// </summary>
        public Task<ApiResult<PiggyBreakResultDto>> BreakAsync(PiggyBreakOption option, string purchaseId)
            => SendAsync<PiggyBreakResultDto>(HttpMethod.Post, "/api/piggy/break",
                   JsonSerializer.Serialize(new BreakBody { Option = (int)option, PurchaseId = purchaseId }, JsonOpts));

        private sealed class BreakBody
        {
            public int Option { get; set; }
            public string PurchaseId { get; set; }
        }

        // ---------- core (mirrors DailyRestClient) ----------

        private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, string json = null)
        {
            var raw = await SendRawAsync(method, path, json);
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

        private async Task<Raw> SendRawAsync(HttpMethod method, string path, string json = null, bool isRetry = false)
        {
            try
            {
                var req = new HTTPRequest(new Uri(Base + path), ToBest(method));
                req.TimeoutSettings.Timeout = TimeSpan.FromSeconds(AppConfig.Instance.RequestTimeoutSeconds);

                if (json != null)
                {
                    req.SetHeader("Content-Type", "application/json");
                    req.UploadSettings.UploadStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                }

                var token = AccountManager.Instance != null ? AccountManager.Instance.JwtToken : null;
                if (!string.IsNullOrEmpty(token))
                    req.SetHeader("Authorization", "Bearer " + token);

                var resp = await req.GetHTTPResponseAsync();
                return new Raw(true, resp.StatusCode, resp.DataAsText, null);
            }
            catch (AsyncHTTPException hex)
            {
                if (hex.StatusCode == 401 && !isRetry && AccountManager.Instance != null)
                {
                    if (await AccountManager.Instance.HandleAuthFailureAsync())
                        return await SendRawAsync(method, path, json, isRetry: true);
                }
                return new Raw(false, hex.StatusCode, hex.Content, ExtractMessage(hex.Content) ?? hex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PiggyRestClient] {method} {path} failed: {ex.Message}");
                return new Raw(false, 0, null, ex.Message);
            }
        }

        private static HTTPMethods ToBest(HttpMethod m)
            => m == HttpMethod.Post ? HTTPMethods.Post : HTTPMethods.Get;

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
