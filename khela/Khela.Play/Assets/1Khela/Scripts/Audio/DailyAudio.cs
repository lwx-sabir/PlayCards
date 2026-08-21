using System;
using PlayCard.Daily;
using PlayCard.UI.RewardFly;
using Sonity;
using UnityEngine;

namespace PlayCard.Audio
{
    /// <summary>
    /// THE owner of the daily-login panel's sound, for the same reason <see cref="TableAudio"/> owns the table's:
    /// sound scattered across the tiles, the panel and the reward flight is impossible to balance and impossible to
    /// stop.
    ///
    /// Deliberately its OWN component rather than a shared one: the daily ladder and the pass are separate features
    /// with separate banks, and a single audio script serving both would mean tuning one panel's mix by editing the
    /// other's. Nothing here references the pass.
    ///
    /// It hears everything through EVENTS, never by being handed references to the flight — the burst channel on
    /// <see cref="RewardFlyTarget"/> is static, so this works whether the panel is a scene object or a runtime prefab,
    /// and whichever HUD the pieces are flying to.
    ///
    /// Every SoundEvent field is optional: an unassigned one is silently skipped, so the panel is usable while the
    /// bank is only half authored. Put this on the daily panel's ROOT — the object that is active exactly while the
    /// panel is open, so it is not listening to the reward flight of some other screen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DailyAudio : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private DailyScreen screen;
        [SerializeField] private DailyPanel panel;

        [Header("0 — the panel")]
        [Tooltip("The daily panel OPENING. Fires with the open tween, not with the fetch — the sound belongs to the " +
                 "sheet arriving, and the ladder fills in a beat later.")]
        [SerializeField] private SoundEvent panelOpen;

        [Tooltip("The daily panel CLOSING. Fires as the close tween starts, so it plays over the exit rather than " +
                 "after it — a dismissal sound that lands once the panel is already gone reads as a stray noise.")]
        [SerializeField] private SoundEvent panelClose;

        [Header("1 — the tap")]
        [Tooltip("A collectable day being TAPPED — today's reward, or a missed one being bought back with an ad. " +
                 "Fires the instant the tap is taken, before the server answers, because the sound is feedback for " +
                 "the finger: waiting for the round trip makes the button feel broken.")]
        [SerializeField] private SoundEvent collectTap;

        [Tooltip("A tap that gives NOTHING back: a day already collected, a future day, a lost one. The tile shakes " +
                 "and stays silent on its own, so without this the refusal is invisible on a phone in a noisy room. " +
                 "Keep it short and dry — this plays on mistakes, and a musical one gets tapped for fun.")]
        [SerializeField] private SoundEvent denyTap;

        [Header("2 — the burst")]
        [Tooltip("The rewards ERUPTING out of the tile. Fires when the pieces actually launch. One per reward, so a " +
                 "day paying chips AND Kash bursts twice, staggered by the flight's own Stagger Between Rewards.")]
        [SerializeField] private SoundEvent rewardBurst;

        [Tooltip("Play the burst only ONCE for a multi-reward payout, on the first one. Turn on if two bursts a " +
                 "sixth of a second apart read as a stutter rather than as two gestures.")]
        [SerializeField] private bool burstOncePerPayout;

        [Header("3 — the landings")]
        [Tooltip("One entry per currency: chips tick, Kash chimes. Matched against the reward id the server paid — " +
                 "\"Chips\", \"Kash\", \"Gems\", a chest or item key — case-insensitively.")]
        [SerializeField]
        private HitSound[] landings =
        {
            new HitSound { rewardId = "Chips" },
            new HitSound { rewardId = "Kash" },
        };

        [Tooltip("Used for any reward with no entry above. Leave empty to let unlisted rewards land silently.")]
        [SerializeField] private SoundEvent defaultLanding;

