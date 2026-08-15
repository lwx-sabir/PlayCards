using System;
using System.Collections.Generic;

namespace PlayCard.Game.Dtos
{
    /// <summary>
    /// Client-side mirror of the board snapshot pushed by the server's BlackjackHub
    /// ("TableUpdated" message). Property names match the server's projection; the SignalR
    /// JSON protocol is case-insensitive, so PascalCase here maps to the server's camelCase.
    ///
    /// NOTE: <see cref="CardView.FaceVal"/> and <see cref="CardView.Suit"/> arrive as integers
    /// (System.Text.Json serialises the server's FaceValue/Suit enums as numbers). Confirm the
    /// server enum ordering when mapping to card art — better still, promote these DTOs + the
    /// FaceValue/Suit enums into Khela.Common so client and server share one definition and can
    /// never drift.
    /// </summary>
    public sealed class BoardSnapshot
    {
        public string TableId { get; set; }

        /// <summary>Monotonic server timestamp (stamped on every SaveTableAsync). TableController drops any snapshot
        /// whose UpdatedAt is older than the one it already holds, so a late REST/poll response can't clobber a newer
        /// hub push (stale board / resurrected round / wrong turn). Default (null/min) = always accepted.</summary>
        public DateTimeOffset? UpdatedAt { get; set; }

        public int MaxPlayers { get; set; }
        public int MaxSeatsPerUser { get; set; }
        public bool RoundInProgress { get; set; }

        /// <summary>Table stake bounds — for client-side bet validation (the server re-validates).</summary>
        public decimal MinBet { get; set; }
        public decimal MaxBet { get; set; }

        /// <summary>Seat whose turn it is (server seat number), or -1 when nobody is to act.</summary>
        public int CurrentSeatNumber { get; set; } = -1;

        /// <summary>Hand index (for splits) the current seat must act on.</summary>
        public int CurrentHandIndex { get; set; }

        /// <summary>When the current turn auto-expires (UTC), or null when no turn is active.</summary>
        public DateTimeOffset? TurnExpiresAt { get; set; }

        /// <summary>The configured decision length. <see cref="TurnExpiresAt"/> is stamped as a GENEROUS ceiling until
        /// our client calls /presented (see TableController), so the countdown is CLAMPED to this — we never show more
        /// than a real turn, even in the moment before the collapse lands or if that call never happens.</summary>
        public int TurnDurationSeconds { get; set; }

        /// <summary>While set, the round is in its INSURANCE phase (its own countdown); play hasn't started.</summary>
        public DateTimeOffset? InsuranceExpiresAt { get; set; }

        /// <summary>Between rounds: when the BETTING window closes and the server deals whatever bets are down.
        /// Null = no window (either disabled server-side, or the table is idle waiting for someone to bet).
        /// Like <see cref="TurnExpiresAt"/> this is a generous ceiling until our /presented call collapses it,
        /// so clamp the displayed countdown to <see cref="BettingDurationSeconds"/>.</summary>
        public DateTimeOffset? BettingExpiresAt { get; set; }

        /// <summary>The configured betting-window length, for clamping the countdown. 0 = window disabled.</summary>
        public int BettingDurationSeconds { get; set; }

        /// <summary>Id of the most recently settled hand — feeds GET /api/Blackjack/verify/{handId}.</summary>
        public string LastHandId { get; set; }

        /// <summary>Provably-fair commitment for the current round (never the secret server seed).</summary>
        public FairnessView Fairness { get; set; }

        /// <summary>Shoe state — lets the table show the cut card and a "new shoe" beat. Null from an older server.</summary>
        public ShoeView Shoe { get; set; }

        public DealerView Dealer { get; set; }
        public List<PlayerView> Players { get; set; } = new List<PlayerView>();
        public List<SeatView> Seats { get; set; } = new List<SeatView>();

