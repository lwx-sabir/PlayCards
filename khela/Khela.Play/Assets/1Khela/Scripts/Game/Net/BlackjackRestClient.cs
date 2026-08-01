using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;              // HttpMethod — used only as the internal method selector (no HttpClient)
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using Best.HTTP;
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

        /// <summary>Raised after a REST call that may have changed the player's wallet (a claim, redeem, …) SUCCEEDS.
        /// The arg is the post-credit chip balance IF the response carried it (else null). <see cref="PlayCard.Game.Wallet.WalletManager"/>
        /// listens, applies the chip hint INSTANTLY, then reconciles all currencies — so every balance HUD updates without
        /// each caller remembering to refresh. Route balance-changing endpoints through <c>BalanceChangingAsync</c>.</summary>
        public static event Action<decimal?> BalanceMaybeChanged;

        // System.Text.Json (already vendored, used by SignalR + the server). Case-insensitive read
        // so the server's camelCase maps to our PascalCase DTOs; camelCase write to match the server.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static string Base => AppConfig.Instance.BaseApiUrl;

        // Wraps a balance-changing call: on success, fires BalanceMaybeChanged so WalletManager re-pulls and every
        // balance HUD refreshes. Route ANY endpoint that credits/debits the wallet (claims, redeems, purchases) through this.
        private async Task<ApiResult<T>> BalanceChangingAsync<T>(HttpMethod method, string path, object body = null)
        {
            var res = await SendAsync<T>(method, path, body);
            if (res.Ok)
            {
                // If the response carries the post-credit chip balance, pass it so WalletManager updates INSTANTLY;
                // the reconcile re-pull then fills the other currencies (e.g. Kash from a chest).
                decimal? chips = (res.Value as IChipBalanceResult)?.NewChipBalance;
                BalanceMaybeChanged?.Invoke(chips);
            }
            return res;
        }

        // ---------- Lobby / wallet (queries) ----------

        /// <summary>All currency balances for the signed-in user (the balance HUD).</summary>
        public Task<ApiResult<WalletBalances>> GetWalletAsync()
            => SendAsync<WalletBalances>(HttpMethod.Get, "/api/wallet/balances");

        /// <summary>The signed-in player's own full profile (GET /api/profile/me).</summary>
        public Task<ApiResult<UserProfileData>> GetMyProfileAsync()
            => SendAsync<UserProfileData>(HttpMethod.Get, "/api/profile/me");

        /// <summary>The caller's live level / into-level XP for the profile XP bar (GET /api/progression/me).</summary>
        public Task<ApiResult<ProgressionData>> GetProgressionAsync()
            => SendAsync<ProgressionData>(HttpMethod.Get, "/api/progression/me");

        /// <summary>The caller's live VIP status — tier, badge, trailing SP, benefit multiplier (GET /api/vip/me).</summary>
        public Task<ApiResult<VipStatusData>> GetMyVipStatusAsync()
            => SendAsync<VipStatusData>(HttpMethod.Get, "/api/vip/me");

        /// <summary>Toggle the "hide my VIP badge from others" opt-out (POST /api/vip/me/hide-badge).</summary>
        public Task<ApiResult<bool>> SetHideVipBadgeAsync(bool hidden)
            => SendOkAsync(HttpMethod.Post, $"/api/vip/me/hide-badge?hidden={(hidden ? "true" : "false")}");

        /// <summary>The Loyalty store — the caller's LP balance + the catalog (GET /api/loyalty).</summary>
        public Task<ApiResult<LoyaltyStoreData>> GetLoyaltyStoreAsync()
            => SendAsync<LoyaltyStoreData>(HttpMethod.Get, "/api/loyalty");

        /// <summary>Redeem a Loyalty-store item (POST /api/loyalty/redeem). idemKey = a stable id per buy tap.</summary>
        public Task<ApiResult<RedeemResultData>> RedeemLoyaltyAsync(string itemId, string idemKey)
            => BalanceChangingAsync<RedeemResultData>(HttpMethod.Post, "/api/loyalty/redeem",
                new RedeemRequestData { ItemId = itemId, IdempotencyKey = idemKey });

        /// <summary>Spend Loyalty Points to keep your current VIP level (POST /api/vip/maintain).</summary>
        public Task<ApiResult<VipMaintainResultData>> MaintainVipAsync()
            => SendAsync<VipMaintainResultData>(HttpMethod.Post, "/api/vip/maintain", new { });

        // ---- Daily missions ----
        /// <summary>The caller's daily missions + bundle state + reset time (GET /api/missions/daily).</summary>
        public Task<ApiResult<DailyMissionsData>> GetDailyMissionsAsync()
            => SendAsync<DailyMissionsData>(HttpMethod.Get, "/api/missions/daily");

        /// <summary>Claim a completed mission — reward credited straight to balance (POST /api/missions/{id}/claim).</summary>
        public Task<ApiResult<MissionClaimResultData>> ClaimMissionAsync(string missionInstanceId)
            => BalanceChangingAsync<MissionClaimResultData>(HttpMethod.Post, $"/api/missions/{missionInstanceId}/claim", new { });

        /// <summary>Claim the complete-all daily bundle (POST /api/missions/bundle/claim).</summary>
        public Task<ApiResult<MissionClaimResultData>> ClaimMissionBundleAsync()
            => BalanceChangingAsync<MissionClaimResultData>(HttpMethod.Post, "/api/missions/bundle/claim", new { });

        // ---- Reward inbox (level-up / passive rewards) ----
        /// <summary>The caller's pending claimable rewards (GET /api/rewards).</summary>
        public Task<ApiResult<List<RewardData>>> GetRewardsAsync()
            => SendAsync<List<RewardData>>(HttpMethod.Get, "/api/rewards");

        /// <summary>Collect one pending reward (POST /api/rewards/{id}/claim).</summary>
        public Task<ApiResult<RewardClaimResultData>> ClaimRewardAsync(string rewardId)
            => BalanceChangingAsync<RewardClaimResultData>(HttpMethod.Post, $"/api/rewards/{rewardId}/claim", new { });

        /// <summary>Collect all pending rewards (POST /api/rewards/claim-all).</summary>
        public Task<ApiResult<RewardClaimResultData>> ClaimAllRewardsAsync()
            => BalanceChangingAsync<RewardClaimResultData>(HttpMethod.Post, "/api/rewards/claim-all", new { });

        // ---- Gifts (free chips from friends) ----
        /// <summary>Claim all pending gifts → credits chips (idempotent). Routed through BalanceChangingAsync so every
        /// balance HUD refreshes after, identical to the mission/reward claim path. A gift-inbox UI must call THIS
        /// (not raw HTTP) so the wallet stays in sync.</summary>
        public Task<ApiResult<GiftClaimResult>> ClaimGiftsAsync()
            => BalanceChangingAsync<GiftClaimResult>(HttpMethod.Post, "/api/gifts/claim", new { });

        // ---- Leaderboards ----
        /// <summary>One leaderboard page + the caller's own rank. game: general/blackjack/poker/teenpatti/roulette ·
        /// metric: xp/biggestwin/streak · period: daily/weekly/monthly/alltime · scope: global/friends/country.</summary>
        public Task<ApiResult<LbPageData>> GetLeaderboardAsync(
            string game = "general", string metric = "xp", string period = "weekly", string scope = "global", int top = 50)
            => SendAsync<LbPageData>(HttpMethod.Get,
                $"/api/leaderboard?game={game}&metric={metric}&period={period}&scope={scope}&top={top}");

        // ---- Avatar (3D BoZo character; server-synced + sanitized) ----
        /// <summary>The caller's saved avatar (null if never set). Value is server-sanitized, so it's safe to render.</summary>
        public async Task<ApiResult<AvatarData>> GetMyAvatarAsync()
            => Unwrap(await SendAsync<AvatarEnvelope>(HttpMethod.Get, "/api/avatar/me"));

        /// <summary>Any player's avatar — used to render other seated players (null if they have none / are blocked).</summary>
        public async Task<ApiResult<AvatarData>> GetAvatarAsync(string userId)
            => Unwrap(await SendAsync<AvatarEnvelope>(HttpMethod.Get, $"/api/avatar/{userId}"));

        /// <summary>Save the caller's avatar. The server re-SANITIZES and echoes the stored result, which we return so the
        /// client re-syncs to exactly what persisted (never trust the local pre-clamp copy).</summary>
        public async Task<ApiResult<AvatarData>> PutMyAvatarAsync(AvatarData avatar)
            => Unwrap(await SendAsync<AvatarEnvelope>(HttpMethod.Put, "/api/avatar/me", avatar));

        private static ApiResult<AvatarData> Unwrap(ApiResult<AvatarEnvelope> r)
            => r.Ok ? ApiResult<AvatarData>.Success(r.Value?.Avatar, r.Status) : ApiResult<AvatarData>.Fail(r.Status, r.Error);

        /// <summary>Edit the caller's profile (PATCH /api/profile/me). Server moderates/validates — re-fetch after.</summary>
        public Task<ApiResult<bool>> UpdateProfileAsync(ProfileEditRequest edit)
            => SendOkAsync(new HttpMethod("PATCH"), "/api/profile/me", edit);

        // ---- Cosmetics shop ----
        /// <summary>The enabled cosmetics catalog + the caller's ownership flags (GET /api/shop/cosmetics).</summary>
        public Task<ApiResult<CosmeticCatalogEnvelope>> GetCosmeticsCatalogAsync()
            => SendAsync<CosmeticCatalogEnvelope>(HttpMethod.Get, "/api/shop/cosmetics");

        /// <summary>Buy a cosmetic (wallet-debited, idempotent). correlationId makes retries safe.</summary>
        public Task<ApiResult<CosmeticPurchaseResult>> BuyCosmeticAsync(string skuId, string correlationId)
            => BalanceChangingAsync<CosmeticPurchaseResult>(HttpMethod.Post, $"/api/shop/cosmetics/{skuId}/buy", new { correlationId });

        /// <summary>A table dealer's avatar look (GET /api/shop/cosmetics/dealer[/{id}]) — a SPECIFIC dealer by id, or
        /// the default (first) house dealer when id is null/empty. Rendered read-only at the table. Dealer is null in
        /// the value when that dealer doesn't exist.</summary>
        public Task<ApiResult<DealerEnvelope>> GetDealerAsync(string dealerId = null)
            => SendAsync<DealerEnvelope>(HttpMethod.Get,
                string.IsNullOrEmpty(dealerId) ? "/api/shop/cosmetics/dealer" : $"/api/shop/cosmetics/dealer/{dealerId}");

        /// <summary>Another player's PUBLIC profile (GET /api/profile/{userId}). 404/null if blocked or not found.</summary>
        public Task<ApiResult<PublicProfileData>> GetPublicProfileAsync(string userId)
            => SendAsync<PublicProfileData>(HttpMethod.Get, $"/api/profile/{userId}");

        /// <summary>The blackjack table browser, optionally filtered by mode.</summary>
        public Task<ApiResult<List<BlackjackTableSummary>>> GetLobbyAsync(BlackjackMode? mode = null)
            => SendAsync<List<BlackjackTableSummary>>(HttpMethod.Get,
                mode.HasValue ? $"/api/lobby/blackjack?mode={(int)mode.Value}" : "/api/lobby/blackjack");

        // ---------- Table lifecycle ----------

        public Task<ApiResult<CreatedTable>> CreateTableAsync(CreateBlackjackTableRequest req)
            => SendAsync<CreatedTable>(HttpMethod.Post, "/api/Blackjack/create", req);

        /// <summary>Server seats the player from their real wallet; the request balance is ignored.</summary>
        public Task<ApiResult<bool>> JoinAsync(string tableId, string name, string image = "", int? seatNumber = null)
            => SendOkAsync(HttpMethod.Post, $"/api/Blackjack/{tableId}/join",
                new JoinTableRequest { Name = name, Image = image, Balance = 0, SeatNumber = seatNumber });

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

        /// <summary>Decline insurance (the NO button) — marks you decided so the window can close early.</summary>
        public Task<ApiResult<BoardSnapshot>> DeclineInsuranceAsync(string tableId, int seatNumber)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/insurance/decline/{seatNumber}");

        /// <summary>
        /// PRESENTATION HANDSHAKE — call the moment the deal (or a drawn card) has finished animating and the player can
        /// actually act. The server stamps turn deadlines generously (max-presentation + turn) so a slow deal can never
        /// cut us off mid-animation; this collapses that to the REAL turn length from now, so the player always gets the
        /// full configured decision time regardless of device speed, table size or clip length. Cheat-safe server-side
        /// (can only shorten, never extend) and idempotent per turn, so a repeated call is harmless.
        /// </summary>
        public Task<ApiResult<BoardSnapshot>> PresentedAsync(string tableId, int seatNumber)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/presented/{seatNumber}");

        /// <summary>Runs the dealer and settles; returns the final board (with the revealed hole card and LastHandId).</summary>
        public Task<ApiResult<BoardSnapshot>> DealerPlayAsync(string tableId)
            => SendAsync<BoardSnapshot>(HttpMethod.Post, $"/api/Blackjack/{tableId}/dealerPlay");

        /// <summary>Fetches the current board — used to resync if the SignalR push was missed.</summary>
        public Task<ApiResult<BoardSnapshot>> GetBoardAsync(string tableId)
            => SendAsync<BoardSnapshot>(HttpMethod.Get, $"/api/Blackjack/{tableId}/board");

        /// <summary>Seated keep-alive — tells the server we're still here so the reaper doesn't remove us.</summary>
        public Task<ApiResult<bool>> HeartbeatAsync(string tableId)
            => SendOkAsync(HttpMethod.Post, $"/api/Blackjack/{tableId}/heartbeat");

        /// <summary>
        /// This player's settled hands at this table — the session hand log / report. Read from the server's per-hand
        /// AUDIT rows, so it survives a reconnect or scene reload and can never drift from what the wallet actually
        /// paid. <paramref name="sinceUtc"/> scopes it to the current sitting; null = everything, capped by
        /// <paramref name="take"/>. Newest first; a split returns one entry per hand.
        /// </summary>
        public Task<ApiResult<HandLogData>> GetHandLogAsync(string tableId, DateTimeOffset? sinceUtc = null, int take = 100)
        {
            // Round-trip the cutoff as UTC ISO-8601 ("o"), so the server's DateTime parse can't pick up the device's
            // local offset and silently shift the session window by hours.
            string q = $"?take={take}";
            if (sinceUtc.HasValue)
                q += "&sinceUtc=" + Uri.EscapeDataString(sinceUtc.Value.UtcDateTime.ToString("o"));
            return SendAsync<HandLogData>(HttpMethod.Get, $"/api/Blackjack/{tableId}/history{q}");
        }

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
                var req = new HTTPRequest(new Uri(Base + path), ToBest(method));
                req.TimeoutSettings.Timeout = TimeSpan.FromSeconds(AppConfig.Instance.RequestTimeoutSeconds);

                // Refresh BEFORE sending if the cached token is expired or about to be, instead of firing a request we
                // know will 401 and recovering afterwards. No network cost when the token is healthy. Skipped on the
                // replay so a genuinely rejected token can't loop.
                if (!isRetry && AccountManager.Instance != null)
                    await AccountManager.Instance.EnsureValidTokenAsync();

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

                // 401 on the RESPONSE path. Best HTTP returns 4xx as a normal response, not an exception — so the
                // refresh-and-replay below in `catch (AsyncHTTPException)` never ran for the ordinary case and the
                // "refresh once and retry" this class documents simply didn't happen. That is why an expired token
                // surfaced to the user as a bare "HTTP 401" (e.g. the lobby failing to load after leaving a table)
                // instead of quietly re-authenticating.
                if (resp.StatusCode == 401 && AccountManager.Instance != null)
                {
                    if (!isRetry && await AccountManager.Instance.HandleAuthFailureAsync())
                        return await SendRawAsync(method, path, body, isRetry: true);

                    // Still unauthorised after a refresh, a re-login AND a replay. Recovery is exhausted, so restart
                    // the app from Boot rather than leaving the player on a screen that can only show "HTTP 401".
                    AccountManager.Instance.AbandonSession();
                }

                // Do NOT assume non-2xx throws — Best HTTP returns 4xx/5xx as a normal response. Treat any non-2xx as
                // failure so a rejected save (e.g. the entitlement gate's 400) can't be reported to callers as success.
                if (resp.StatusCode < 200 || resp.StatusCode >= 300)
                    return new Raw(false, resp.StatusCode, resp.DataAsText, ExtractMessage(resp.DataAsText) ?? $"HTTP {resp.StatusCode}");
                return new Raw(true, resp.StatusCode, resp.DataAsText, null);
            }
            catch (AsyncHTTPException hex)
            {
                // Token expired mid-session: refresh once and replay the exact same call.
                if (hex.StatusCode == 401 && AccountManager.Instance != null)
                {
                    if (!isRetry && await AccountManager.Instance.HandleAuthFailureAsync())
                        return await SendRawAsync(method, path, body, isRetry: true);

                    AccountManager.Instance.AbandonSession();   // recovery exhausted — see the response path above
                }
                return new Raw(false, hex.StatusCode, hex.Content, ExtractMessage(hex.Content) ?? hex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlackjackRestClient] {method} {path} failed: {ex.Message}");
                return new Raw(false, 0, null, ex.Message);
            }
        }

        // Map the System.Net.Http.HttpMethod selector (used by the public methods) to Best HTTP's enum.
        private static HTTPMethods ToBest(HttpMethod m)
        {
            if (m == HttpMethod.Get) return HTTPMethods.Get;
            if (m == HttpMethod.Post) return HTTPMethods.Post;
            if (m == HttpMethod.Put) return HTTPMethods.Put;
            if (m == HttpMethod.Delete) return HTTPMethods.Delete;
            if (string.Equals(m.Method, "PATCH", StringComparison.OrdinalIgnoreCase)) return HTTPMethods.Patch;
            return HTTPMethods.Get;
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
        public decimal Kash { get; set; }
    }

    /// <summary>Result of POST /api/gifts/claim — how many pending gifts were collected.</summary>
    public sealed class GiftClaimResult
    {
        public int Claimed { get; set; }
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
