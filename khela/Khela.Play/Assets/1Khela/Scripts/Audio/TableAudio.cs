using PlayCard.Game.Betting;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using PlayCard.UI;
using Sonity;
using UnityEngine;

namespace PlayCard.Audio
{
    /// <summary>
    /// THE owner of the blackjack table's gameplay sound. Every state-driven sound is fired from here and nowhere
    /// else, for the same reason <see cref="RoundEndDirector"/> owns the round-end presentation: sound scattered across
    /// the view, the dealer and the HUD is impossible to balance and impossible to stop.
    ///
    /// The split is deliberate:
    ///  • ANIMATION-locked sounds (the dealer's throw, her chip push, the peek) belong on the animation, and the clip
    ///    events for them already exist on <see cref="DealerAnimator"/> — those call the public Play* methods here so
    ///    the CLIPS own the timing while this still owns the SoundEvent references and the mix.
    ///  • STATE-driven sounds (a card touching the felt, a new round shuffling) are subscribed here.
    ///  • RESULT stingers must NOT be hooked to the board. The server flips RoundInProgress false the instant a round
    ///    resolves — seconds before the dealer has revealed — so a win sting fired from a board push plays over a
    ///    face-down hole card. They belong on the director's PAY beat, which is added with that slice.
    ///
    /// Every SoundEvent field is optional: an unassigned one is silently skipped, so the table is playable while the
    /// bank is only half authored. Put this on an always-active object in the Table scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TableAudio : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;
        [SerializeField] private DealerAnimator dealer;
        [SerializeField] private BetStacks betStacks;
        [SerializeField] private HandValueLabels valueLabels;
        [SerializeField] private HandBlackjackLabels resultLabels;
        [SerializeField] private BetTimerPopup betTimer;
        [SerializeField] private TurnPopup turnPopup;

        [Header("Cards")]
        [Tooltip("The dealer TAKING A CARD FROM THE SHOE — the card sliding out. Fires once per card dealt, on the " +
                 "frame her hand reaches the shoe, so it is locked to the animation rather than to the board.")]
        [SerializeField] private SoundEvent cardPickFromShoe;

        [Tooltip("A dealt card TOUCHING THE FELT. Played at the card's own position, so it pans to the seat it was " +
                 "dealt to. This is the workhorse — it fires 8+ times in the opening deal alone, so it wants a poly " +
                 "group and enough variation not to machine-gun.")]
        [SerializeField] private SoundEvent cardLand;

        [Tooltip("The dealer PEEKING at her hole card — she tilts it up to read it, never flashing the face. Fires on " +
                 "the clip's read frame, once per round, and only when her up-card actually warrants a peek.")]
        [SerializeField] private SoundEvent dealerPeek;

        [Tooltip("The dealer TURNING HER HOLE CARD OVER at the round end — the one card flip of the round, so it can " +
                 "afford to be the most characterful sound on the table. Fires on the flip itself, not on the settle.")]
        [SerializeField] private SoundEvent dealerReveal;

        [Tooltip("The felt being SWEPT to the discard at the round end. ONE sound for the whole sweep, however many " +
                 "cards are on the table — they leave as a single gesture. Played at the discard tray.")]
        [SerializeField] private SoundEvent cardsSweep;

        [Tooltip("Played once as a new round begins dealing (Cards/Shuffle/Riffle or Overhand). Fires on the " +
                 "transition into a round, not on every board push.")]
        [SerializeField] private SoundEvent shuffle;

        [Header("Chips")]
        [Tooltip("ONE chip settling into the committed stack during the DEAL gather — fires per chip, staggered by " +
                 "BetStacks' Gather Interval, so five chips give five ticks. Assign the SAME event the physics " +
                 "chip-on-chip impact uses: it is literally the same thing happening, a chip landing on a stack. " +
                 "It has to be fired from code because the gather is a kinematic tween — no collisions occur.")]
        [SerializeField] private SoundEvent chipsGather;

        [Tooltip("The dealer SWEEPING a losing bet to herself. Fires per losing hand, on the clip's release frame.")]
        [SerializeField] private SoundEvent chipsCollect;

        [Tooltip("YOUR winnings LANDING in front of you — once, when the flying chips arrive at your chip spot. " +
                 "Deliberately local-seat only: a payout landing across the table is somebody else's news, and the " +
                 "pay loop is already the busiest moment of the round. There is intentionally no sound for the " +
                 "dealer PUSHING chips out; the arrival is the beat that reads.")]
        [SerializeField] private SoundEvent chipsPayLand;

        [Tooltip("Chips ADDED to a hand already in play — a DOUBLE, or the matching bet on a freshly SPLIT hand. Not " +
                 "the opening bets, which arrive with the round rather than during it.")]
        [SerializeField] private SoundEvent chipsAdded;

