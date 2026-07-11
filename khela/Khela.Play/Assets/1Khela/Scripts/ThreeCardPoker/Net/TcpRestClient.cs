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
using PlayCard.ThreeCardPoker.Dtos;
using UnityEngine;

namespace PlayCard.ThreeCardPoker.Net
{
    /// <summary>
    /// REST client for the server-authoritative Three Card Poker <b>action</b> channel (<c>/api/threecard/*</c>),
    /// plus the 3CP lobby query. Mirrors <c>BlackjackRestClient</c> exactly — same Best.HTTP transport, same
    /// System.Text.Json (camelCase) contract, same <see cref="AccountManager"/> JWT with a one-shot 401 refresh,
    /// same <see cref="ApiResult{T}"/> result. Every state-changing endpoint returns the current masked
    /// <see cref="TcpBoard"/> so the client renders immediately even if the SignalR push lags.
    /// </summary>
    public sealed class TcpRestClient
    {
        private static TcpRestClient _instance;
        public static TcpRestClient Instance => _instance ??= new TcpRestClient();

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string Base => AppConfig.Instance.BaseApiUrl;

        // ---------- lobby ----------

        /// <summary>Browsable 3CP table list (GET /api/lobby/threecard). Tops up the default house tables server-side.</summary>
        public Task<ApiResult<List<TcpTableSummary>>> GetLobbyAsync()
            => SendAsync<List<TcpTableSummary>>(HttpMethod.Get, "/api/lobby/threecard");

        // ---------- table lifecycle ----------

        public Task<ApiResult<TcpBoard>> CreateTableAsync(CreateTcpTableRequest req)
            => SendAsync<TcpBoard>(HttpMethod.Post, "/api/threecard/create", req);

        /// <summary>Server seats the player from their real wallet; prefer a SPECIFIC seat so the client knows which
        /// one it holds (the masked board carries no user-id).</summary>
        public Task<ApiResult<TcpBoard>> JoinAsync(string tableId, string name, string image = "", int? seatNumber = null)
            => SendAsync<TcpBoard>(HttpMethod.Post, $"/api/threecard/{tableId}/join",
                new JoinTcpTableRequest { Name = name, Image = image, SeatNumber = seatNumber });

        public Task<ApiResult<TcpBoard>> LeaveAsync(string tableId, int seatNumber)
            => SendAsync<TcpBoard>(HttpMethod.Post, $"/api/threecard/{tableId}/leave/{seatNumber}");

        // ---------- betting / actions ----------
        // Every action returns the authoritative masked board, so the client renders immediately even if the
        // SignalR push lags or the hub is mid-reconnect (the server also pushes TableUpdated; the view diffs).

        public Task<ApiResult<TcpBoard>> PlaceBetsAsync(string tableId, PlaceTcpBetsRequest bets)
            => SendAsync<TcpBoard>(HttpMethod.Post, $"/api/threecard/{tableId}/bet", bets);

        /// <summary>Deals the round (debit-on-bet); returns the fresh board (dealer cards masked until reveal).</summary>
        public Task<ApiResult<TcpBoard>> DealAsync(string tableId)
            => SendAsync<TcpBoard>(HttpMethod.Post, $"/api/threecard/{tableId}/deal");

        /// <summary>Post the Play bet (== Ante); the round reveals + settles once every seat has decided.</summary>
        public Task<ApiResult<TcpBoard>> PlayAsync(string tableId, int seatNumber)
            => SendAsync<TcpBoard>(HttpMethod.Post, $"/api/threecard/{tableId}/play/{seatNumber}");

        /// <summary>Fold — forfeits the Ante (side bets still settle); the round reveals once every seat has decided.</summary>
        public Task<ApiResult<TcpBoard>> FoldAsync(string tableId, int seatNumber)
            => SendAsync<TcpBoard>(HttpMethod.Post, $"/api/threecard/{tableId}/fold/{seatNumber}");

        /// <summary>Fetches the current board — used to resync if a SignalR push was missed.</summary>
        public Task<ApiResult<TcpBoard>> GetBoardAsync(string tableId)
            => SendAsync<TcpBoard>(HttpMethod.Get, $"/api/threecard/{tableId}/board");

        // ---------- core (mirrors BlackjackRestClient) ----------

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
                Debug.LogError($"[TcpRestClient] {method} {path} failed: {ex.Message}");
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
