using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;              // HttpMethod — the internal method selector (no HttpClient)
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Best.HTTP;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Net;            // reuse ApiResult<T> (transport result shape is game-agnostic)
using PlayCard.VideoPoker.Dtos;
using UnityEngine;

namespace PlayCard.VideoPoker.Net
{
    /// <summary>
    /// REST client for the server-authoritative Video Poker channel (<c>/api/videopoker/*</c>). Mirrors
    /// <c>TcpRestClient</c>/<c>BlackjackRestClient</c> exactly — same Best.HTTP transport, same System.Text.Json
    /// (camelCase) contract, same <see cref="AccountManager"/> JWT with a one-shot 401 refresh, same
    /// <see cref="ApiResult{T}"/> result. Video poker is single-player + REST-only, so there is NO hub/polling/
    /// heartbeat: <c>/deal</c> and <c>/draw</c> each return the full <see cref="VpBoard"/> and that is the whole loop.
    /// </summary>
    public sealed class VideoPokerRestClient
    {
        private static VideoPokerRestClient _instance;
        public static VideoPokerRestClient Instance => _instance ??= new VideoPokerRestClient();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string Base => AppConfig.Instance.BaseApiUrl;

        // ---------- menu ----------

        /// <summary>The offered variants + their paytables (GET /api/videopoker/variants). Anonymous — no auth needed.</summary>
        public Task<ApiResult<List<VpVariantSummary>>> GetVariantsAsync()
            => SendAsync<List<VpVariantSummary>>(HttpMethod.Get, "/api/videopoker/variants");

        // ---------- play loop ----------

        /// <summary>Start a hand (debit-on-bet). Returns the dealt board (phase "dealt", server seed still hidden).</summary>
        public Task<ApiResult<VpBoard>> DealAsync(DealVpRequest req)
            => SendAsync<VpBoard>(HttpMethod.Post, "/api/videopoker/deal", req);

        /// <summary>Complete the hand with the hold mask (credit-on-settle). Returns the final board (phase "complete").
        /// Idempotent on the server — a duplicate draw returns the same result without re-crediting.</summary>
        public Task<ApiResult<VpBoard>> DrawAsync(DrawVpRequest req)
            => SendAsync<VpBoard>(HttpMethod.Post, "/api/videopoker/draw", req);

        /// <summary>Re-fetch a hand (reconnect / resync).</summary>
        public Task<ApiResult<VpBoard>> GetHandAsync(string handId)
            => SendAsync<VpBoard>(HttpMethod.Get, $"/api/videopoker/hand/{handId}");

        /// <summary>Provably-fair proof for a settled hand (raw JSON, for a "provably fair" info panel / offline replay).
        /// Public endpoint. Returned as text so the client needn't model the full verification shape to display it.</summary>
        public Task<ApiResult<string>> GetVerifyJsonAsync(string handId)
            => SendTextAsync(HttpMethod.Get, $"/api/videopoker/verify/{handId}");

        // ---------- core (mirrors TcpRestClient) ----------

        private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object body = null)
        {
            var raw = await SendRawAsync(method, path, body);
            if (!raw.Ok) return ApiResult<T>.Fail(raw.Status, raw.Error);
            try
            {
                var value = JsonSerializer.Deserialize<T>(raw.Body, JsonOpts);
                return ApiResult<T>.Success(value, raw.Status);
            }
            catch (Exception ex)
            {
                return ApiResult<T>.Fail(raw.Status, $"Parse error: {ex.Message}");
            }
        }

        private async Task<ApiResult<string>> SendTextAsync(HttpMethod method, string path, object body = null)
        {
            var raw = await SendRawAsync(method, path, body);
            return raw.Ok ? ApiResult<string>.Success(raw.Body, raw.Status) : ApiResult<string>.Fail(raw.Status, raw.Error);
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
                Debug.LogError($"[VideoPokerRestClient] {method} {path} failed: {ex.Message}");
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

        // Server errors come back as { "message": "..." }.
        private static string ExtractMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try { return JsonSerializer.Deserialize<ErrorBody>(body, JsonOpts)?.Message; }
            catch { return null; }
        }

        private sealed class ErrorBody { public string Message { get; set; } }
    }
}
