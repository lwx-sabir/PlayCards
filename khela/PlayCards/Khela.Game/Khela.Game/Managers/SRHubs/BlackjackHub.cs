using CardGames.Blackjack.CardGames.Blackjack;
using CardGames.Platforms;
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
            return BlackjackBoard.Build(table);
        }

        private string TableGroup(string tableId) => $"table:{tableId}";

        private string? GetUserId()
        {
            return Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        }
    }
}