        [Header("Mix")]
        [Tooltip("How many landings may ring at once. Sonity keys a voice on (event, OWNER) and allows ONE per key, " +
                 "so every hit needs a different owner or each cuts the last — this is how many rotating owners are " +
                 "kept. Around the number of pieces in a burst; too few and fast hits clip each other, too many and " +
                 "a long tail piles up into mush.")]
        [Range(1, 32)][SerializeField] private int landingVoices = 12;

        [Tooltip("Log every landing: whether the event arrived, which SoundEvent matched, which voice it played on, " +
                 "and whether Sonity's SoundManager is present. Off by default — turn it on for one run when a " +
                 "landing sound goes missing, because every step of this chain fails silently and the symptom is " +
                 "identical whichever one broke.")]
        [SerializeField] private bool logLandings;

        [Serializable]
        public sealed class HitSound
        {
            [Tooltip("The reward id, exactly as the server pays it: Chips / Coins / Gems / Kash, a chest or item key.")]
            public string rewardId;
            public SoundEvent sound;
        }

        // Rotating owners. Sonity treats (SoundEvent, owner Transform) as ONE voice and re-triggering it STOPS the
        // playing instance — which is why TableAudio gives each gathered chip its own transform rather than sharing
        // its own. The flight's pieces are pooled and already returned by the time a landing is reported, so there is
        // no per-piece transform to borrow: these stand in for them. A poly group cannot fix this — it only ever
        // LOWERS the limit.
        private Transform[] _voices;
        private int _voice;

        private bool _burstPlayedThisPayout;

        private void Awake()
        {
            if (screen == null) screen = GetComponentInChildren<DailyScreen>(true);
            if (panel == null) panel = GetComponentInChildren<DailyPanel>(true);
        }

        private void OnEnable()
        {
            if (screen != null) screen.TileTapped += OnTileTapped;
            if (panel != null)
            {
                panel.Opened += OnPanelOpened;
                panel.Closing += OnPanelClosing;
            }

            // One checkbox lights the whole chain: the flight's own reporting as well as this bank's.
            if (logLandings) RewardFlyTarget.LogPieces = true;

            RewardFlyTarget.BurstStarted += OnBurstStarted;
            RewardFlyTarget.BurstProgress += OnPieceLanded;
            RewardFlyTarget.BurstEnded += OnBurstEnded;
        }

        private void OnDisable()
        {
            if (screen != null) screen.TileTapped -= OnTileTapped;
            if (panel != null)
            {
                panel.Opened -= OnPanelOpened;
                panel.Closing -= OnPanelClosing;
            }

            RewardFlyTarget.BurstStarted -= OnBurstStarted;
            RewardFlyTarget.BurstProgress -= OnPieceLanded;
            RewardFlyTarget.BurstEnded -= OnBurstEnded;
        }

        // ---- 0. the panel ----

        private void OnPanelOpened()
        {
            if (panelOpen != null) panelOpen.UIPlay();
        }

        // UIPlay, deliberately: its owner is the SoundManager, not this object. A sound owned by THIS transform would
        // be cut the moment the close tween finishes and deactivates the panel — the same trap that makes a
        // self-disabling button silence its own click.
        private void OnPanelClosing()
        {
            if (panelClose != null) panelClose.UIPlay();
        }

        // ---- 1. the tap ----

        private void OnTileTapped(DailyItemState state, bool collecting)
        {
            if (!collecting)
            {
                if (denyTap != null) denyTap.UIPlay();
                return;
            }

            _burstPlayedThisPayout = false;   // a fresh collect: the next burst may speak again
            if (collectTap != null) collectTap.UIPlay();
        }

        // ---- 2. the burst ----

        private void OnBurstStarted(string rewardId, int pieces)
        {
            if (rewardBurst == null) return;
            if (burstOncePerPayout && _burstPlayedThisPayout) return;

            _burstPlayedThisPayout = true;
            rewardBurst.UIPlay();
        }

        // ---- 3. the landings ----

