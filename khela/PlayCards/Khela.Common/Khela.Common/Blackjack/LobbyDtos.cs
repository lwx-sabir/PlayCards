using System.Collections.Generic;

namespace Khela.Common.Blackjack
{
    /// <summary>One row in the blackjack lobby's table browser (the screen-3 table list).</summary>
    public class BlackjackTableSummary
    {
        public string TableId { get; set; } = string.Empty;
        public BlackjackMode Mode { get; set; }
        public decimal MinBet { get; set; }
        public decimal MaxBet { get; set; }
        public int MaxPlayers { get; set; }
        public int SeatsOccupied { get; set; }
        public bool RoundInProgress { get; set; }

        /// <summary>Seated players, for the lobby's avatar/chips preview.</summary>
        public List<TableOccupant> Occupants { get; set; } = new List<TableOccupant>();
    }

    /// <summary>A seated player as shown in the lobby preview.</summary>
    public class TableOccupant
    {
        public int SeatNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public decimal Balance { get; set; }
    }
}
