using CardGames.Blackjack.CardGames.Blackjack;
using Khela.Game.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace Khela.Game.Managers.SRHubs
{
    [Authorize]
    public class BlackjackHub : Hub
    {
        private readonly BlackjackTableManager _tableManager;

        public BlackjackHub(BlackjackTableManager tableManager)
        {
            _tableManager = tableManager;
        }

        /// <summary>
        /// Subscribe caller to a table group and return current board snapshot.
        /// </summary>
        public async Task JoinTable(string tableId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TableGroup(tableId));
            var board = await GetBoardAsync(tableId);
            if (board != null)
            {
                await Clients.Caller.SendAsync("TableUpdated", board);
            }
        }

        /// <summary>
        /// Unsubscribe caller from table group.
        /// </summary>
        public async Task LeaveTable(string tableId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, TableGroup(tableId));
        }

        /// <summary>
        /// Caller requests current board snapshot.
        /// </summary>
        public async Task RequestBoard(string tableId)
        {
            var board = await GetBoardAsync(tableId);
            if (board != null)
            {
                await Clients.Caller.SendAsync("TableUpdated", board);
            }
        }

        /// <summary>
        /// Broadcast a table update to all subscribers (for use by server-side calls).
        /// </summary>
        public async Task BroadcastTable(string tableId)
        {
            var board = await GetBoardAsync(tableId);
            if (board != null)
            {
                await Clients.Group(TableGroup(tableId)).SendAsync("TableUpdated", board);
            }
        }

        private async Task<object?> GetBoardAsync(string tableId)
        {
            var table = await _tableManager.GetTableAsync(tableId);
            if (table == null) return null;

            return new
            {
                table.TableId,
                table.MaxPlayers,
                table.MaxSeatsPerUser,
                table.RoundInProgress,
                Dealer = new
                {
                    Cards = table.Game.Dealer.Hand.Cards.Select(c => new { c.FaceVal, c.Suit, c.IsCardUp }),
                    HandValue = table.Game.Dealer.Hand.GetSumOfHand()
                },
                Players = table.Game.Players.Select(ToPlayerDto),
                Seats = table.Seats.Select(ToSeatDto)
            };
        }

        private string TableGroup(string tableId) => $"table:{tableId}";

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
            return Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        }
    }
}
