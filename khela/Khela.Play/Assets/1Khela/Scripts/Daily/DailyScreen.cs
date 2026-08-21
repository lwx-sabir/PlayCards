using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Daily;
using Khela.Common.Rewards;
using PlayCard.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Daily
{
    /// <summary>
    /// Binds the daily login popup to a server snapshot: fills the four week-lists with day tiles, keeps the countdown
    /// ticking, and turns a tap into an intent (collect / watch an ad).
    ///
    /// It renders ONLY what the server said. Which day is claimable, which are missed and how long until the next one
    /// are decisions already made in <c>DailyService</c> — nothing here recomputes them from the device clock, which is
    /// what keeps a changed phone date from unlocking anything.
    ///
    /// Fetching is not this class's job: hand it a <see cref="DailyStateDto"/> via <see cref="Render"/> and subscribe
    /// to <see cref="ClaimRequested"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DailyScreen : MonoBehaviour
    {
        [Header("Week lists — filled in order, 7 days each")]
        [Tooltip("List_1..List_4 under Group_Reward. Tiles are spawned into these left to right, so the FIRST list " +
                 "holds days 1-7. A ladder longer than these lists can hold simply spills off the end, which is a " +
                 "config mistake worth seeing rather than hiding.")]
        [SerializeField] private RectTransform[] weekLists;

        [Tooltip("How many days each list holds. 7 unless the art changes.")]
        [SerializeField] private int daysPerList = 7;

        [Header("Tile prefabs")]
        [Tooltip("The everyday tile.")]
        [SerializeField] private DailyItemView itemPrefab;
        [Tooltip("The bigger tile for the last day of a week — used on any day the SERVER flagged as a milestone " +
                 "(7 / 14 / 21 / 28 in the built-in ladder). Leave empty to use the everyday tile throughout.")]
        [SerializeField] private DailyItemView milestonePrefab;

        [Header("Header")]
        [SerializeField] private TMP_Text titleText;
        [Tooltip("Optional \"Day 3 of 28\" line.")]
        [SerializeField] private TMP_Text progressText;
        [Tooltip("{0} = current day, {1} = days in the run.")]
        [SerializeField] private string progressFormat = "Day {0} of {1}";
        [Tooltip("Optional \"next reward in 5h 46m\" countdown.")]
        [SerializeField] private TMP_Text nextDayText;
        [Tooltip("Optional label on each tile. {0} = the day number.")]
        [SerializeField] private string dayFormat = "Day {0}";
        [SerializeField] private Button closeButton;

        /// <summary>A day the player wants. <c>useAds</c> means they're spending rewarded-ad credits on a missed day.</summary>
        public event Action<int, bool> ClaimRequested;

        /// <summary>
        /// A collect just STARTED — raised on the tap, before the server has answered, with the day's advertised
        /// rewards and the tile they came out of. This is what the fly animation hangs off: waiting for the response
        /// would mean the burst fires seconds after the finger, which reads as a broken button.
        ///
        /// The amounts are the ADVERTISED ones. They're what the server is about to pay for every fixed reward; a
        /// chest is the one thing rolled server-side, and the wallet refresh reconciles the number either way.
        /// </summary>
        public event Action<int, IReadOnlyList<RewardGrant>, RectTransform> CollectStarted;

        /// <summary>The player tapped Close.</summary>
        public event Action CloseRequested;

        /// <summary>
        /// A tile was TAPPED: the state it was in, and whether that tap does anything. Raised for every tap, including
        /// the refused ones — a tap on a finished or future day still has to be answered, and a sound bank is the only
        /// thing that can answer it (the tile's own shake is silent by design).
        ///
        /// Fired before anything is decided, so a listener hears the finger rather than the outcome.
        /// </summary>
        public event Action<DailyItemState, bool> TileTapped;

        /// <summary>The tile the player last tried to collect — where the collect juice should burst FROM. Captured on
        /// tap, because the rewards arrive after a round trip by which time the ladder may have re-rendered.</summary>
        public RectTransform LastClaimSource { get; private set; }

        private readonly List<DailyItemView> _tiles = new List<DailyItemView>();
        private readonly List<DailyItemView> _prefabs = new List<DailyItemView>();   // what each tile was spawned from

        private DailyStateDto _state;
        private Coroutine _ticker;
        private string _builtCycle;

        private void Awake()
        {
            if (closeButton != null) closeButton.onClick.AddListener(() => CloseRequested?.Invoke());
        }

        private void OnDisable()
        {
            if (_ticker != null) { StopCoroutine(_ticker); _ticker = null; }
        }

        /// <summary>Show the popup with a snapshot. Safe to call again with a fresh one after a claim.</summary>
        public void Render(DailyStateDto state)
        {
            // Rendering a PREFAB ASSET instead of an instance spawns tiles into the asset's own transforms and
            // silently draws nothing. Fail once, loudly, instead of a wall of Unity warnings.
            if (!gameObject.scene.IsValid())
            {
                Debug.LogError($"{name}: DailyScreen.Render was called on a PREFAB ASSET. Instantiate the daily prefab " +
                               "first (DailyButton does this) — a prefab asset can't be shown.", this);
                return;
            }

            _state = state;
            gameObject.SetActive(true);

            if (state == null || !state.Active)
            {
                Clear();
                if (titleText != null) titleText.text = string.Empty;
                if (progressText != null) progressText.text = string.Empty;
                if (nextDayText != null) nextDayText.text = string.Empty;
                return;
            }

            BuildHeader(state);
            BuildLadder(state);

            if (_ticker != null) StopCoroutine(_ticker);
            if (isActiveAndEnabled) _ticker = StartCoroutine(Tick());   // a disabled object can't host the countdown
        }

        public void Hide() => gameObject.SetActive(false);

        /// <summary>Flip one day to Collected without rebuilding — keeps any tween on the other tiles alive.</summary>
        public void MarkClaimed(int day)
        {
            var node = _state?.Nodes?.FirstOrDefault(n => n.Index == day);
            if (node != null)
            {
                node.Claimed = true;
                node.ClaimableNow = false;
                node.AdUnlockable = false;
            }

            var tile = _tiles.FirstOrDefault(t => t != null && t.Day == day);
            if (tile != null) tile.SetState(DailyItemState.Collected);
        }

        // ---------------- header ----------------

        private void BuildHeader(DailyStateDto state)
        {
            if (titleText != null) titleText.text = string.IsNullOrEmpty(state.Title) ? "Daily Rewards" : state.Title;

            if (progressText != null)
                progressText.text = state.Days > 0 ? SafeFormat(progressFormat, state.DayIndex, state.Days) : string.Empty;
        }

        private IEnumerator Tick()
        {
            var wait = new WaitForSeconds(1f);
            while (_state != null && _state.Active)
            {
                if (nextDayText != null) nextDayText.text = FormatSpan(_state.NextDayUtc - DateTime.UtcNow);
                yield return wait;
            }
        }

        // ---------------- ladder ----------------

        private void BuildLadder(DailyStateDto state)
        {
            if (state.Nodes == null) { Clear(); return; }

            // Say it ONCE. Complaining per tile turns one wiring mistake into 28 identical stack traces, which buries
            // whatever else the console was trying to tell you.
            int capacity = (weekLists?.Length ?? 0) * Mathf.Max(1, daysPerList);
            if (capacity < state.Nodes.Count)
            {
                Debug.LogWarning($"{name}: the ladder is {state.Nodes.Count} days but the week lists hold {capacity} " +
                                 $"({weekLists?.Length ?? 0} list(s) × {daysPerList}). Assign Week Lists on DailyScreen — " +
                                 "List_1..List_4 under Group_Reward, in order. Days past the end aren't drawn.", this);
            }

            // A refresh that only changed a day or two updates in place. Rebuilding would destroy and re-instantiate
            // every tile in the frame a collect animation is starting, and that hitch is visible.
            if (CanUpdateInPlace(state)) { UpdateLadder(state); return; }

            Clear();
            foreach (var node in state.Nodes.OrderBy(n => n.Index)) SpawnTile(node, state);
            _builtCycle = state.CycleKey;
        }

        private bool CanUpdateInPlace(DailyStateDto state)
            => _tiles.Count == state.Nodes.Count
            && string.Equals(_builtCycle, state.CycleKey, StringComparison.Ordinal);

        private void UpdateLadder(DailyStateDto state)
        {
            var nodes = state.Nodes.OrderBy(n => n.Index).ToList();
            for (int i = 0; i < nodes.Count && i < _tiles.Count; i++)
            {
                var tile = _tiles[i];
                if (tile == null) continue;

                var node = nodes[i];
                tile.Bind(node.Index, SafeFormat(dayFormat, node.Index, state.Days), node.Text,
                          StateOf(node, state), state.AdsPerUnlock);
                LoadArt(tile, node);
            }
        }

        /// <summary>
        /// Which list a day belongs in: day 1-7 → list 1, 8-14 → list 2, and so on. The lists are the WEEKS, so this is
        /// simple division rather than anything the server has to send.
        /// </summary>
        private RectTransform ListFor(int dayIndex)
        {
            if (weekLists == null || weekLists.Length == 0) return null;
            int per = Mathf.Max(1, daysPerList);
            int list = (dayIndex - 1) / per;
            return list >= 0 && list < weekLists.Length ? weekLists[list] : null;
        }

        private void SpawnTile(DailyNodeDto node, DailyStateDto state)
        {
            var list = ListFor(node.Index);
            if (list == null) return;   // already reported once in BuildLadder

            var prefab = node.IsMilestone && milestonePrefab != null ? milestonePrefab : itemPrefab;
            if (prefab == null) return;

            var tile = Instantiate(prefab, list);
            tile.name = $"Day_{node.Index}";
            tile.Bind(node.Index, SafeFormat(dayFormat, node.Index, state.Days), node.Text,
                      StateOf(node, state), state.AdsPerUnlock);
            tile.Clicked += OnTileClicked;

            LoadArt(tile, node);
            _tiles.Add(tile);
            _prefabs.Add(prefab);
        }

        /// <summary>
        /// The tile's state, from the server's own flags.
        ///
        /// "Tomorrow" is the only one derived here, and it is deliberately the day AFTER the highest reachable one —
        /// the next thing that opens, not the next unclaimed thing. A player who missed days 2 and 3 should still see
        /// tomorrow's tile as tomorrow's, with the missed ones shown as missed.
        /// </summary>
        private DailyItemState StateOf(DailyNodeDto node, DailyStateDto state)
        {
            // A claim still in flight OUTRANKS the server's snapshot.
            //
            // Claims are queued and the server is seconds per call, so a player who taps ten days has nine requests
            // still waiting when the next refresh arrives — and that refresh is honest: the server has not seen them
            // yet. Rendering it verbatim un-collects nine tiles the player already collected, and they come back a
            // minute later one at a time, which reads as the taps having been thrown away. They were not: _pending is
            // exactly the set that is on its way, and it is emptied by ConfirmCollect or RevertCollect either way.
            if (_pending.ContainsKey(node.Index)) return DailyItemState.Collected;

            if (node.Claimed) return DailyItemState.Collected;
            if (node.ClaimableNow) return DailyItemState.Focused;
            if (node.AdUnlockable) return DailyItemState.AdUnlockable;
            if (node.Index == state.MaxNode + 1) return DailyItemState.Tomorrow;
            return DailyItemState.Default;
        }

        private void OnTileClicked(DailyItemView tile)
        {
            LastClaimSource = (RectTransform)tile.transform;

            // Both actionable states count as "collecting": an ad-unlock tap is still the player reaching for the
            // reward, and it should sound like it — the refusal sound belongs to a day that gives nothing back.
            bool collecting = tile.State == DailyItemState.Focused || tile.State == DailyItemState.AdUnlockable;
            TileTapped?.Invoke(tile.State, collecting);

            switch (tile.State)
            {
                case DailyItemState.Focused:
                    BeginCollect(tile);
                    ClaimRequested?.Invoke(tile.Day, false);
                    break;
                case DailyItemState.AdUnlockable:
                    ClaimRequested?.Invoke(tile.Day, true);   // the ad has to play first; nothing is optimistic here
                    break;
                case DailyItemState.Collected:
                    tile.PlayAlreadyCollected();   // acknowledge the tap instead of doing nothing
                    break;
                default:
                    tile.PlayDenied();             // a future or lost day — the tap must not feel ignored
                    break;
            }
        }

        // ---------------- optimistic collect ----------------

        /// <summary>
        /// Commit to the collect on the TAP, not on the response.
        ///
        /// The server is the authority on whether this pays, but it is not the authority on whether the button felt
        /// pressed. A remote database can make a claim take seconds; a tile that sits there unchanged for that long
        /// reads as a dead button and gets tapped again. So the tile flips to Collected immediately, the reward flies
        /// immediately, and <see cref="RevertCollect"/> puts it all back if the server says no.
        ///
        /// Safe because nothing here touches money: the wallet is server-authoritative and the balance HUD reconciles
        /// from the refresh that follows either way. The worst case on a refusal is a tile that flipped and flipped
        /// back, which is what the rollback is for.
        /// </summary>
        private void BeginCollect(DailyItemView tile)
        {
            var node = _state?.Nodes?.FirstOrDefault(n => n.Index == tile.Day);

            _pending[tile.Day] = tile.State;
            tile.PlayClaimed();
            tile.SetState(DailyItemState.Collected);

            if (node?.Rewards == null || node.Rewards.Count == 0)
            {
                // The day pays nothing the client can see — XP-only, or a ladder edited to empty.
                Debug.LogWarning($"{name}: day {tile.Day} has no visible rewards, so nothing flies.", this);
                return;
            }

            // Say so when the collect goes nowhere. A tile that flips to Collected with no burst looks like the fly is
            // broken, when the usual cause is simply that no DailyRewardFlyBinder is listening — and an unsubscribed
            // event is the one failure that cannot report itself from the other end.
            if (CollectStarted == null)
            {
                if (!_warnedNoFlyBinder)
                {
                    _warnedNoFlyBinder = true;
                    Debug.LogWarning($"{name}: a day was collected but nothing is listening for the reward fly. Add a " +
                                     "DailyRewardFlyBinder to this panel (with a RewardFly on it) — the collect works, " +
                                     "it just has no animation.", this);
                }
                return;
            }

            CollectStarted.Invoke(tile.Day, node.Rewards, (RectTransform)tile.transform);
        }

        /// <summary>The server refused a collect we already showed. Put the tile back exactly as it was.</summary>
        public void RevertCollect(int day)
        {
            if (!_pending.TryGetValue(day, out var previous)) return;
            _pending.Remove(day);

            var tile = _tiles.FirstOrDefault(t => t != null && t.Day == day);
            if (tile != null) { tile.SetState(previous); tile.PlayDenied(); }
        }

        /// <summary>The server confirmed it. Nothing to draw — the tile is already collected — just stop tracking it.</summary>
        public void ConfirmCollect(int day) => _pending.Remove(day);

        private readonly Dictionary<int, DailyItemState> _pending = new Dictionary<int, DailyItemState>();
        private bool _warnedNoFlyBinder;

        // ---------------- artwork ----------------

        /// <summary>
        /// Server art overrides the prefab's. Downloads are async and the tile may be gone by the time one lands (a
        /// rebuild, a closed popup), so the callback re-checks the tile is still alive and still on the same day.
        /// XP is skipped: it has no icon in this design and would take the slot from the thing that does.
        /// </summary>
        private void LoadArt(DailyItemView tile, DailyNodeDto node)
        {
            var line = node.Rewards?.FirstOrDefault(r => r != null && r.Kind != RewardKind.Xp
                                                      && r.Images != null && r.Images.Count > 0);
            if (line == null) return;

            int day = tile.Day;
            RemoteImage.Load(line.Images[0], sprite =>
            {
                if (sprite == null || tile == null || tile.Day != day) return;
                tile.SetIcon(sprite);
            });
        }

        // ---------------- plumbing ----------------

        /// <summary>
        /// Empty the week lists. Everything in them is ours to remove — including anything left over from the mockup,
        /// which would otherwise offset every day.
        ///
        /// Children are DETACHED before Destroy: Unity defers the actual destruction to end of frame, so a layout group
        /// would still count them while the new tiles are being added.
        /// </summary>
        private void Clear()
        {
            _tiles.Clear();
            _prefabs.Clear();
            _builtCycle = null;

            if (weekLists == null) return;
            foreach (var list in weekLists)
            {
                if (list == null) continue;
                for (int i = list.childCount - 1; i >= 0; i--)
                {
                    var child = list.GetChild(i);
                    child.SetParent(null, false);
                    Destroy(child.gameObject);
                }
            }
        }

        /// <summary>A bad format string in the inspector must not throw every time the ladder renders.</summary>
        private static string SafeFormat(string format, int a, int b)
        {
            if (string.IsNullOrEmpty(format)) return a.ToString();
            try { return string.Format(format, a, b); }
            catch (FormatException) { return a.ToString(); }
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{span.Minutes}m {span.Seconds}s";
        }
    }
}
