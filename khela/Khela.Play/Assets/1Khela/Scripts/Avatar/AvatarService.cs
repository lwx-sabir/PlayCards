using System.Collections.Generic;
using System.Threading.Tasks;
using Bozo.ModularCharacters;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// The client's single hold on avatar state: the caller's own <see cref="Mine"/> avatar plus a small cache of other
    /// players' avatars (for rendering them at seats). Talks to the server through <see cref="BlackjackRestClient"/> — the
    /// server is the source of truth and sanitizes on write, so what comes back is trusted.
    ///
    /// Also the place that turns an <see cref="AvatarData"/> into a renderable BoZo <see cref="CharacterData"/>: resolve
    /// the premade base from Resources, then overlay the player's diff (<see cref="AvatarMapper"/>).
    /// </summary>
    public sealed class AvatarService
    {
        private static AvatarService _instance;
        public static AvatarService Instance => _instance ??= new AvatarService();

        /// <summary>The caller's avatar (null until <see cref="LoadMineAsync"/> or a save). Kept in sync with the server.</summary>
        public AvatarData Mine { get; private set; }

        /// <summary>Raised whenever <see cref="Mine"/> is (re)assigned — stage/HUD binders re-render off this.</summary>
        public event System.Action MineChanged;

        private readonly Dictionary<string, AvatarData> _others = new Dictionary<string, AvatarData>();

        /// <summary>Seed the cached "mine" from an already-fetched value (e.g. the boot router did the GET) — avoids a
        /// second round-trip. Pass null to record "fetched, has none yet".</summary>
        public void SetMine(AvatarData avatar) { Mine = avatar; MineChanged?.Invoke(); }

        /// <summary>Fetch + cache the caller's avatar. Returns null if the player has none yet.</summary>
        public async Task<AvatarData> LoadMineAsync()
        {
            var r = await BlackjackRestClient.Instance.GetMyAvatarAsync();
            if (r.Ok) { Mine = r.Value; MineChanged?.Invoke(); }
            else Debug.LogWarning($"[AvatarService] load mine failed: {r.Error}");
            return Mine;
        }

        /// <summary>Persist the caller's avatar. Stores the server's SANITIZED echo as the new local truth. Returns success.</summary>
        public async Task<bool> SaveAsync(AvatarData avatar)
        {
            var r = await BlackjackRestClient.Instance.PutMyAvatarAsync(avatar);
            if (r.Ok) { Mine = r.Value; MineChanged?.Invoke(); return true; }
            Debug.LogWarning($"[AvatarService] save failed: {r.Error}");
            return false;
        }

        /// <summary>Fetch another player's avatar (cached). Null if none / blocked.</summary>
        public async Task<AvatarData> LoadUserAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            if (_others.TryGetValue(userId, out var cached)) return cached;
            var r = await BlackjackRestClient.Instance.GetAvatarAsync(userId);
            if (r.Ok) { _others[userId] = r.Value; return r.Value; }
            return null;
        }

        /// <summary>Drop a cached other-player avatar (call when you know theirs changed).</summary>
        public void Invalidate(string userId)
        {
            if (!string.IsNullOrEmpty(userId)) _others.Remove(userId);
        }

        // ---- house dealer ----

        private readonly Dictionary<string, AvatarData> _dealers = new Dictionary<string, AvatarData>();

        /// <summary>Fetch + cache a house table dealer's look: the one with <paramref name="dealerId"/>, or the default
        /// (first) dealer when null/empty. Null if that dealer doesn't exist, or the game wasn't launched from Boot (no
        /// session to authorize the fetch). Cached per id.</summary>
        public async Task<AvatarData> LoadDealerAsync(string dealerId = null)
        {
            string key = dealerId ?? "";
            if (_dealers.TryGetValue(key, out var cached)) return cached;

            var r = await BlackjackRestClient.Instance.GetDealerAsync(dealerId);
            if (!r.Ok) { Debug.LogWarning($"[AvatarService] load dealer '{key}' failed: {r.Error}"); return null; }

            var data = r.Value?.Dealer?.Character;
            _dealers[key] = data;   // cache success (incl. a legitimately-absent dealer = null) to avoid a refetch
            return data;
        }

        // ---- base resolution + build ----

        /// <summary>Load a premade base's <see cref="CharacterData"/> from Resources (e.g. "Base/Mary"). Null if missing.
        /// Returns a COPY, never the shared asset — see below.</summary>
        public static CharacterData LoadBaseData(string baseId)
        {
            if (string.IsNullOrEmpty(baseId)) return null;
            var obj = Resources.Load<CharacterObject>(baseId);
            if (obj == null) { Debug.LogWarning($"[AvatarService] base not found in Resources: {baseId}"); return null; }
            if (obj.data == null) return null;

            // CRITICAL: Resources.Load returns ONE cached CharacterObject; obj.data is its live serialized asset. BoZo's
            // LoadCharacter runs Bozo_SavePatcher.UpdateSave, which MUTATES the CharacterData IN PLACE for older
            // (versionID < 1) bases — 6 of our 9 premade bases are v0 — permanently corrupting the shared asset for the
            // session. So hand every caller a COPY; the shared asset is never touched.
            AvatarMapper.EnsureLists(obj.data);     // guard: copy ctor needs non-null lists (harmless empty-init if any is null)
            return new CharacterData(obj.data);
        }

        /// <summary>Full renderable CharacterData = the avatar's base + the player's overlaid diff. Null if the base is missing.</summary>
        public static CharacterData BuildCharacter(AvatarData a)
        {
            var baseData = LoadBaseData(a?.BaseId);
            return baseData == null ? null : AvatarMapper.ToCharacter(a, baseData);
        }
    }
}
