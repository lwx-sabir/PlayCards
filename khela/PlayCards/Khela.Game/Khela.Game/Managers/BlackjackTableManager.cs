using CardGames.Blackjack.CardGames.Blackjack;
using CardGames.Platforms;
using Khela.Game.Services.Redis;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardGames.Blackjack;

namespace Khela.Game.Managers
{
    public class BlackjackTableManager
    {
        private readonly IRedisService redisService;
        private const int DefaultMaxPlayers = 5;

        public BlackjackTableManager(IRedisService redisService)
        {
            this.redisService = redisService;
        }

        private string GetKey(string tableId) => $"blackjack:table:{tableId}";

        // Create a new table
        public async Task<TableCreateResult> CreateTableAsync(int? maxPlayers = null, int? maxSeatsPerUser = null)
        {
            var tableId = Guid.NewGuid().ToString();
            var game = new BlackJackGame();

            var table = new BlackjackTable
            {
                TableId = tableId,
                MaxPlayers = Math.Clamp(maxPlayers ?? DefaultMaxPlayers, 1, 10),
                Game = game,
                RoundInProgress = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                MaxSeatsPerUser = Math.Clamp(maxSeatsPerUser ?? 1, 1, Math.Clamp(maxPlayers ?? DefaultMaxPlayers, 1, 10))
            };

            table.Seats = Enumerable.Range(1, table.MaxPlayers)
                .Select(i => new Seat { SeatNumber = i })
                .ToList();

            table.TurnDurationSeconds = 20;

            await SaveTableAsync(tableId, table);

            return new TableCreateResult { Game = game, TableId = tableId, MaxPlayers = table.MaxPlayers, MaxSeatsPerUser = table.MaxSeatsPerUser };
        }

        // Get table by ID
        public async Task<BlackjackTable?> GetTableAsync(string tableId)
        {
            var json = await redisService.GetDatabase().StringGetAsync(GetKey(tableId));
            if (json.IsNullOrEmpty) return null;

            var table = JsonSerializer.Deserialize<BlackjackTable>(json);
            if (table == null) return null;

            NormalizeSeats(table);
            return table;
        }

        // Save updated table back
        public async Task SaveTableAsync(string tableId, BlackjackTable table)
        {
            table.UpdatedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(table);
            await redisService.GetDatabase().StringSetAsync(GetKey(tableId), json, TimeSpan.FromHours(2)); // TTL 2h
        }

        public async Task<BlackjackTable?> AddPlayerAsync(string tableId, Player player)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (player.Balance < 0)
                throw new InvalidOperationException("Balance cannot be negative.");

            var existingSeatsForUser = table.Seats.Count(s => s.Player != null && s.Player.Id == player.Id);
            if (existingSeatsForUser >= table.MaxSeatsPerUser)
                throw new InvalidOperationException("Player has reached max seats at this table.");

            var openSeat = table.Seats.FirstOrDefault(s => s.Player == null);
            if (openSeat == null)
                throw new InvalidOperationException("Table is full.");

            var seatedPlayer = new Player(player.Id, player.Balance, player.Name, player.Image, openSeat.SeatNumber);

            openSeat.Player = seatedPlayer;
            table.Game.Players.Add(seatedPlayer);
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> RemovePlayerAsync(string tableId, int seatNumber, string userId)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            table.Game.Players.RemoveAll(p => p.SeatNumber == seatNumber);
            seat.Player = null;
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> PlaceBetAsync(string tableId, string userId, int seatNumber, decimal amount, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (table.RoundInProgress)
                throw new InvalidOperationException("Cannot change bets during an active round.");
            if (amount <= 0)
                throw new InvalidOperationException("Bet amount must be positive.");

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;

            player.ClearBet(handIndex);
            player.IncreaseBet(amount, handIndex);
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> DealAsync(string tableId)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.Game.Players.Any())
                throw new InvalidOperationException("No players seated.");

            foreach (var player in table.Game.Players)
            {
                if (player.Hands.Count == 0 || player.Hands[0].Bet <= 0)
                    throw new InvalidOperationException($"Player {player.Id} has no bet placed.");

                player.PlaceBet(0);
                player.ClearInsurance(0);
            }

