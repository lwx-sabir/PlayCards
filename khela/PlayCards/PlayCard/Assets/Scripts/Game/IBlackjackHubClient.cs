using System;
using System.Threading.Tasks;
using PlayCard.Game.Dtos;

namespace PlayCard.Game
{
    /// <summary>
    /// Real-time push channel to the server's BlackjackHub. The rest of the game (scene, UI)
    /// codes against this interface, never against the underlying SignalR library — so the
    /// transport can be swapped (e.g. Microsoft SignalR -> Best SignalR for mobile) by providing
    /// a different implementation, with no changes to game code.
    ///
    /// Game *actions* (bet/hit/stand/deal/...) are NOT here — those go over REST via the
    /// BlackjackController. This channel only joins/leaves tables and receives board snapshots.
    /// </summary>
    public interface IBlackjackHubClient
    {
        /// <summary>Raised on the Unity main thread when the server pushes a fresh board snapshot.</summary>
        event Action<BoardSnapshot> OnTableUpdated;

        /// <summary>Raised on the main thread once the connection is established (incl. after a reconnect).</summary>
        event Action OnConnected;

        /// <summary>Raised on the main thread when the connection drops or is reconnecting. Arg is a reason.</summary>
        event Action<string> OnDisconnected;

        /// <summary>True while the underlying connection is in the Connected state.</summary>
        bool IsConnected { get; }

        Task ConnectAsync();
        Task DisconnectAsync();

        /// <summary>Subscribe to a table's group and receive its current board snapshot.</summary>
        Task JoinTableAsync(string tableId);

        /// <summary>Unsubscribe from a table's group.</summary>
        Task LeaveTableAsync(string tableId);

        /// <summary>Ask the server to re-send the current board snapshot.</summary>
        Task RequestBoardAsync(string tableId);
    }
}
