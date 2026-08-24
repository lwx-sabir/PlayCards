using System;
using System.Linq;
using System.Text;

namespace Khela.Game.Services.Storage
{
    /// <summary>
    /// Turning what an admin typed into a key that is safe to store and safe to serve.
    ///
    /// A key becomes part of a url and, on the local provider, part of a PATH — so this is the boundary where a
    /// traversal (<c>../../appsettings.json</c>), a backslash, or a name the file system will not accept has to stop.
    /// It is deliberately strict rather than clever: lower case, slash-separated, and a short alphabet.
    /// </summary>
    public static class StorageKeys
    {
        public const int MaxLength = 200;

        /// <summary>
        /// Normalise a key, or return null if nothing usable survives. Rejects traversal outright rather than stripping
        /// it — an admin who typed <c>..</c> meant something, and quietly storing it somewhere else would hide that.
        /// </summary>
        public static string Normalise(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var s = key.Trim().Replace('\\', '/').TrimStart('/');
            if (s.Length == 0 || s.Length > MaxLength) return null;
            if (s.Contains("..")) return null;

            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '/') { if (sb.Length > 0 && sb[sb.Length - 1] != '/') sb.Append('/'); continue; }
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
                if (c == '.' || c == '-' || c == '_') { sb.Append(c); continue; }
                sb.Append('-');   // spaces and punctuation become a separator rather than disappearing
            }

            var result = sb.ToString().Trim('/', '-');
            return result.Length == 0 ? null : result;
        }

        /// <summary>A key for an uploaded file: <c>{folder}/{name}{ext}</c>, with the original name kept where it is usable.</summary>
        public static string ForUpload(string folder, string fileName)
        {
            var ext = System.IO.Path.GetExtension(fileName ?? "").ToLowerInvariant();
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "");
            var name = Normalise(stem) ?? DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var dir = Normalise(folder);
            return Normalise(string.IsNullOrEmpty(dir) ? name + ext : dir + "/" + name + ext);
        }

        /// <summary>The Content-Type for a key, so a browser and the CDN both know what came back.</summary>
        public static string ContentType(string key)
        {
            var ext = System.IO.Path.GetExtension(key ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        /// <summary>True for something already absolute — a legacy product url, or art hosted elsewhere on purpose.</summary>
        public static bool IsAbsolute(string value)
            => !string.IsNullOrWhiteSpace(value)
               && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