        private void OnPieceLanded(string rewardId, float progress01)
        {
            var sound = LandingFor(rewardId);
            if (sound == null)
            {
                if (logLandings) Debug.Log($"[DailyAudio] landing '{rewardId}': NO SOUND MATCHED (check the Landings rows)", this);
                WarnNoLanding(rewardId);
                return;
            }

            // A DIFFERENT owner per hit. Sharing one would make each chip silence the one before it, which sounds
            // like a single stuttering tick instead of a stream of coins.
            var voice = NextVoice();

            if (logLandings)
            {
                bool managerAlive = Sonity.SoundManager.Instance != null;
                Debug.Log($"[DailyAudio] landing '{rewardId}' p={progress01:0.00} sound={sound.name} " +
                          $"voice={(voice != null ? voice.name : "NULL")} " +
                          $"voiceActive={(voice != null && voice.gameObject.activeInHierarchy)} " +
                          $"soundManager={(managerAlive ? "yes" : "MISSING")}", this);
            }

            sound.Play(voice);
        }

        private void OnBurstEnded(string rewardId)
        {
            // Nothing to stop — every landing is a one-shot. The hook exists so a tail/finish sting has an obvious
            // home when one is authored.
        }

        /// <summary>
        /// Say WHY a landing was silent — once per reward id, not once per piece.
        ///
        /// This is the failure that is otherwise invisible: the flight runs, the counter punches, the balance ticks
        /// up, and the only thing missing is a sound nobody can prove was ever asked for. An id that doesn't match the
        /// row ("Chip" vs "Chips") looks exactly like an unassigned SoundEvent, which looks exactly like a broken mix.
        /// </summary>
        private void WarnNoLanding(string rewardId)
        {
            if (_warned == null) _warned = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_warned.Add(rewardId ?? string.Empty)) return;

            Debug.LogWarning($"{name}: no landing sound for reward '{rewardId}'. Add a row to Landings with that exact " +
                             "id and a SoundEvent (the id is the wallet's currency name — Chips / Kash / Gems), or set " +
                             "Default Landing. Rows with an id but no SoundEvent count as unassigned.", this);
        }

        private System.Collections.Generic.HashSet<string> _warned;

        private SoundEvent LandingFor(string rewardId)
        {
            if (landings != null && !string.IsNullOrWhiteSpace(rewardId))
            {
                foreach (var entry in landings)
                    if (entry != null && entry.sound != null &&
                        string.Equals(entry.rewardId, rewardId, StringComparison.OrdinalIgnoreCase))
                        return entry.sound;
            }
            return defaultLanding;
        }

        /// <summary>
        /// The next rotating owner, created on demand and parked ON THE LISTENER.
        ///
        /// The position matters even though these are UI sounds. This component lives on a Canvas, whose world
        /// coordinates run to hundreds of units — so a landing event authored with a 3D SoundContainer would play
        /// somewhere out in the world and attenuate to nothing, with a cheerful "Play" in the log and silence in the
        /// headphones. Sitting on the listener, a 3D container is centred and full volume, and a 2D one ignores the
        /// position entirely. Either way it is audible, which is the only outcome worth defending.
        /// </summary>
        private Transform NextVoice()
        {
            if (_voices == null || _voices.Length != landingVoices)
            {
                var old = _voices;
                _voices = new Transform[Mathf.Max(1, landingVoices)];
                for (int i = 0; i < _voices.Length; i++)
                {
                    if (old != null && i < old.Length && old[i] != null) { _voices[i] = old[i]; continue; }
                    var go = new GameObject($"Voice_{i}");
                    go.transform.SetParent(transform, false);
                    _voices[i] = go.transform;
                }
            }

            _voice = (_voice + 1) % _voices.Length;
            var voice = _voices[_voice] != null ? _voices[_voice] : transform;

            if (_listener == null) _listener = FindAnyObjectByType<AudioListener>();
            if (_listener != null) voice.position = _listener.transform.position;

            return voice;
        }

        private AudioListener _listener;
    }
}
