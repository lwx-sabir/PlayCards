using System;
using System.Collections.Generic;
using System.IO;

namespace Khela.Game.Services.Storage
{
    /// <summary>
    /// Where assets are stored and how they are addressed (<c>Storage:*</c> in appsettings).
    ///
    /// The credentials are secrets and belong in the un-committed appsettings or the environment, never in source.
    /// With none configured the game falls back to local disk, which is why a fresh clone still runs.
    /// </summary>
    public sealed class StorageOptions
    {
        public const string Section = "Storage";

        /// <summary><c>R2</c> or <c>Local</c>. Empty picks R2 when it has credentials, local otherwise.</summary>
        public string Provider { get; set; }

        /// <summary>
        /// The base every key is served from — the R2 custom domain or <c>pub-xxxx.r2.dev</c>, or the game's own
        /// origin when running on local disk. No trailing slash needed.
        /// </summary>
        public string PublicBaseUrl { get; set; }

        /// <summary>Cap on one upload, in megabytes. Shop art that needs more than this wants compressing, not raising.</summary>
        public int MaxUploadMb { get; set; } = 8;

        /// <summary>Extensions the admin may upload. Anything else is refused — this endpoint takes pictures, not files.</summary>
        public List<string> AllowedExtensions { get; set; } = new List<string> { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

        public R2Options R2 { get; set; } = new R2Options();
        public LocalOptions Local { get; set; } = new LocalOptions();

        public sealed class R2Options
        {
            /// <summary>Cloudflare account id — the endpoint is https://{AccountId}.r2.cloudflarestorage.com.</summary>
            public string AccountId { get; set; }
            public string AccessKeyId { get; set; }
            public string SecretAccessKey { get; set; }
            public string Bucket { get; set; }

            public bool IsConfigured =>
                !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(AccessKeyId)
                && !string.IsNullOrWhiteSpace(SecretAccessKey) && !string.IsNullOrWhiteSpace(Bucket);

            public string ServiceUrl => $"https://{AccountId}.r2.cloudflarestorage.com";
        }

        public sealed class LocalOptions
        {
            /// <summary>Folder under the content root that holds the objects. Served read-only by the static middleware.</summary>
            public string Root { get; set; } = Path.Combine("filesystem", "assets");
            /// <summary>The path it is served under, so a key becomes {origin}{RequestPath}/{key}.</summary>
            public string RequestPath { get; set; } = "/assets";
        }
    }
}
