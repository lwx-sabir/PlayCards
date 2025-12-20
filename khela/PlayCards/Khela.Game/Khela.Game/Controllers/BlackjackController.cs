using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Khela.Game.Managers;
using CardGames.Blackjack.CardGames.Blackjack;
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

        public BlackjackController(BlackjackTableManager tableManager)
        {
            this.tableManager = tableManager;
        }
         
        [HttpPost("create")]
        public async Task<IActionResult> CreateTable([FromBody] CreateBlackjackTableRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var table = await tableManager.CreateTableAsync(request.MaxPlayers, request.MaxSeatsPerUser);
            return Ok(new { table.TableId, table.MaxPlayers, table.MaxSeatsPerUser });
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

                return Ok(new
                {
                    table.TableId,
                    Round = "started",
                    table.RoundInProgress,
                    Dealer = new
                    {
                        Cards = table.Game.Dealer.Hand.Cards.Select(c => new { c.FaceVal, c.Suit, c.IsCardUp }),
                        HandValue = table.Game.Dealer.Hand.GetSumOfHand()
                    },
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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
            try
            {
                var table = await tableManager.DealerPlayAndSettleAsync(tableId);
                if (table == null) return NotFound("Table not found or expired.");

                return Ok(new
                {
                    DealerCards = table.Game.Dealer.Hand.Cards.Select(c => new { c.FaceVal, c.Suit, c.IsCardUp }),
                    HandValue = table.Game.Dealer.Hand.GetSumOfHand(),
                    RoundSettled = true,
                    Players = table.Game.Players.Select(ToPlayerDto),
                    Seats = table.Seats.Select(ToSeatDto)
                });
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

            var board = new
            {
                table.TableId,
                table.MaxPlayers,
                table.MaxSeatsPerUser,
                table.RoundInProgress,
                DeckRemaining = table.Game.Deck.GetRemainingDeck(),
                Dealer = new
                {
                    Cards = table.Game.Dealer.Hand.Cards.Select(c => new { c.FaceVal, c.Suit, c.IsCardUp }),
                    HandValue = table.Game.Dealer.Hand.GetSumOfHand()
                },
                Players = table.Game.Players.Select(ToPlayerDto),
                Seats = table.Seats.Select(ToSeatDto)
            };

            return Ok(board);
        }

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