        /// <summary>Per-seat outcome of the most recently settled round (drives the result banner); empty mid-round.</summary>
        public List<SeatResultView> LastResults { get; set; } = new List<SeatResultView>();
    }

    /// <summary>One seat's outcome for the last settled round — mirrors the server's SeatRoundResult.</summary>
    public sealed class SeatResultView
    {
        public int SeatNumber { get; set; }
        public string Outcome { get; set; }     // "win" | "lose" | "push" — the seat's NET across all its hands
        public decimal Delta { get; set; }        // net chips change this round (signed: + win, - loss, 0 push)
        public decimal Payout { get; set; }       // gross returned to the wallet
        public int FinalHandValue { get; set; }
        public bool Bust { get; set; }
        public bool Blackjack { get; set; }

        /// <summary>Per-hand results for THIS seat, ordered by hand index (0 = main, 1 = split, …). Lets the banner
        /// label — and the round-end director pay/collect — each split hand on its own, where <see cref="Outcome"/>/
        /// <see cref="Delta"/> (a net) would call a mixed win/loss a "push" and move no chips. A single-hand seat has
        /// one entry matching the seat-level values; empty from an older server, in which case consumers fall back to
        /// the seat-level fields.</summary>
        public List<HandResultView> Hands { get; set; } = new List<HandResultView>();
    }

    /// <summary>One HAND's outcome within a settled seat — mirrors the server's HandRoundResult.</summary>
    public sealed class HandResultView
    {
        public int HandIndex { get; set; }
        /// <summary>"blackjack" | "win" | "push" | "bust" | "lose" — this hand alone, not the seat net.</summary>
        public string Outcome { get; set; }
        /// <summary>Total staked on this hand (main stake incl. any double-down extra, plus insurance).</summary>
        public decimal Stake { get; set; }
        /// <summary>Gross returned for this hand (0 on a loss/bust, stake back on a push, incl. insurance).</summary>
        public decimal Payout { get; set; }
        /// <summary>Net for this hand = Payout − Stake (signed). The seat's Delta is the sum of these.</summary>
        public decimal Delta { get; set; }

        /// <summary>The insurance side bet on this hand, and what it returned gross (0 if lost or never placed).</summary>
        public decimal InsuranceBet { get; set; }

        /// <inheritdoc cref="InsuranceBet"/>
        public decimal InsuranceReturn { get; set; }

        /// <summary>What insurance did to the net: winnings if it won, −stake if it lost, 0 if never placed.</summary>
        public decimal InsuranceDelta => InsuranceReturn - InsuranceBet;

        /// <summary>
        /// The MAIN WAGER's net, with insurance taken back out. This is what decides whether the dealer collects this
        /// hand or pays it — <see cref="Delta"/> nets the two, and insurance against a dealer blackjack cancels the
        /// hand exactly (that is what insurance is for), leaving a 0 that reads as a push and moves no chips at all.
        /// </summary>
        public decimal HandDelta => Delta - InsuranceDelta;
    }

    public sealed class FairnessView
    {
        /// <summary>SHA-256 commitment of the secret server seed, published before the deal.</summary>
        public string ServerSeedHash { get; set; }
        public string ClientSeed { get; set; }

        /// <summary>Rounds dealt at this table.</summary>
        public long RoundNonce { get; set; }

        /// <summary>Shoes used at this table — this, NOT <see cref="RoundNonce"/>, is the nonce the shuffle is
        /// derived from, since one shoe spans many rounds.</summary>
        public long ShoeNonce { get; set; }
    }

    /// <summary>
    /// The dealing shoe. A multi-deck shoe PERSISTS across rounds and is only replaced once the cut card is reached,
    /// so these values move round to round. A table configured to reshuffle every round (a single deck, or
    /// ReshuffleEveryRound) reports <see cref="CutCardAt"/> 0 and never sets <see cref="CutCardReached"/>.
    ///
    /// All-zero with a null <see cref="ShoeId"/> means the table has no managed shoe yet (it has not dealt since
    /// the shoe was introduced) — render no cut-card indicator rather than an empty one.
    /// </summary>
    public sealed class ShoeView
    {
        /// <summary>Cards in the shoe when it was shuffled (52 × deck count). 0 from an older server.</summary>
        public int ShoeSize { get; set; }

