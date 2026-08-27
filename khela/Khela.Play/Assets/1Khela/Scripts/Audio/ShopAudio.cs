using PlayCard.Store;
using PlayCard.UI.RewardFly;
using System.Collections;
using Sonity;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Audio
{
    /// <summary>
    /// THE owner of the shop's sound, for the same reason <see cref="TableAudio"/> and <see cref="PassAudio"/> own
    /// theirs: sound scattered across a screen, its cards and its reward flight is impossible to balance and impossible
    /// to stop.
    ///
    /// It hears the screen through EVENTS, and it CONFIGURES the cards rather than listening to them — the lanes spawn
    /// their cards from a prefab at runtime, so a per-card sound authored by hand would only ever cover the cards that
    /// happen to be authored. Pushing the two card sounds onto every <see cref="ButtonSound"/> in the shop, after every
    /// catalog refresh, is what makes a spawned card sound like the rest of the game.
    ///
    /// NOT here: the sound a reward makes when it LANDS on its counter. That belongs to the counter, on
    /// <c>RewardFlyTarget.impactSound</c> — the target already knows which reward it is, and routing that through each
    /// screen is how a chip ends up sounding different depending on which panel paid it.
    ///
    /// Every SoundEvent is optional; an unassigned one is skipped, so the shop is usable while the bank is half
    /// authored. Put this on the shop panel's root, which is active exactly while the shop is open.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopAudio : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private ShopScreen screen;
        [SerializeField] private ShopTabs tabs;
        [Tooltip("The purchase ceremony. It ANNOUNCES its beats and this plays them, so the view stays display-only.")]
        [SerializeField] private PurchaseView purchase;

        [Header("0 — the panel")]
        [Tooltip("The shop OPENING. Fires with the open, not with the catalog answer — the sound belongs to the screen " +
                 "arriving, and the cards fill in a beat later.")]
        [SerializeField] private SoundEvent panelOpen;
        [Tooltip("The shop CLOSING. Fires as the close starts, so it plays over the exit rather than after the panel " +
                 "is already gone.")]
        [SerializeField] private SoundEvent panelClose;
        [Tooltip("The BACK button specifically, when it should differ from the general close. Leave empty and back " +
                 "just uses the close sound.")]
        [SerializeField] private SoundEvent backTap;

        [Header("1 — navigating")]
        [Tooltip("A TAB being tapped. Only a tap — the strip also lights up as you scroll, and sounding that would " +
                 "chirp all the way down the page.")]
        [SerializeField] private SoundEvent tabTap;

        [Header("2 — the cards")]
        [Tooltip("A card being tapped. Pushed onto every card's ButtonSound, including the ones a lane spawns, so it " +
                 "fires on POINTER DOWN like every other button in the game rather than on the click.")]
        [SerializeField] private SoundEvent cardTap;
        [Tooltip("A card that CANNOT be bought being tapped — sold out, limit reached, store not ready. Leave empty to " +
                 "keep a refused tap silent, which is what the table does with a denied action.")]
        [SerializeField] private SoundEvent cardDenied;

        [Header("3 — the purchase ceremony")]
        [Tooltip("The receipt is with the server and the VERIFYING state is up. Something small and patient — this is " +
                 "a wait, and it can still end badly. Leave empty for silence under the spinner.")]
        [SerializeField] private SoundEvent purchaseVerifying;
        [Tooltip("The purchase JINGLE — the server said GRANTED and the celebration is starting. Fires with the " +
                 "CEREMONY, not with the purchase result: the result lands before the minimum-verify delay has even " +
                 "elapsed, so hanging the fanfare off it plays it over the spinner.")]
        [SerializeField] private SoundEvent purchaseSuccess;
        [Tooltip("The item hitting its mark — the BANG. The loudest thing here, and the one that has to sit exactly on " +
                 "the squash or the whole stamp reads as mistimed.")]
        [SerializeField] private SoundEvent itemStamp;
        [Tooltip("A granted card LEAVING the item. One per card, so it fires as often as there are rewards — keep it " +
                 "light, a whoosh rather than a hit.")]
        [SerializeField] private SoundEvent cardLaunch;
        [Tooltip("A granted card LANDING in its slot. The little sibling of the item stamp; pitch it up per card with " +
                 "Card Land Pitch Step to make a run of rewards climb.")]
        [SerializeField] private SoundEvent cardLand;
        [Tooltip("SEMITONES added per card landing, so three rewards read as a rising run rather than the same click " +
                 "three times. 1 is subtle, 2 is a clear climb. 0 = every card lands identically.")]
        [SerializeField] private float cardLandPitchStep = 1f;
        [Tooltip("A purchase that FAILED — declined, unavailable, refused by the server. A cancel is deliberately not " +
                 "this: backing out of a sheet is not an error and should not sound like one.")]
        [SerializeField] private SoundEvent purchaseFailed;
        [Tooltip("Pending or rejected — the state that needs SAYING. Softer than a failure: the money may well be " +
                 "fine, and an alarm over 'this will arrive shortly' is a lie about how bad it is.")]
        [SerializeField] private SoundEvent purchaseProblem;

        [Header("4 — the burst")]
        [Tooltip("The rewards ERUPTING out of the card. One per reward, so a bundle bursts more than once, staggered " +
                 "by the flight's own stagger.")]
        [SerializeField] private SoundEvent rewardBurst;
        [Tooltip("Play the burst only ONCE per payout, on the first reward. Turn on if a bundle's bursts read as a " +
                 "stutter rather than as separate gestures.")]
        [SerializeField] private bool burstOncePerPayout;

        private Coroutine configureRoutine;
        private bool burstPlayedThisPayout;
        /// <summary>Guards the open sound against playing twice — see <see cref="PlayOpen"/>.</summary>
        private bool openSoundPlayed;

        private void Awake()
        {
            // `==`/`!=` rather than `??` — the null-coalescing operator ignores Unity's overloaded null, so a failed
            // GetComponent would satisfy it and leave a dead reference behind.
            if (screen == null) screen = GetComponentInParent<ShopScreen>();
            if (screen == null) screen = GetComponentInChildren<ShopScreen>(true);
            if (tabs == null) tabs = GetComponentInChildren<ShopTabs>(true);
            if (purchase == null) purchase = GetComponentInParent<PurchaseView>();
            if (purchase == null) purchase = GetComponentInChildren<PurchaseView>(true);
        }

        private void OnEnable()
        {
            openSoundPlayed = false;
            if (screen != null)
            {
                screen.Opened += OnPanelOpened;
                screen.Closing += OnPanelClosing;
            }
            if (tabs != null) tabs.TabTapped += OnTabTapped;
            if (purchase != null)
            {
                purchase.Verifying      += OnVerifying;
                purchase.CeremonyStarted += OnCeremonyStarted;
                purchase.Stamped        += OnStamped;
                purchase.CardLaunched   += OnCardLaunched;
                purchase.CardLanded     += OnCardLanded;
                purchase.Problem        += OnProblem;
            }
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += OnPurchaseCompleted;
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed += OnCatalogChanged;

            RewardFlyTarget.BurstStarted += OnBurstStarted;
            RewardFlyTarget.BurstEnded += OnBurstEnded;

            ConfigureCardsSoon();

            // Played HERE as well as on the event, because the two run in an order nothing guarantees: this component
            // and ShopScreen sit on the same object, and if the screen's OnEnable runs first it fires Opened before
            // anything has subscribed. This object is active exactly while the shop is, so its own enable IS the open.
            PlayOpen();
        }

        private void OnDisable()
        {
            if (screen != null)
            {
                screen.Opened -= OnPanelOpened;
                screen.Closing -= OnPanelClosing;
            }
            if (tabs != null) tabs.TabTapped -= OnTabTapped;
            if (purchase != null)
            {
                purchase.Verifying      -= OnVerifying;
                purchase.CeremonyStarted -= OnCeremonyStarted;
                purchase.Stamped        -= OnStamped;
                purchase.CardLaunched   -= OnCardLaunched;
                purchase.CardLanded     -= OnCardLanded;
                purchase.Problem        -= OnProblem;
            }
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= OnPurchaseCompleted;
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed -= OnCatalogChanged;

            RewardFlyTarget.BurstStarted -= OnBurstStarted;
            RewardFlyTarget.BurstEnded -= OnBurstEnded;
            if (configureRoutine != null) { StopCoroutine(configureRoutine); configureRoutine = null; }
        }

        private void OnPanelOpened() => PlayOpen();

        /// <summary>The open sound, at most once per opening whichever of the two paths gets here first.</summary>
        private void PlayOpen()
        {
            if (openSoundPlayed) return;
            openSoundPlayed = true;
            Play(panelOpen);
        }

        private void OnPanelClosing() => Play(backTap != null ? backTap : panelClose);

        private void OnTabTapped(int _) => Play(tabTap);

        private void OnCatalogChanged(Khela.Common.Store.StoreCatalogDto _) => ConfigureCardsSoon();

        /// <summary>
        /// Configure NEXT frame, because this always runs before the cards it is meant to configure exist.
        ///
        /// Two orderings conspire. This component lives on the shop root and ShopSection lives down in the lanes, and
        /// Unity raises OnEnable parent-first — so the open configures an empty shop. And both subscribe to
        /// StoreCatalog.Changed, where subscribers fire in subscription order: this one registered first, so on every
        /// refresh it configures the OLD cards and only afterwards does ShopSection destroy them and instantiate the
        /// ones the player actually taps.
        ///
        /// A frame is enough — the sections build synchronously inside their own Changed handler.
        /// </summary>
        private void ConfigureCardsSoon()
        {
            if (!isActiveAndEnabled) return;
            if (configureRoutine != null) StopCoroutine(configureRoutine);
            configureRoutine = StartCoroutine(ConfigureNextFrame());
        }

        private IEnumerator ConfigureNextFrame()
        {
            yield return null;
            configureRoutine = null;
            ConfigureCards();
        }

        /// <summary>
        /// Give every card in the shop its tap sound, ADDING the component where there isn't one.
        ///
        /// Adding rather than merely configuring is the entire point. The lanes build their cards at runtime from
        /// prefabs, so a ButtonSound placed by hand only ever covers the prefabs someone remembered to open — and a
        /// new card type ships silent with nothing to say so. Searching for a component that was never authored just
        /// does nothing, quietly, which is exactly how this went unnoticed.
        ///
        /// Run after each catalog change, because that is when new cards exist. Idempotent: a card that already has
        /// the component keeps it and is simply re-configured.
        /// </summary>
        private void ConfigureCards()
        {
            if (cardTap == null && cardDenied == null) return;
            foreach (var card in GetComponentsInChildren<StorePurchaseButton>(includeInactive: true))
            {
                if (card == null) continue;
                // Every Button on the card, because ButtonSound requires one and hangs off its pointer-down.
                foreach (var button in card.GetComponentsInChildren<Button>(includeInactive: true))
                {
                    if (button == null) continue;
                    if (!button.TryGetComponent<ButtonSound>(out var sound))
                        sound = button.gameObject.AddComponent<ButtonSound>();
                    sound.Configure(cardTap, cardDenied);
                }
            }
        }

        // ---------------------------------------------------------------- the ceremony

        private void OnVerifying() => Play(purchaseVerifying);

        /// <summary>The server granted. THIS is the fanfare's moment — see the tooltip on purchaseSuccess.</summary>
        private void OnCeremonyStarted()
        {
            burstPlayedThisPayout = false;
            Play(purchaseSuccess);
        }

        private void OnStamped() => Play(itemStamp);

        private void OnCardLaunched(int index) => Play(cardLaunch);

        /// <summary>A card landing, pitched up per card so a run of rewards climbs instead of repeating.</summary>
        private void OnCardLanded(int index)
        {
            if (cardLand == null) return;
            if (cardLandPitchStep == 0f) { cardLand.UIPlay(); return; }
            // Pitch is a sound PARAMETER in Sonity, not an argument on the play call.
            cardLand.UIPlay(new SoundParameterPitchSemitone(index * cardLandPitchStep));
        }

        private void OnProblem() => Play(purchaseProblem != null ? purchaseProblem : purchaseFailed);

        private void OnPurchaseCompleted(IapService.PurchaseResult result)
        {
            if (result == null) return;
            // Success is NOT sounded here. The result arrives before the minimum-verify delay has elapsed, so a
            // fanfare hung off it plays over the spinner; the ceremony announces its own start instead.
            if (result.status == IapService.PurchaseStatus.Success) return;

            // A CANCEL is not a failure: sounding an error at someone who deliberately backed out of the store sheet
            // reads as the app telling them off. Pending/rejected are the view's Problem state, not this.
            if (result.Cancelled) return;
            if (result.status == IapService.PurchaseStatus.Pending) return;
            Play(purchaseFailed);
        }

        private void OnBurstStarted(string rewardId, int pieces)
        {
            if (rewardBurst == null) return;
            if (burstOncePerPayout && burstPlayedThisPayout) return;
            burstPlayedThisPayout = true;
            rewardBurst.UIPlay();
        }

        // Nothing to stop — the landings are the target's business now. The hook stays so a tail sting has a home.
        private void OnBurstEnded(string rewardId) { }

        private void Play(SoundEvent sound)
        {
            if (sound != null) sound.UIPlay();
        }
    }
}