        [Tooltip("ONE win chip hitting the balance icon during the payout burst. Fires many times in quick " +
                 "succession — give it a poly group (POL_Chips) or it will machine-gun. A light coin tick suits it.")]
        [SerializeField] private SoundEvent chipToBalance;

        [Header("Mix")]
        [Tooltip("Play card sounds at their world position (pans across the table) instead of flat 2D. Off is safer " +
                 "on a table this small if the panning reads as distracting.")]
        [SerializeField] private bool spatialiseCards = true;

        [Header("Hand results — fired the frame the label appears, not off the board")]
        [Tooltip("A hand reached 21 without being a natural blackjack (3+ cards, or 21 on a split hand). Plays the " +
                 "instant the value badge first reads 21.")]
        [SerializeField] private SoundEvent handTwentyOne;

        [Tooltip("A hand busted. Plays the instant the BUST banner unrolls.")]
        [SerializeField] private SoundEvent handBust;

        [Tooltip("A NATURAL blackjack — a 2-card 21 on an unsplit hand, pays 3:2. Plays the instant the BLACKJACK " +
                 "banner unrolls, mid-round as the card lands. A natural never also plays the win sting: BJ is a " +
                 "terminal banner, it never becomes Win at settle.")]
        [SerializeField] private SoundEvent handBlackjack;

        [Tooltip("The hand BEAT the dealer (not a natural — that has its own sting). Plays at settle, the instant the " +
                 "WIN banner unrolls, which is during the round-end hold before the payout chips move.")]
        [SerializeField] private SoundEvent handWin;

        [Tooltip("The hand TIED the dealer — stake comes back. Plays as the PUSH banner unrolls.")]
        [SerializeField] private SoundEvent handPush;

        [Tooltip("The hand LOST without busting (a bust has its own sting). Plays as the LOSE banner unrolls — which " +
                 "only happens if the banner prefab actually has a Label_Lose child; without it there is no banner " +
                 "and so, correctly, no sound.")]
        [SerializeField] private SoundEvent handLose;

        [Tooltip("The betting window opened and it is YOUR turn to bet — plays as the countdown slides in. Skipped " +
                 "for a window you have already committed to, and held until the round-end ceremony has finished so " +
                 "it never lands on top of the payout.")]
        [SerializeField] private SoundEvent bettingWindowOpen;

        [Tooltip("LOOPING urgency bed, started when EITHER clock passes its warning threshold and stopped when that " +
                 "clock stops. Shared by the turn timer and the betting timer — they never run at once, so one bed " +
                 "covers both. The SoundContainer MUST have Loop on, or you get a single one-shot and silence after.")]
        [SerializeField] private SoundEvent timerUrgentLoop;

        [Tooltip("ON: only YOUR hands sting. These are announcer-length stingers (1.5-2s), so at a full table every " +
                 "seat busting would talk over itself — and with polyphony 1 they would cut each other mid-word. " +
                 "Turn OFF only if you want the whole table audible.")]
        [SerializeField] private bool resultStingsMySeatOnly = true;

        private bool _prevInRound;

        private void OnEnable()
        {
            // FindObjectsInactive.Include: this may enable before the table root does, and a null ref here means the
            // whole table plays silently with nothing in the log to say why.
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
            if (dealer == null) dealer = FindAnyObjectByType<DealerAnimator>(FindObjectsInactive.Include);
            if (betStacks == null) betStacks = FindAnyObjectByType<BetStacks>(FindObjectsInactive.Include);

            if (betStacks != null)
            {
                betStacks.ChipsAdded += OnChipsAdded;
                betStacks.ChipGathered += OnChipGathered;
            }

            if (view != null)
            {
                view.CardLanded += OnCardLanded;
                view.CardsSwept += OnCardsSwept;
            }
            // The label components are the authority on WHEN a result becomes visible, so the stings hang off them
            // rather than off the board. A board push announces a bust a beat before the card that caused it has even
            // landed — sting off that and the player hears the bad news before seeing why.
            if (valueLabels == null) valueLabels = FindAnyObjectByType<HandValueLabels>(FindObjectsInactive.Include);
            if (resultLabels == null) resultLabels = FindAnyObjectByType<HandBlackjackLabels>(FindObjectsInactive.Include);
            if (valueLabels != null) valueLabels.HandMadeTwentyOne += OnHandTwentyOne;
            if (resultLabels != null) resultLabels.ResultShown += OnHandResult;

            if (betTimer == null) betTimer = FindAnyObjectByType<BetTimerPopup>(FindObjectsInactive.Include);
            if (turnPopup == null) turnPopup = FindAnyObjectByType<TurnPopup>(FindObjectsInactive.Include);
            if (betTimer != null)
            {
                betTimer.WindowOpened += OnBettingWindowOpen;
                betTimer.UrgencyChanged += OnBetUrgency;
            }
            if (turnPopup != null) turnPopup.UrgencyChanged += OnTurnUrgency;

            if (table != null)
            {
                table.OnBoardChanged += OnBoard;
                // Seed from the current board so re-entering a table mid-round doesn't shuffle on the first push.
                _prevInRound = table.Board != null && table.Board.RoundInProgress;
            }
        }

