using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using PlayCard.Core;

namespace PlayCard.App
{
    /// <summary>
    /// Central scene transitions for the Home → Lobby → Table flow. All three scene names must be
    /// added to File ▸ Build Settings ▸ Scenes In Build.
    /// </summary>
    public static class SceneNavigator
    {
        public const string Boot = "Boot";
        public const string Onboarding = "Onboarding";
        public const string Home = "Home";
        public const string Lobby = "Lobby";
        public const string Table = "Blackjack_Table";   // renamed from "Table"
        public const string Wardrobe = "Wardrobe";
        public const string World = "DiveBar_01";   // the 3D virtual-world scene (temp: single hardcoded world)

        /// <summary>
        /// Restart the app flow from the beginning: re-authenticate, re-bootstrap, then route on as normal. Used as
        /// the LAST-RESORT auth recovery — a 401 that survived a token refresh and a full re-login means the session
        /// is genuinely dead, and every screen from that point would only render errors. Clears the table/seat so we
        /// can't boot straight back into a table we are no longer authorised for.
        /// </summary>
        public static void GoToBoot()
        {
            KhelaAnalytics.LogScreen(Boot);
            GameSession.TableId = null;
            GameSession.SeatNumber = 0;
            SceneManager.LoadScene(Boot);
        }

        /// <summary>Avatar customization (full edit — shapes/outfits/colours). Re-entrant from Home. Must be in Build Settings.</summary>
        public static void GoToWardrobe()
        {
            KhelaAnalytics.LogScreen(Wardrobe);
            SceneManager.LoadScene(Wardrobe);
        }

        /// <summary>Enter the 3D virtual world (social hub). Temp: hardcoded to the DiveBar scene.</summary>
        public static void GoToWorld()
        {
            KhelaAnalytics.LogScreen(World);
            Addressables.LoadSceneAsync("worlds/divebar_01", LoadSceneMode.Single);
        }

        /// <summary>First-run avatar picker (shown only when the player has no saved avatar). Must be in Build Settings.</summary>
        public static void GoToOnboarding()
        {
            KhelaAnalytics.LogScreen(Onboarding);
            SceneManager.LoadScene(Onboarding);
        }

        public static void GoToHome()
        {
            KhelaAnalytics.LogScreen(Home);
            SceneManager.LoadScene(Home);
        }

        public static void GoToLobby()
        {
            KhelaAnalytics.LogScreen(Lobby);
            KhelaAnalytics.LogLobbyOpened(GameSession.SelectedGame ?? "blackjack");
            SceneManager.LoadScene(Lobby);
        }

        /// <summary>Open a specific table — stashes its id (and the picked seat, if any) for the Table scene to
        /// pick up after load. <paramref name="seatNumber"/> 0 = unknown (auto-match / spectate).</summary>
        public static void GoToTable(string tableId, int seatNumber = 0)
        {
            GameSession.TableId = tableId;
            GameSession.SeatNumber = seatNumber;   // lets the Table scene resolve the local seat before the board
            KhelaAnalytics.LogScreen(Table);
            SceneManager.LoadScene(Table);
        }
    }
}
