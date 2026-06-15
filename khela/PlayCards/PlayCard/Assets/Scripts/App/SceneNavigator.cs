using UnityEngine.SceneManagement;

namespace PlayCard.App
{
    /// <summary>
    /// Central scene transitions for the Home → Lobby → Table flow. All three scene names must be
    /// added to File ▸ Build Settings ▸ Scenes In Build.
    /// </summary>
    public static class SceneNavigator
    {
        public const string Home = "Home";
        public const string Lobby = "Lobby";
        public const string Table = "Table";

        public static void GoToHome() => SceneManager.LoadScene(Home);
        public static void GoToLobby() => SceneManager.LoadScene(Lobby);

        /// <summary>Open a specific table — stashes its id for TableController to pick up after load.</summary>
        public static void GoToTable(string tableId)
        {
            GameSession.TableId = tableId;
            SceneManager.LoadScene(Table);
        }
    }
}