        private void OnDisable()
        {
            if (betStacks != null)
            {
                betStacks.ChipsAdded -= OnChipsAdded;
                betStacks.ChipGathered -= OnChipGathered;
            }
            if (view != null)
            {
                view.CardLanded -= OnCardLanded;
                view.CardsSwept -= OnCardsSwept;
            }
            if (valueLabels != null) valueLabels.HandMadeTwentyOne -= OnHandTwentyOne;
            if (resultLabels != null) resultLabels.ResultShown -= OnHandResult;
            if (betTimer != null)
            {
                betTimer.WindowOpened -= OnBettingWindowOpen;
                betTimer.UrgencyChanged -= OnBetUrgency;
            }
            if (turnPopup != null) turnPopup.UrgencyChanged -= OnTurnUrgency;

            // Last line of defence. Unsubscribing does NOT stop a loop already playing, and leaving the table mid
            // countdown is the single most likely way to strand one — a bed that survives into the lobby is the kind
            // of bug players report as "the sound broke" with no way to clear it but a restart.
            _turnUrgent = false;
            _betUrgent = false;
            StopUrgencyLoop();
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        // ---- state-driven ----

        private void OnCardLanded(int seat, Vector3 worldPos) => PlayCardLand(worldPos);

        private void OnHandTwentyOne(int seat, Vector3 worldPos) => PlayResultSting(handTwentyOne, seat, worldPos);

        // Always for this player — the popup only opens for them in the first place, so there is no seat to filter on.
        private void OnBettingWindowOpen()
        {
            if (bettingWindowOpen != null) bettingWindowOpen.UIPlay();
        }

        // ---- urgency bed ----
        //
        // Two independent clocks feed ONE loop. They should never overlap (the turn clock runs in-round, the betting
        // clock between rounds), but they are driven by separate components with separate lifetimes, so this tracks
        // them as flags and derives the loop from "is either urgent" rather than trusting play/stop calls to arrive in
        // order. A stray stop from the clock that ISN'T running can then never cut the one that is, and a missed stop
        // cannot leave the bed on once both flags are down.
        private bool _turnUrgent;
        private bool _betUrgent;
        private bool _urgencyPlaying;

        private void OnTurnUrgency(bool on) { _turnUrgent = on; ApplyUrgencyLoop(); }
        private void OnBetUrgency(bool on)  { _betUrgent = on;  ApplyUrgencyLoop(); }

        private void ApplyUrgencyLoop()
        {
            bool want = _turnUrgent || _betUrgent;
            if (want == _urgencyPlaying) return;   // idempotent: re-starting a loop every frame is a buzz, not a loop
            if (want)
            {
                if (timerUrgentLoop != null) timerUrgentLoop.UIPlay();
                _urgencyPlaying = true;
            }
            else StopUrgencyLoop();
        }

        private void StopUrgencyLoop()
        {
            if (!_urgencyPlaying) return;
            _urgencyPlaying = false;
            // UIStop pairs with UIPlay — same owner (the SoundManager's UI transform), so this stops exactly the
            // instance started above. Fade-out left on, so a clock that stops on the beat doesn't clip the bed off.
            if (timerUrgentLoop != null) timerUrgentLoop.UIStop();
        }

        /// <summary>
        /// One banner, one sting — every outcome the banner can show has a slot here, and an unassigned slot is simply
        /// silent, so which results speak is an authoring decision rather than a code one.
        /// </summary>
        private void OnHandResult(HandBlackjackLabels.Variant variant, int seat, Vector3 worldPos)
        {
            switch (variant)
            {
                case HandBlackjackLabels.Variant.BJ:   PlayResultSting(handBlackjack, seat, worldPos); break;
                case HandBlackjackLabels.Variant.Bust: PlayResultSting(handBust, seat, worldPos);      break;
                case HandBlackjackLabels.Variant.Win:  PlayResultSting(handWin, seat, worldPos);       break;
                case HandBlackjackLabels.Variant.Push: PlayResultSting(handPush, seat, worldPos);      break;
                case HandBlackjackLabels.Variant.Lose: PlayResultSting(handLose, seat, worldPos);      break;
            }
        }

        /// <summary>
        /// One place for both result stings, so they can never diverge on who hears them or how they are placed.
        /// Played FLAT (UIPlay) rather than at the hand: these read as commentary on the round, not as a noise coming
        /// from a spot on the felt, and a voice panning to the left seat is distracting. The world position is still
        /// carried by the events for anything that wants to place a visual there.
        /// </summary>
        private void PlayResultSting(SoundEvent sound, int seat, Vector3 worldPos)
        {
            if (sound == null) return;
            if (resultStingsMySeatOnly && table != null && seat != table.MySeat) return;
            sound.UIPlay();
        }

        private void OnCardsSwept(Vector3 discardPos)
        {
            if (cardsSweep != null) cardsSweep.PlayAtPosition(transform, discardPos);
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null) return;
            bool inRound = board.RoundInProgress;
            if (inRound && !_prevInRound) PlayShuffle();   // the round just started — one shuffle, not one per push
            _prevInRound = inRound;
        }

