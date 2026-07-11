using System;
using System.Threading.Tasks;
using PlayCard.ThreeCardPoker.Dtos;

namespace PlayCard.ThreeCardPoker.Net
{
    /// <summary>
    /// Real-time push channel to the server's <c>ThreeCardPokerHub</c> — the exact analogue of the blackjack
    /// <c>IBlackjackHubClient</c>, so the 3CP game codes against this interface and the transport (Best SignalR
    /// live, or REST polling) can be swapped with no game-code change. Game <b>actions</b> (bet/deal/play/fold)
    /// are NOT here — those go over REST via <see cref="TcpRestClient"/>; this channel only joins/leaves a table
    /// and receives masked board snapshots.
    /// </summary>
    public interface ITcpHubClient
    {
        /// <summary>Raised on the Unity main thread when the server pushes a fresh board snapshot.</summary>
        event Action<TcpBoard> OnTableUpdated;

        /// <summary>Raised on the main thread once connected (incl. after a reconnect).</summary>
        event Action OnConnected;

        /// <summary>Raised on the main thread when the connection drops / is reconnecting. Arg is a reason.</summary>
        event Action<string> OnDisconnected;

        bool IsConnected { get; }

        Task ConnectAsync();
        Task DisconnectAsync();

        /// <summary>Subscribe to a table's group and receive its current board snapshot.</summary>
        Task JoinTableAsync(string tableId);

        /// <summary>Unsubscribe from a table's group.</summary>
        Task LeaveTableAsync(string tableId);

        /// <summary>Ask the server to re-send the current board snapshot.</summary>
        Task RequestBoardAsync(string tableId);

        /// <summary>Seated keep-alive so the server doesn't mark us stalled. Hub call on the live transport;
        /// a no-op on the polling fallback (the poll already keeps state fresh and the round-driver auto-folds).</summary>
        Task HeartbeatAsync(string tableId);
    }
}