        /// <summary>Cards still undealt. Falls through the round as cards come out.</summary>
        public int CardsRemaining { get; set; }

        /// <summary>The cut card sits here: once <see cref="CardsRemaining"/> drops to this, the shoe is spent.
        /// 0 means no cut card (single-deck table).</summary>
        public int CutCardAt { get; set; }

        /// <summary>The cut card has been reached. The CURRENT round still finishes on this shoe — the next deal
        /// comes from a freshly shuffled one. That is the cue for a "new shoe" presentation.</summary>
        public bool CutCardReached { get; set; }

        /// <summary>Hash of this shoe as shuffled — changes exactly when a new shoe is brought in, so the client can
        /// detect the swap without inferring it from the counts.</summary>
        public string ShoeId { get; set; }
    }

    public sealed class DealerView
    {
        public List<CardView> Cards { get; set; } = new List<CardView>();
        public int HandValue { get; set; }
    }

    public sealed class PlayerView
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal Balance { get; set; }
        public int SeatNumber { get; set; }
        public List<HandView> Hands { get; set; } = new List<HandView>();
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Push { get; set; }

        /// <summary>Participating in the CURRENT round. False = seated but spectating (joined mid-round, or sitting
        /// out because they didn't bet). Drives the "waiting for next round" panel and the leave-button lock.</summary>
        public bool InRound { get; set; }

        /// <summary>Actively placed a bet during the CURRENT betting window (not just a persisted auto-repeat). Lets the
        /// bet stacks show a seat's wager DURING the window (before the deal), so a held deal keeps chips on the felt.</summary>
        public bool BetThisWindow { get; set; }
    }

    public sealed class HandView
    {
        public int HandIndex { get; set; }
        public decimal Bet { get; set; }
        public decimal Insurance { get; set; }
        /// <summary>Hand has finished acting (stood/bust/double/split-aces) — used to close the insurance window.</summary>
        public bool Done { get; set; }
        public List<CardView> Cards { get; set; } = new List<CardView>();
        public int HandValue { get; set; }
    }

    public sealed class SeatView
    {
        public int SeatNumber { get; set; }
        public bool Occupied { get; set; }
        public PlayerView Player { get; set; }

        /// <summary>Server says this seat's client is alive (heartbeat fresh). False ⇒ render "disconnected…".
        /// Defaults true so a snapshot from an older server (no field) doesn't show everyone as disconnected.</summary>
        public bool IsConnected { get; set; } = true;
        /// <summary>No heartbeat past the server's StalledTimeout — auto-removal is imminent.</summary>
        public bool IsStalled { get; set; }

        /// <summary>Consecutive betting windows this seat has sat out without betting (for UI / debugging).</summary>
        public int MissedBetWindows { get; set; }

        /// <summary>This seat is in its FINAL betting window before being evicted for not betting. The local player
        /// sees the "bet or you'll be removed" warning when this is true for their own seat.</summary>
        public bool IdleKickWarning { get; set; }
    }

    public sealed class CardView
    {
        /// <summary>Server FaceValue enum as an integer. Confirm ordering before mapping to art.</summary>
        public int FaceVal { get; set; }

        /// <summary>Server Suit enum as an integer. Confirm ordering before mapping to art.</summary>
        public int Suit { get; set; }

        /// <summary>Blackjack point value (J/Q/K = 10, Ace = 11); 0 for a masked hole card.</summary>
        public int Value { get; set; }

        /// <summary>False for the dealer's hole card (rendered face-down).</summary>
        public bool IsCardUp { get; set; }
    }
}
