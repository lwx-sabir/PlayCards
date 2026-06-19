using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using Khela.Common.Blackjack;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Outcome of a REST call. <see cref="Ok"/> distinguishes success from a transport error
    /// (Status 0) or a server error (Status &gt;= 400, with <see cref="Error"/> carrying the
    /// server's message when available).
    /// </summary>
    public readonly struct ApiResult<T>
    {
        public readonly bool Ok;
        public readonly int Status;
        public readonly string Error;
        public readonly T Value;

        private ApiResult(bool ok, int status, string error, T value)
        {
            Ok = ok;
            Status = status;
            Error = error;
            Value = value;
        }

        public static ApiResult<T> Success(T value, int status) => new ApiResult<T>(true, status, null, value);
        public static ApiResult<T> Fail(int status, string error) => new ApiResult<T>(false, status, error, default);
    }

    /// <summary>
    /// REST client for the server-authoritative <b>action</b> channel (bet/hit/stand/deal/…),
    /// plus the lobby and wallet queries. Live board state is pushed separately over SignalR
    /// (<c>TableUpdated</c>); the deal/dealerPlay/board calls here also return the current
    /// <see cref="BoardSnapshot"/> for immediate feedback and as a hub-down fallback.
    ///
    /// Auth uses <see cref="AccountManager"/>'s cached JWT; on a 401 the token is refreshed once
    /// and the request retried. Reuses the shared <c>Khela.Common.Blackjack</c> request DTOs so
    /// the wire contract can't drift from the server.
    ///
    /// Uses <see cref="HttpClient"/> (matches <see cref="AccountManager"/>); like the SignalR
    /// transport this is fine on Android/iOS (IL2CPP) but not WebGL — WebGL rides the same future
    /// transport swap.
    /// </summary>
    public sealed class BlackjackRestClient
    {
        private static BlackjackRestClient _instance;
        public static BlackjackRestClient Instance => _instance ??= new BlackjackRestClient();

        // System.Text.Json (already vendored, used by SignalR + the server). Case-insensitive read
        // so the server's camelCase maps to our PascalCase DTOs; camelCase write to match the server.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HttpClient _http;

        public BlackjackRestClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConfig.Instance.RequestTimeoutSeconds) };
        }

        private static string Base => AppConfig.Instance.BaseApiUrl;

        // ---------- Lobby / wallet (queries) ----------

        /// <summary>All currency balances for the signed-in user (the balance HUD).</summary>
        public Task<ApiResult<WalletBalances>> GetWalletAsync()
            => SendAsync<WalletBalances>(HttpMethod.Get, "/api/wallet/balances");

        /// <summary>The blackjack table browser, optionally filtered by mode.</summary>
        public Task<ApiResult<List<BlackjackTableSummary>>> GetLobbyAsync(BlackjackMode? mode = null)
            => SendAsync<List<BlackjackTableSummary>>(HttpMethod.Get,
                mode.HasValue ? $"/api/lobby/blackjack?mode={(int)mode.Value}" : "/api/lobby/blackjack");

        // ---------- Table lifecycle ----------

        public Task<ApiResult<CreatedTable>> CreateTableAsync(CreateBlackjackTableRequest req)
            => SendAsync<CreatedTable>(HttpMethod.Post, "/api/Blackjack/create", req);

        /// <summary>Server seats the player from their real wallet; the request balance is ignored.</summary>
        public Task<ApiResult<bool>> JoinAsync(string tableId, string name, string image = "")
            => SendOkAsync(HttpMethod.Post, $"/api/Blackjack/{tableId}/join",
                new JoinTableRequest { Name = name, Image = image, Balance = 0 });

        public Task<ApiResult<bool>> LeaveAsync(string tableId, int seatNumber)
            => SendOkAsync(HttpMethod.Post, $"/api/Blackjack/{tableId}/leave/{seatNumber}");

        // ---------- Betting / actions ----------
        // Every action returns the authoritative masked board, so the client renders immediately even if the
        // SignalR push lags or the hub is mid-reconnect (the server also pushes TableUpdated; the view diffs).

        public Task<ApiResult<BoardSnapshot>> BetAsync(string tableId, decimal amount, int seatNumber, int handIndex = 0)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/bet",
                new PlaceBetRequest { Amount = amount, SeatNumber = seatNumber, HandIndex = handIndex });

        /// <summary>Deals the round; returns the fresh board (dealer hole card masked).</summary>
        public Task<ApiResult<BoardSnapshot>> DealAsync(string tableId)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/deal");

        public Task<ApiResult<BoardSnapshot>> HitAsync(string tableId, int seatNumber, int handIndex = 0)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/hit/{seatNumber}?handIndex={handIndex}");

        public Task<ApiResult<BoardSnapshot>> StandAsync(string tableId, int seatNumber, int handIndex = 0)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/stand/{seatNumber}?handIndex={handIndex}");

        public Task<ApiResult<BoardSnapshot>> DoubleAsync(string tableId, int seatNumber, int handIndex = 0)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/double/{seatNumber}?handIndex={handIndex}");

        public Task<ApiResult<BoardSnapshot>> SplitAsync(string tableId, int seatNumber, int handIndex = 0)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/split/{seatNumber}?handIndex={handIndex}");

        public Task<ApiResult<BoardSnapshot>> InsuranceAsync(string tableId, int seatNumber, decimal amount, int handIndex = 0)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/insurance",
                new InsuranceRequest { SeatNumber = seatNumber, Amount = amount, HandIndex = handIndex });

        /// <summary>Runs the dealer and settles; returns the final board (with the revealed hole card and LastHandId).</summary>
        public Task<ApiResult<BoardSnapshot>> DealerPlayAsync(string tableId)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/dealerPlay");

        /// <summary>Fetches the current board — used to resync if the SignalR push was missed.</summary>
        public Task<ApiResult<BoardSnapshot>> GetBoardAsync(string tableId)
            => SendAsync<BoardSnapshot>(HttpMethod.Get, $"/api/Blackjack/{tableId}/board");

        // ---------- core ----------

        private async Task<ApiResult<bool>> SendOkAsync(HttpMethod method, string path, object body = null)
        {
            var raw = await SendRawAsync(method, path, body);
            return raw.Ok ? ApiResult<bool>.Success(true, raw.Status) : ApiResult<bool>.Fail(raw.Status, raw.Error);
        }

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

            public Raw(bool ok, int status, string body, string error)
            {
                Ok = ok;
                Status = status;
                Body = body;
                Error = error;
            }
        }

        private async Task<Raw> SendRawAsync(HttpMethod method, string path, object body, bool isRetry = false)
        {
            try
            {
                using var req = new HttpRequestMessage(method, Base + path);

                var token = AccountManager.Instance != null ? AccountManager.Instance.JwtToken : null;
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                if (body != null)
                {
                    var json = JsonSerializer.Serialize(body, JsonOpts);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using var resp = await _http.SendAsync(req);
                var text = await resp.Content.ReadAsStringAsync();

                // Token expired mid-session: refresh once and replay the exact same call.
                if (resp.StatusCode == HttpStatusCode.Unauthorized && !isRetry && AccountManager.Instance != null)
                {
                    if (await AccountManager.Instance.HandleAuthFailureAsync())
                        return await SendRawAsync(method, path, body, isRetry: true);
                }

                if (!resp.IsSuccessStatusCode)
                    return new Raw(false, (int)resp.StatusCode, text, ExtractMessage(text) ?? resp.ReasonPhrase);

                return new Raw(true, (int)resp.StatusCode, text, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlackjackRestClient] {method} {path} failed: {ex.Message}");
                return new Raw(false, 0, null, ex.Message);
            }
        }

        // Server errors come back as { "message": "..." }.
        private static string ExtractMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try { return JsonSerializer.Deserialize<ErrorBody>(body, JsonOpts)?.Message; }
            catch { return null; }
        }

        private sealed class ErrorBody
        {
            public string Message { get; set; }
        }
    }

    /// <summary>Client mirror of the wallet <c>/balances</c> response (server returns an anonymous object).</summary>
    public sealed class WalletBalances
    {
        public decimal Chips { get; set; }
        public decimal Coins { get; set; }
        public decimal Gems { get; set; }
        public decimal Tokens { get; set; }
    }

    /// <summary>Client mirror of the <c>/Blackjack/create</c> response.</summary>
    public sealed class CreatedTable
    {
        public string TableId { get; set; }
        public int MaxPlayers { get; set; }
        public int MaxSeatsPerUser { get; set; }
        public BlackjackMode Mode { get; set; }
        public decimal MinBet { get; set; }
        public decimal MaxBet { get; set; }
    }
}
