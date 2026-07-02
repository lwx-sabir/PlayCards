using System;
using System.Collections.Generic;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Drives the Leaderboard panel. A board is (game, metric, period, scope): the LEFT tabs pick the game (Overall =
    /// "general", per-game otherwise); the RIGHT tabs pick the scope (global/country/friends) and optionally the period
    /// (daily/weekly/monthly/alltime). Any tab change fetches GET /api/leaderboard and repaints the rows + the caller's
    /// self row. Add a tab = add an array element (no code). Pure VIEW — server-authoritative; the client only renders.
    /// </summary>
    public sealed class LeaderboardBinder : MonoBehaviour
    {
        [Serializable] public struct GameTab    { public Button button; public string game;   public GameObject focus; }
        [Serializable] public struct ScopeTab   { public Button button; public string scope;  public GameObject focus; }
        [Serializable] public struct PeriodTab  { public Button button; public string period; public GameObject focus; }
        [Serializable] public struct AvatarEntry { public string id; public Sprite sprite; }
        [Serializable] public struct FlagEntry   { public string region; public Sprite sprite; }

        [Header("Left panel — game selection")]
        [Tooltip("One per game tab. Overall = game \"general\"; per-game e.g. \"blackjack\" / \"poker\" / \"teenpatti\" / \"roulette\".")]
        [SerializeField] private GameTab[] gameTabs;

        [Header("Right panel — scope / period tabs")]
        [Tooltip("Scope tabs: \"global\" / \"country\" / \"friends\".")]
        [SerializeField] private ScopeTab[] scopeTabs;
        [Tooltip("Optional period tabs: \"daily\" / \"weekly\" / \"monthly\" / \"alltime\". Empty = use Default Period.")]
        [SerializeField] private PeriodTab[] periodTabs;

        [Header("Rows")]
        [Tooltip("Scroll View → Content (a Vertical Layout Group).")]
        [SerializeField] private Transform rowsContainer;
        [SerializeField] private LeaderboardRowView rowPrefab;
        [Tooltip("Optional pinned self row (the highlighted '… (You)' row), bound from the page's Me.")]
        [SerializeField] private LeaderboardRowView selfRow;
        [SerializeField] private GameObject emptyState;     // optional "no entries yet"
        [SerializeField] private TMP_Text messageText;      // optional status/error

        [Header("Defaults")]
        [Tooltip("Overall is XP-only; per-game can also rank by \"biggestwin\" / \"streak\". Add metric tabs like the period tabs if needed.")]
        [SerializeField] private string metric = "xp";
        [SerializeField] private string defaultGame = "general";
        [SerializeField] private string defaultScope = "global";
        [SerializeField] private string defaultPeriod = "weekly";
        [SerializeField, Range(1, 200)] private int top = 50;

        [Header("Icons (optional)")]
        [SerializeField] private AvatarEntry[] avatarIcons;   // AvatarId → sprite
        [SerializeField] private FlagEntry[] flagIcons;       // ISO region → flag sprite

        private readonly List<LeaderboardRowView> _rows = new List<LeaderboardRowView>();
        private string _game, _scope, _period;
        private int _reqId;

        private void OnEnable()
        {
            _game = defaultGame; _scope = defaultScope; _period = defaultPeriod;
            WireTabs(true);
            Refresh();
        }

        private void OnDisable() => WireTabs(false);

        private void WireTabs(bool on)
        {
            if (gameTabs != null)
                foreach (var t in gameTabs)   if (t.button != null) { t.button.onClick.RemoveAllListeners(); if (on) { var g = t.game;   t.button.onClick.AddListener(() => Select(game: g)); } }
            if (scopeTabs != null)
                foreach (var t in scopeTabs)  if (t.button != null) { t.button.onClick.RemoveAllListeners(); if (on) { var s = t.scope;  t.button.onClick.AddListener(() => Select(scope: s)); } }
            if (periodTabs != null)
                foreach (var t in periodTabs) if (t.button != null) { t.button.onClick.RemoveAllListeners(); if (on) { var p = t.period; t.button.onClick.AddListener(() => Select(period: p)); } }
        }

        private void Select(string game = null, string scope = null, string period = null)
        {
            if (game != null) _game = game;
            if (scope != null) _scope = scope;
            if (period != null) _period = period;
            Refresh();
        }

        /// <summary>Re-fetch the current board and repaint. Race-safe — only the latest request renders.</summary>
        public async void Refresh()
        {
            UpdateFocus();
            int req = ++_reqId;
            try
            {
                var res = await BlackjackRestClient.Instance.GetLeaderboardAsync(_game, metric, _period, _scope, top);
                if (req != _reqId) return;   // a newer tab change superseded this fetch
                if (res.Ok && res.Value != null) Render(res.Value);
                else SetText(messageText, "Couldn't load leaderboard");
            }
            catch (Exception e)
            {
                if (req == _reqId) SetText(messageText, "Couldn't load leaderboard");
                Debug.LogWarning($"[LeaderboardBinder] {e.Message}");
            }
        }

        private void Render(LbPageData page)
        {
            SetText(messageText, "");
            for (int i = 0; i < _rows.Count; i++) if (_rows[i] != null) Destroy(_rows[i].gameObject);
            _rows.Clear();

            string myId = page.Me?.UserId;
            if (rowsContainer != null && rowPrefab != null && page.Entries != null)
            {
                foreach (var e in page.Entries)
                {
                    var row = Instantiate(rowPrefab, rowsContainer);
                    row.Bind(e, ResolveAvatar(e.AvatarId), ResolveFlag(e.Region), e.UserId == myId);
                    _rows.Add(row);
                }
            }
            SetActiveSafe(emptyState, page.Entries == null || page.Entries.Count == 0);

            if (selfRow != null)
            {
                bool hasMe = page.Me != null;
                if (selfRow.gameObject.activeSelf != hasMe) selfRow.gameObject.SetActive(hasMe);
                if (hasMe) selfRow.Bind(page.Me, ResolveAvatar(page.Me.AvatarId), ResolveFlag(page.Me.Region), true);
            }
        }

        private void UpdateFocus()
        {
            if (gameTabs != null)   foreach (var t in gameTabs)   SetActiveSafe(t.focus, t.game   == _game);
            if (scopeTabs != null)  foreach (var t in scopeTabs)  SetActiveSafe(t.focus, t.scope  == _scope);
            if (periodTabs != null) foreach (var t in periodTabs) SetActiveSafe(t.focus, t.period == _period);
        }

        private Sprite ResolveAvatar(string id)
        {
            if (avatarIcons != null && !string.IsNullOrEmpty(id))
                for (int i = 0; i < avatarIcons.Length; i++) if (avatarIcons[i].id == id) return avatarIcons[i].sprite;
            return null;
        }

        private Sprite ResolveFlag(string region)
        {
            if (flagIcons != null && !string.IsNullOrEmpty(region))
                for (int i = 0; i < flagIcons.Length; i++)
                    if (string.Equals(flagIcons[i].region, region, StringComparison.OrdinalIgnoreCase)) return flagIcons[i].sprite;
            return null;
        }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
        private static void SetActiveSafe(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }
}
