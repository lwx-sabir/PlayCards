using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Khela.Game.Managers;
using Khela.Game.Database;
using CardGames.Blackjack.CardGames.Blackjack;
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

        [HttpPost("{tableId}/join")]
        public async Task<IActionResult> JoinTable(string tableId, [FromBody] JoinTableRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized("Missing user id.");

            try
            {
                var table = await tableManager.AddPlayerAsync(
                    tableId,
                    new Player(userId, request.Balance, request.Name, request.Image));

                if (table == null) return NotFound("Table not found or expired.");

                return Ok(new
                {
                    table.TableId,
                    table.MaxPlayers,
                    table.MaxSeatsPerUser,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

                return Ok(new
                {
                    table.TableId,
                    table.MaxPlayers,
                    table.MaxSeatsPerUser,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

                return Ok(new
                {
                    table.TableId,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("{tableId}/deal")]
        public async Task<IActionResult> Deal(string tableId)
        {
            try
            {
                var table = await tableManager.DealAsync(tableId);
                if (table == null) return NotFound("Table not found or expired.");

                // Unified masked board — the dealer hole card stays hidden until dealer reveal.
                return Ok(BlackjackBoard.Build(table));
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

                var player = table.Game.Players.First(p => p.SeatNumber == seatNumber && p.Id == userId);

                return Ok(new
                {
                    Player = player.Name,
                    HandValue = player.GetHand(handIndex).Hand.GetSumOfHand(),
                    Hit = result
                });
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

                var player = table.Game.Players.First(p => p.SeatNumber == seatNumber && p.Id == userId);

                return Ok(new
                {
                    Player = player.Name,
                    HandValue = player.GetHand(handIndex).Hand.GetSumOfHand(),
                    Double = result,
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

                return Ok(new
                {
                    table.TableId,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

                return Ok(new
                {
                    table.TableId,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

                return Ok(new
                {
                    table.TableId,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

                // Unified board — round settled (dealer revealed); includes LastHandId for one-click verify.
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

        // Face-down cards (the dealer hole card) are masked so a board snapshot never leaks the
        // down card; the real value appears once it's flipped face-up at dealer play.
        private static object MaskCard(Card c) => c.IsCardUp
            ? new { FaceVal = (int)c.FaceVal, Suit = (int)c.Suit, c.IsCardUp }
            : new { FaceVal = 0, Suit = 0, c.IsCardUp };

        private static object ToPlayerDto(Player p) => new
        {
            p.Id,
            p.Name,
            p.Balance,
            p.SeatNumber,
            Hands = p.Hands.Select((h, idx) => new
            {
                HandIndex = idx,
                h.Bet,
                Insurance = h.InsuranceBet,
                Cards = h.Hand.Cards.Select(c => new { c.FaceVal, c.Suit, c.IsCardUp }),
                HandValue = h.Hand.GetSumOfHand()
            }),
            p.Wins,
            p.Losses,
            p.Push
        };

        private static object ToSeatDto(Seat s) => new
        {
            s.SeatNumber,
            Occupied = s.Player != null,
            Player = s.Player == null ? null : ToPlayerDto(s.Player)
        };

        private string? GetUserId()
        {
            return User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        }
    }
}
