namespace Khela.Common.Profiles
{
    /// <summary>
    /// A linkable account provider — an OAuth login, a platform game-service, or a display-only social
    /// link. Persisted as int AND sent to the client (badge rendering): append-only, never renumber.
    /// </summary>
    public enum LinkedAccountProvider
    {
        // OAuth / login providers
        Facebook        = 0,
        Google          = 1,
        Apple           = 2,

        // Platform game services
        GooglePlayGames = 3,
        GameCenter      = 4,

        // Social / display-only (vanity) links
        Instagram       = 5,
        TikTok          = 6,
        X               = 7,  // Twitter / X
        YouTube         = 8,
        Discord         = 9,
        Telegram        = 10
    }
}
