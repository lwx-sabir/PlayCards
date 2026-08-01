using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Khela.Game.Managers;
using Khela.Game.Database;
using Khela.Game.Database.Models;   // GameHandHeader/Participant + HandStatus (session hand log)
using Microsoft.EntityFrameworkCore;
using CardGames.Blackjack;
using CardGames.Platforms;
using CardGames.Provable;
using Khela.Common.Blackjack;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System;
using System.Linq;

namespace Khela.Game.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class BlackjackController : ControllerBase
    {
        private readonly BlackjackTableManager tableManager;
        private readonly AppDbContext db;

        public BlackjackController(BlackjackTableManager tableManager, AppDbContext db)
        {
            this.tableManager = tableManager;
            this.db = db;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateTable([FromBody] CreateBlackjackTableRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var table = await tableManager.CreateTableAsync(request.MaxPlayers, request.MaxSeatsPerUser,
                request.Mode, request.MinBet, request.MaxBet);
            return Ok(new { table.TableId, table.MaxPlayers, table.MaxSeatsPerUser, request.Mode, request.MinBet, request.MaxBet });
        }

        // ----------------------------------------------------------------------------------------------
        // Every state-changing endpoint returns the SAME masked projection — BlackjackBoard.Build(table) —
        // so the client has one board contract (BoardSnapshot) regardless of which action it sent, and can
        // render immediately even if the SignalR push lags. The dealer hole card stays masked until reveal.
        // ----------------------------------------------------------------------------------------------

        [HttpPost("{tableId}/join")]
        public async Task<IActionResult> JoinTable(string tableId, [FromBody] JoinTableRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                // Seat from the AUTHORITATIVE wallet — request.Balance is ignored by AddPlayerAsync.
                // request.SeatNumber (nullable) lets the client pick a seat; null = auto-assign first open.
                var table = await tableManager.AddPlayerAsync(
                    tableId,
                    new Player(userId, request.Balance, request.Name, request.Image),
                    request.SeatNumber);

                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/leave/{seatNumber:int}")]
        public async Task<IActionResult> LeaveTable(string tableId, int seatNumber)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");
            try
            {
                var table = await tableManager.RemovePlayerAsync(tableId, seatNumber, userId);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/bet")]
        public async Task<IActionResult> PlaceBet(string tableId, [FromBody] PlaceBetRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.PlaceBetAsync(tableId, userId, request.SeatNumber, request.Amount, request.HandIndex);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/deal")]
        public async Task<IActionResult> Deal(string tableId)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.DealAsync(tableId, userId);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Seated keep-alive (~every 5s) so the reaper doesn't flag the player stalled. REST fallback for
        /// the polling transport — same effect as the hub's Heartbeat. No-op if the caller isn't seated here.</summary>
        [HttpPost("{tableId}/heartbeat")]
        public async Task<IActionResult> Heartbeat(string tableId)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.RecordHeartbeatAsync(tableId, userId);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Play a transient EMOTE at the table — broadcast to everyone seated (rate-limited; no board change).</summary>
        [HttpPost("{tableId}/emote")]
        public async Task<IActionResult> Emote(string tableId, [FromBody] EmoteRequest request)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");
            try
            {
                var ok = await tableManager.SendEmoteAsync(tableId, userId, request?.EmoteId);
                return ok ? Ok() : BadRequest(new { message = "Emote not sent (unknown id, not seated, or rate-limited)." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/hit/{seatNumber:int}")]
        public async Task<IActionResult> Hit(string tableId, int seatNumber, [FromQuery] int handIndex = 0)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var (table, result) = await tableManager.HitAsync(tableId, userId, seatNumber, handIndex);
                if (table == null || result == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/double/{seatNumber:int}")]
        public async Task<IActionResult> DoubleDown(string tableId, int seatNumber, [FromQuery] int handIndex = 0)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var (table, result) = await tableManager.DoubleDownAsync(tableId, userId, seatNumber, handIndex);
                if (table == null || result == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/insurance")]
        public async Task<IActionResult> Insurance(string tableId, [FromBody] InsuranceRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.PlaceInsuranceAsync(tableId, userId, request.SeatNumber, request.Amount, request.HandIndex);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>Decline insurance during the insurance phase (the NO button). No money moves; it just
        /// marks the player decided so the window can close early once everyone has decided.</summary>
        [HttpPost("{tableId}/insurance/decline/{seatNumber:int}")]
        public async Task<IActionResult> DeclineInsurance(string tableId, int seatNumber)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.DeclineInsuranceAsync(tableId, userId, seatNumber);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/split/{seatNumber:int}")]
        public async Task<IActionResult> Split(string tableId, int seatNumber, [FromQuery] int handIndex = 0)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.SplitAsync(tableId, userId, seatNumber, handIndex);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/stand/{seatNumber:int}")]
        public async Task<IActionResult> Stand(string tableId, int seatNumber, [FromQuery] int handIndex = 0)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.StandAsync(tableId, userId, seatNumber, handIndex);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Presentation handshake: the current player's client calls this once it has finished animating the deal (or a
        /// drawn card) and can actually act. Collapses the generously-stamped turn deadline to the REAL turn length from
        /// now, so the decision clock is the full configured turn on any device / table size. Cheat-safe (can only
        /// shorten, never extend) and idempotent per turn, so a stray or repeated call is harmless.
        /// </summary>
        [HttpPost("{tableId}/presented/{seatNumber:int}")]
        public async Task<IActionResult> Presented(string tableId, int seatNumber)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.PresentedAsync(tableId, userId, seatNumber);
                if (table == null) return NotFound("Table not found or expired.");
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/dealerPlay")]
        public async Task<IActionResult> DealerPlay(string tableId)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");
            try
            {
                var table = await tableManager.DealerPlayAndSettleAsync(tableId, userId);
                if (table == null) return NotFound("Table not found or expired.");

                // Round settled (dealer revealed); board includes LastHandId for one-click verify.
                return Ok(BlackjackBoard.Build(table));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("{tableId}/board")]
        public async Task<IActionResult> GetBoard(string tableId)
        {
            var table = await tableManager.GetTableAsync(tableId);
            if (table == null) return NotFound("Table not found or expired.");

            return Ok(BlackjackBoard.Build(table));
        }

        /// <summary>
        /// Provably-fair verification for a settled hand: recompute the shoe from the recorded
        /// per-round seed and confirm it hashes to the recorded deck. Public so anyone can verify.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("verify/{handId}")]
        public async Task<IActionResult> Verify(Guid handId)
        {
            var header = await db.GameHandHeaders.FindAsync(handId);
            if (header == null) return NotFound("Hand not found.");

            var shoe = new Deck(6);
            shoe.Shuffle(Convert.FromHexString(header.ShuffleSeed));
            var recomputed = shoe.ComputeHash();

            return Ok(new
            {
                header.HandId,
                header.TableId,
                header.RoundId,
                header.HandNumber,
                ShoeCommitment = header.ShoeId,
                header.ShuffleSeed,
                RecordedDeckHash = header.DeckHash,
                RecomputedDeckHash = recomputed,
                Verified = string.Equals(recomputed, header.DeckHash, StringComparison.OrdinalIgnoreCase),
                header.ResultChecksum,
                DeckOrder = shoe.Cards.Select(ProvableShuffle.Canonical)
            });
        }

        /// <summary>
        /// THIS player's settled hands at THIS table — the session hand log / report. Reads the authoritative
        /// per-hand audit rows (GameHandParticipants joined to GameHandHeaders), so it is the same data the ledger
        /// was built from and it SURVIVES a reconnect or a scene reload — unlike anything the client accumulates
        /// in memory. A split contributes one row per hand (HandIndex 0, 1, …), exactly as it settled.
        ///
        /// <paramref name="sinceUtc"/> scopes it to the current sitting (the client stamps when it sat down);
        /// omit for "everything this table has for me", capped by <paramref name="take"/>. Newest first.
        /// </summary>
        [HttpGet("{tableId}/history")]
        public async Task<IActionResult> History(string tableId, [FromQuery] DateTime? sinceUtc = null,
                                                 [FromQuery] int take = 100)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");
            if (!Guid.TryParse(userId, out var uid)) return Unauthorized("Bad user id.");
            if (take <= 0 || take > 500) take = 100;   // clamp: this is a UI list, not a bulk export

            // Normalise the cutoff to a UTC INSTANT. SettledAt is stored UTC, but a DATETIME column round-trips with
            // no kind and the query-string binder can hand us Unspecified (or Local) — comparing that raw would shift
            // the sitting window by the server's offset and show the wrong hands (or none).
            if (sinceUtc.HasValue)
                sinceUtc = sinceUtc.Value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc)
                    : sinceUtc.Value.ToUniversalTime();

            // Own rows only — a player can never read another seat's stakes or payouts through this.
            var q = from p in db.GameHandParticipants
                    join h in db.GameHandHeaders on p.HandId equals h.HandId
                    where p.UserId == uid
                          && h.TableId == tableId
                          && h.Status == HandStatus.Settled
                          && (sinceUtc == null || h.SettledAt >= sinceUtc)
                    orderby h.SettledAt descending, h.HandNumber descending, p.HandIndex
                    select new
                    {
                        p.HandId,                 // feeds the one-click provably-fair verify link
                        h.HandNumber,
                        h.SettledAt,
                        p.SeatNumber,
                        p.HandIndex,              // a split shows one entry per hand
                        p.Bet,
                        p.InsuranceBet,
                        p.Payout,
                        // Net for this hand: what came back minus everything staked on it. Same definition the
                        // board's per-hand Delta uses, so the log and the felt can never disagree.
                        Delta = p.Payout - (p.Bet + p.InsuranceBet),
                        p.FinalHandValue,
                        p.Bust,
                        p.Blackjack,
                        p.Outcome
                    };

            var rows = await q.Take(take).ToListAsync();

            // Session totals for the report header. Computed over the RETURNED rows so the numbers always match
            // the list the player is looking at (a truncated list never shows a total it doesn't itemise).
            return Ok(new
            {
                TableId = tableId,
                SinceUtc = sinceUtc,
                Count = rows.Count,
                Truncated = rows.Count >= take,
                Wagered = rows.Sum(r => r.Bet + r.InsuranceBet),
                Returned = rows.Sum(r => r.Payout),
                Net = rows.Sum(r => r.Delta),
                Wins = rows.Count(r => r.Outcome == "win" || r.Outcome == "blackjack"),
                Losses = rows.Count(r => r.Outcome == "lose" || r.Outcome == "bust"),
                Pushes = rows.Count(r => r.Outcome == "push"),
                Hands = rows
            });
        }

        private string? GetUserId()
        {
            return User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        }
    }
}