        // ---- public beats (clip events / other systems call these; the SoundEvents stay owned here) ----

        /// <summary>
        /// The dealer just drew a card out of the shoe. Called by <see cref="DealerAnimator"/> from the clip event that
        /// already marks that frame.
        ///
        /// Played at the view's DEAL SOURCE — the shoe model the cards physically fly out of. Every pick comes from
        /// that one spot, so unlike the card landing this never needs to move: it is a fixed source, and it should
        /// sound identical every time. Playing it on this component's own transform instead would emit it from
        /// wherever the TableAudio object happens to sit, which is arbitrary. Falls back to flat if no deal source is
        /// authored.
        /// </summary>
        public void PlayCardPickFromShoe()
        {
            if (cardPickFromShoe == null) return;

            var shoe = view != null ? view.DealSource : null;
            if (shoe != null) cardPickFromShoe.PlayAtPosition(transform, shoe.position);
            else cardPickFromShoe.Play(transform);
        }

        /// <summary>A card touched the felt at <paramref name="worldPos"/>.</summary>
        public void PlayCardLand(Vector3 worldPos)
        {
            if (cardLand == null) return;
            if (spatialiseCards) cardLand.PlayAtPosition(transform, worldPos);
            else cardLand.Play(transform);
        }

        /// <summary>The dealer is peeking at her hole card. Played AT the hole card, which is where the action is.</summary>
        public void PlayDealerPeek() => PlayAtHoleCard(dealerPeek);

        /// <summary>The dealer is turning her hole card face up.</summary>
        public void PlayDealerReveal() => PlayAtHoleCard(dealerReveal);

        // Both dealer card moments happen at the same object, so they share the lookup. The hole card is the truthful
        // source — not the dealer anchor, which is the hand's CENTRE and drifts away from the card as she draws.
        private void PlayAtHoleCard(SoundEvent sound)
        {
            if (sound == null) return;

            var hole = view != null ? view.DealerHoleCard() : null;
            if (hole != null) sound.PlayAtPosition(transform, hole.transform.position);
            else sound.Play(transform);   // hole card not on the felt (or no view) — still make the sound
        }

        /// <summary>A new round is being dealt.</summary>
        public void PlayShuffle()
        {
            if (shuffle != null) shuffle.Play(transform);
        }

        // ---- chips ----

        /// <summary>The dealer swept a losing bet in. Played at her chip hand — where the gesture starts.</summary>
        public void PlayChipsCollect() => PlayAtDealerHands(chipsCollect);

        /// <summary>The pushed winnings LANDED at a seat's chip spot. One per winning hand.</summary>
        public void PlayChipsPayLand(Vector3 worldPos)
        {
            if (chipsPayLand != null) chipsPayLand.PlayAtPosition(transform, worldPos);
        }

        /// <summary>One win chip hit the balance icon. UI, so flat — the balance HUD is not in the world.</summary>
        public void PlayChipToBalance()
        {
            if (chipToBalance != null) chipToBalance.Play(transform);
        }

        // Collect and pay are both HER gesture, so both sound from her hands rather than from the seat the chips are
        // travelling to — that is where the push actually happens, and it needs no per-seat bookkeeping to stay right.
        private void PlayAtDealerHands(SoundEvent sound)
        {
            if (sound == null) return;

            var hands = dealer != null ? dealer.ChipHandPoint : null;
            if (hands != null) sound.PlayAtPosition(transform, hands.position);
            else sound.Play(transform);
        }

        private void OnChipsAdded(int seat, Vector3 worldPos)
        {
            if (chipsAdded != null) chipsAdded.PlayAtPosition(transform, worldPos);
        }

        // Owned by the CHIP, not by this component: Sonity keys an instance on (event, owner), so a shared owner would
        // give all five gathered chips one voice and each would cut the last. Per-chip ownership is also what the
        // physics impacts already do, which is why the same SoundEvent can be assigned to both.
        private void OnChipGathered(Transform chip)
        {
            if (chipsGather == null || chip == null) return;
            chipsGather.PlayAtPosition(chip, chip.position);
        }
    }
}
