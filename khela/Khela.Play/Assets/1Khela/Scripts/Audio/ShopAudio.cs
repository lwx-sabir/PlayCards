using PlayCard.Store;
using PlayCard.UI.RewardFly;
using Sonity;
using UnityEngine;

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

        [Header("3 — the purchase")]
        [Tooltip("The purchase JINGLE — the moment the server says granted. Not the tap: a tap can still fail, and a " +
                 "fanfare for a purchase that then errors is worse than silence.")]
        [SerializeField] private SoundEvent purchaseSuccess;
        [Tooltip("A purchase that FAILED — declined, unavailable, refused by the server. A cancel is deliberately not " +
                 "this: backing out of a sheet is not an error and should not sound like one.")]
        [SerializeField] private SoundEvent purchaseFailed;

        [Header("4 — the burst")]
        [Tooltip("The rewards ERUPTING out of the card. One per reward, so a bundle bursts more than once, staggered " +
                 "by the flight's own stagger.")]
        [SerializeField] private SoundEvent rewardBurst;
        [Tooltip("Play the burst only ONCE per payout, on the first reward. Turn on if a bundle's bursts read as a " +
                 "stutter rather than as separate gestures.")]
        [SerializeField] private bool burstOncePerPayout;

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
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += OnPurchaseCompleted;
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed += OnCatalogChanged;

            RewardFlyTarget.BurstStarted += OnBurstStarted;
            RewardFlyTarget.BurstEnded += OnBurstEnded;

            ConfigureCards();

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
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= OnPurchaseCompleted;
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed -= OnCatalogChanged;

            RewardFlyTarget.BurstStarted -= OnBurstStarted;
            RewardFlyTarget.BurstEnded -= OnBurstEnded;
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

        private void OnCatalogChanged(Khela.Common.Store.StoreCatalogDto _) => ConfigureCards();

        /// <summary>
        /// Hand the card sounds to every button in the shop, including the ones a lane has just spawned.
        ///
        /// Run after each catalog change because that is exactly when new cards exist. Configure is idempotent, so
        /// re-running it over cards that already have the sound costs nothing.
        /// </summary>
        private void ConfigureCards()
        {
            if (cardTap == null && cardDenied == null) return;
            foreach (var card in GetComponentsInChildren<StorePurchaseButton>(includeInactive: true))
            {
                if (card == null) continue;
                foreach (var sound in card.GetComponentsInChildren<ButtonSound>(includeInactive: true))
                    if (sound != null) sound.Configure(cardTap, cardDenied);
            }
        }

        private void OnPurchaseCompleted(IapService.PurchaseResult result)
        {
            if (result == null) return;
            if (result.status == IapService.PurchaseStatus.Success) { burstPlayedThisPayout = false; Play(purchaseSuccess); return; }

            // A CANCEL is not a failure: sounding an error at someone who deliberately backed out of the store sheet
            // reads as the app telling them off.
            if (result.Cancelled) return;
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
