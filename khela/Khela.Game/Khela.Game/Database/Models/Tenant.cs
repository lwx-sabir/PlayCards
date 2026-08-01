namespace Khela.Game.Database.Models
{
    /// <summary>
    /// Multi-tenancy constants. The engine is intended to be LICENSED to operators who run it under their own
    /// gaming licence, so every money movement and every audit row is tagged with the operator it belongs to.
    ///
    /// Khela's own play uses <see cref="Default"/>. Nothing today writes anything else — the point of having the
    /// column now is that adding it later, to a live ledger with a licensee on it, is a far worse job than
    /// carrying a constant for a while.
    /// </summary>
    public static class Tenant
    {
        /// <summary>Khela's own operation — the first-party tenant.</summary>
        public const string Default = "khela";

        /// <summary>Max stored length; keep in step with the [MaxLength] on the tagged columns.</summary>
        public const int MaxLength = 64;
    }
}