            table.Game.DealNewGame();
            MarkNaturals(table);
            SetInitialTurn(table);
            table.RoundInProgress = true;
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<(BlackjackTable? Table, HitResult? Result)> HitAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return (null, null);

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (player.HasBust(handIndex) || player.GetHand(handIndex).Done) throw new InvalidOperationException("Hand already finished.");

            var result = player.Hit(handIndex);
            if (result.IsBust)
            {
                player.GetHand(handIndex).Done = true;
                AdvanceTurn(table);
            }
            else
            {
                RefreshTurn(table);
            }
            await SaveTableAsync(tableId, table);
            return (table, result);
        }

        public async Task<(BlackjackTable? Table, DoubleDownResult? Result)> DoubleDownAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return (null, null);

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (player.HasBust(handIndex) || player.GetHand(handIndex).Done) throw new InvalidOperationException("Hand already finished.");
            if (player.GetHand(handIndex).Hand.Cards.Count != 2) throw new InvalidOperationException("Double down only allowed on first action.");

            var result = player.DoubleDown(handIndex);
            AdvanceTurn(table);
            await SaveTableAsync(tableId, table);
            return (table, result);
        }

        public async Task<BlackjackTable?> PlaceInsuranceAsync(string tableId, string userId, int seatNumber, decimal amount, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (player.GetHand(handIndex).InsuranceBet > 0) throw new InvalidOperationException("Insurance already placed.");

            var upCard = table.Game.Dealer.Hand.Cards.FirstOrDefault(c => c.IsCardUp);
            if (upCard == null || upCard.FaceVal != CardGames.Platforms.FaceValue.Ace)
                throw new InvalidOperationException("Insurance available only when dealer shows an Ace.");

            player.PlaceInsurance(amount, handIndex);
            RefreshTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> SplitAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            seat.Player.Split(handIndex);
            RefreshTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> DealerPlayAndSettleAsync(string tableId)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            // If player turns are still active, advance until done
            if (table.CurrentSeatNumber > 0)
            {
                AdvanceTurn(table);
            }

            table.Game.DealerPlay();
            SettleRound(table.Game);
            table.RoundInProgress = false;
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> StandAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            seat.Player.Stand(handIndex);
            AdvanceTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        private static void SettleRound(BlackJackGame game)
        {
            var dealerTotal = game.Dealer.Hand.GetSumOfHand();
            var dealerBust = dealerTotal > 21;
            var dealerBlackjack = dealerTotal == 21 && game.Dealer.Hand.Cards.Count == 2;

            foreach (var player in game.Players)
            {
                for (int i = 0; i < player.Hands.Count; i++)
                {
                    var handState = player.Hands[i];
                    var playerTotal = handState.Hand.GetSumOfHand();

                    if (handState.InsuranceBet > 0)
                    {
                        if (dealerBlackjack)
                        {
                            player.AddInsurancePayout(handState.InsuranceBet);
                        }
                        handState.InsuranceBet = 0;
                    }

                    if (playerTotal > 21)
                    {
                        player.AddLoss(i);
                        continue;
                    }

                    if (dealerBust)
                    {
                        player.AddWin(2, i);
                        continue;
                    }

                    if (playerTotal > dealerTotal)
                    {
                        player.AddWin(2, i);
                    }
                    else if (playerTotal == dealerTotal)
                    {
                        player.AddPush(i);
                    }
                    else
                    {
                        player.AddLoss(i);
                    }
                }
            }
        }

        private static void MarkNaturals(BlackjackTable table)
        {
            foreach (var player in table.Game.Players)
            {
                var hand = player.Hands.FirstOrDefault();
                if (hand == null) continue;
                if (hand.Hand.Cards.Count == 2 && hand.Hand.GetSumOfHand() == 21)
                {
                    hand.Done = true;
                }
            }
        }

        private static void NormalizeSeats(BlackjackTable table)
        {
            if (table.MaxSeatsPerUser <= 0)
            {
                table.MaxSeatsPerUser = table.MaxPlayers;
            }

            table.Seats ??= new List<Seat>();
            if (table.Seats.Count == 0)
            {
                table.Seats = Enumerable.Range(1, table.MaxPlayers)
                    .Select(i => new Seat { SeatNumber = i })
                    .ToList();
            }

            foreach (var seat in table.Seats)
            {
                seat.Player = null;
            }

            var openSeats = new Queue<Seat>(table.Seats.Where(s => s.Player == null));
            foreach (var player in table.Game.Players)
            {
                // If seat already has this player, skip
                if (player.SeatNumber > 0)
                {
                    var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == player.SeatNumber);
                    if (seat != null)
                    {
                        seat.Player = player;
                        continue;
                    }
                }

                if (openSeats.Count > 0)
                {
                    var seat = openSeats.Dequeue();
                    player.SeatNumber = seat.SeatNumber;
                    seat.Player = player;
                }
            }
        }

        private static void SetInitialTurn(BlackjackTable table)
        {
            var active = GetOrderedHands(table);
            var current = active.FirstOrDefault();
            if (current.seat == -1)
            {
                table.CurrentSeatNumber = -1;
                table.CurrentHandIndex = 0;
                table.TurnExpiresAt = null;
                return;
            }
            table.CurrentSeatNumber = current.seat;
            table.CurrentHandIndex = current.hand;
            table.TurnExpiresAt = DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
        }

        private static void RefreshTurn(BlackjackTable table)
        {
            if (table.CurrentSeatNumber <= 0) return;
            table.TurnExpiresAt = DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
        }

        private static void EnsureTurn(BlackjackTable table, int seatNumber, int handIndex)
        {
            if (table.TurnExpiresAt.HasValue && DateTimeOffset.UtcNow > table.TurnExpiresAt.Value)
            {
                // auto-stand timed-out hand
                AutoStand(table);
            }

            if (table.CurrentSeatNumber != seatNumber || table.CurrentHandIndex != handIndex)
            {
                throw new InvalidOperationException("Not your turn.");
            }
        }

        private static void AutoStand(BlackjackTable table)
        {
            if (table.CurrentSeatNumber <= 0) return;
            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == table.CurrentSeatNumber);
            if (seat?.Player == null) return;
            seat.Player.Stand(table.CurrentHandIndex);
            AdvanceTurn(table);
        }

        private static void AdvanceTurn(BlackjackTable table)
        {
            var active = GetOrderedHands(table).ToList();
            var idx = active.FindIndex(x => x.seat == table.CurrentSeatNumber && x.hand == table.CurrentHandIndex);
            var next = idx >= 0 && idx + 1 < active.Count ? active[idx + 1] : (seat: -1, hand: 0);

            table.CurrentSeatNumber = next.seat;
            table.CurrentHandIndex = next.hand;
            table.TurnExpiresAt = next.seat == -1 ? null : DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
        }

        private static IEnumerable<(int seat, int hand)> GetOrderedHands(BlackjackTable table)
        {
            foreach (var seat in table.Seats.OrderBy(s => s.SeatNumber))
            {
                if (seat.Player == null) continue;
                for (int i = 0; i < seat.Player.Hands.Count; i++)
                {
                    var hand = seat.Player.Hands[i];
                    if (hand.Done || hand.Hand.GetSumOfHand() > 21) continue;
                    yield return (seat.SeatNumber, i);
                }
            }
            yield return (-1, 0);
        }
    }

    public class TableCreateResult
    {
        public string TableId { get; set; }

        public BlackJackGame Game { get; set; }

        public int MaxPlayers { get; set; }

        public int MaxSeatsPerUser { get; set; }
    }

    public class BlackjackTable
    {
        public string TableId { get; set; }

        public int MaxPlayers { get; set; }

        public int MaxSeatsPerUser { get; set; }

        public bool RoundInProgress { get; set; }

        public BlackJackGame Game { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public List<Seat> Seats { get; set; } = new List<Seat>();

        public int TurnDurationSeconds { get; set; } = 20;

        public int CurrentSeatNumber { get; set; } = -1;

        public int CurrentHandIndex { get; set; } = 0;

        public DateTimeOffset? TurnExpiresAt { get; set; }
    }

    public class Seat
    {
        public int SeatNumber { get; set; }
        public Player? Player { get; set; }
    }
}
