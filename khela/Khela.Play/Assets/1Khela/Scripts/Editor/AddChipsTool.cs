#if UNITY_EDITOR
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.Game.Wallet;
using UnityEditor;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Dev helper: credit test Chips to the signed-in player so you don't run dry while testing. Calls the
    /// dev-only server endpoint (POST /api/wallet/dev/chips, 404 outside Development) with the player's JWT,
    /// then refreshes the wallet HUD. Play mode only (needs the signed-in player + a running server).
    /// Tools ▸ Khela ▸ Add … Chips.
    /// </summary>
    public static class AddChipsTool
    {
        [MenuItem("Tools/Khela/Add 10,000 Chips")]
        private static void Add10k() => Grant(10000);

        [MenuItem("Tools/Khela/Add 100,000 Chips")]
        private static void Add100k() => Grant(100000);

        private static async void Grant(int amount)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[AddChips] Enter Play mode first — needs the signed-in player + a running server.");
                return;
            }

            var token = AccountManager.Instance != null ? AccountManager.Instance.JwtToken : null;
            if (string.IsNullOrEmpty(token)) { Debug.LogWarning("[AddChips] Not signed in yet."); return; }

            try
            {
                using var http = new HttpClient();
                var url = $"{AppConfig.Instance.BaseApiUrl}/api/wallet/dev/chips?amount={amount}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var resp = await http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    Debug.LogWarning($"[AddChips] server returned {(int)resp.StatusCode}: {body} " +
                                     "(the endpoint only exists when the server runs in Development).");
                    return;
                }

                if (WalletManager.Instance != null) await WalletManager.Instance.RefreshAsync();
                Debug.Log($"[AddChips] +{amount:N0} chips → {body}");
            }
            catch (Exception ex) { Debug.LogError($"[AddChips] {ex.Message}"); }
        }
    }
}
#endif
