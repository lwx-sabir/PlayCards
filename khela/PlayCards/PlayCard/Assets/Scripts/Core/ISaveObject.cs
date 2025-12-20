namespace PlayCard.Core
{
    /// <summary>
    /// Implement on any component/service that wants to participate in local save.
    /// Keys must be deterministic/stable across runs.
    /// </summary>
    public interface ISaveObject
    {
        /// <summary>
        /// Unique, deterministic key for this save entry (e.g., "auth", "settings").
        /// </summary>
        string Key { get; }

        /// <summary>
        /// Serialize the current state to a JSON string.
        /// </summary>
        string SaveToJson();

        /// <summary>
        /// Load state from a JSON string previously produced by SaveToJson.
        /// </summary>
        void LoadFromJson(string json);
    }
}
