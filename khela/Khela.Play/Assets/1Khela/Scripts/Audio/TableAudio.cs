using PlayCard.Game.Betting;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
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
        [SerializeField] private BetBuilder betBuilder;

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
        [Tooltip("Your dropped chips being PULLED INTO the committed stack when you press DEAL/REPEAT. Several chips " +
                 "move at once, so this wants a multi-chip clip (3on2 / 5on3), not a single-chip one.")]
        [SerializeField] private SoundEvent chipsGather;

        [Tooltip("The dealer SWEEPING a losing bet to herself. Fires per losing hand, on the clip's release frame.")]
        [SerializeField] private SoundEvent chipsCollect;

        [Tooltip("The dealer PUSHING winnings out to a seat. Fires per winning hand, on the clip's release frame — the " +
                 "moment they LEAVE her hands. Leave empty if you only want the arrival below.")]
        [SerializeField] private SoundEvent chipsPay;

        [Tooltip("The winnings LANDING in front of the player. A separate beat from the push above: fires once per " +
                 "winning hand when the flying chips arrive, at that hand's chip spot. This is the payoff sound.")]
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

        private bool _prevInRound;

        private void OnEnable()
        {
            // FindObjectsInactive.Include: this may enable before the table root does, and a null ref here means the
            // whole table plays silently with nothing in the log to say why.
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
            if (dealer == null) dealer = FindAnyObjectByType<DealerAnimator>(FindObjectsInactive.Include);
            if (betStacks == null) betStacks = FindAnyObjectByType<BetStacks>(FindObjectsInactive.Include);
            if (betBuilder == null) betBuilder = FindAnyObjectByType<BetBuilder>(FindObjectsInactive.Include);

            if (betBuilder != null) betBuilder.OnDealCommitted += OnDealCommitted;
            if (betStacks != null) betStacks.ChipsAdded += OnChipsAdded;

            if (view != null)
            {
                view.CardLanded += OnCardLanded;
                view.CardsSwept += OnCardsSwept;
            }
            if (table != null)
            {
                table.OnBoardChanged += OnBoard;
                // Seed from the current board so re-entering a table mid-round doesn't shuffle on the first push.
                _prevInRound = table.Board != null && table.Board.RoundInProgress;
            }
        }

        private void OnDisable()
        {
            if (betBuilder != null) betBuilder.OnDealCommitted -= OnDealCommitted;
            if (betStacks != null) betStacks.ChipsAdded -= OnChipsAdded;
            if (view != null)
            {
                view.CardLanded -= OnCardLanded;
                view.CardsSwept -= OnCardsSwept;
            }
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        // ---- state-driven ----

        private void OnCardLanded(int seat, Vector3 worldPos) => PlayCardLand(worldPos);

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

        /// <summary>The dealer pushed winnings out.</summary>
        public void PlayChipsPay() => PlayAtDealerHands(chipsPay);

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

        private void OnDealCommitted(long amount)
        {
            if (chipsGather == null) return;

            // At your own bet spot. Spatialised for consistency with the rest of the felt, though your own seat is
            // near-centre from your camera anyway.
            var anchor = (betStacks != null && table != null) ? betStacks.ChipAnchor(table.MySeat) : null;
            if (anchor != null) chipsGather.PlayAtPosition(transform, anchor.position);
            else chipsGather.Play(transform);
        }
    }
}
