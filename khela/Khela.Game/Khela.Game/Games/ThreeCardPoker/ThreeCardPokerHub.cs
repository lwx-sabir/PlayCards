using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Khela.Game.Games.ThreeCardPoker
{
    /// <summary>
    /// Live board push for Three Card Poker (its own hub at <c>/threecardhub</c>, separate from Blackjack).
    /// Clients join a table group and receive a <c>TableUpdated</c> board snapshot on every state change; the
    /// manager broadcasts to the same group after each save.
    /// </summary>
    [Authorize]
    public class ThreeCardPokerHub : Hub
    {
        private readonly ThreeCardPokerTableManager _tables;
        public ThreeCardPokerHub(ThreeCardPokerTableManager tables) => _tables = tables;

        public async Task JoinTable(string tableId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TableGroup(tableId));
            var board = await BoardAsync(tableId);
            if (board != null) await Clients.Caller.SendAsync("TableUpdated", board);
        }

        public Task LeaveTable(string tableId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, TableGroup(tableId));

        public async Task RequestBoard(string tableId)
        {
            var board = await BoardAsync(tableId);
            if (board != null) await Clients.Caller.SendAsync("TableUpdated", board);
        }

        public async Task Heartbeat(string tableId)
        {
            var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId)) await _tables.RecordHeartbeatAsync(tableId, userId);
        }

        private async Task<object> BoardAsync(string tableId)
        {
            var t = await _tables.GetTableAsync(tableId);
            return t == null ? null : ThreeCardPokerBoard.Build(t);
        }

        /// <summary>Group name for a table — shared with the manager's broadcast.</summary>
        internal static string TableGroup(string tableId) => $"table:{tableId}";
    }
}
