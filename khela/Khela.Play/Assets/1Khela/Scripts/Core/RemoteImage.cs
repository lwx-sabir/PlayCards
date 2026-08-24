using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayCard.Core
{
    /// <summary>
    /// Downloads artwork the SERVER named (reward image urls, shop art, anything data-driven) and hands back a Sprite.
    ///
    /// Server art is always an OVERRIDE: a view keeps whatever the artist put in the prefab until a sprite actually
    /// arrives, so a missing url, a dead CDN or an offline player degrades to the designed look instead of a blank card.
    /// Failures therefore call back with <c>null</c> rather than throwing.
    ///
    /// Caches by url in MEMORY for the session, on DISK across sessions, and de-duplicates in-flight requests — a 31-day
    /// ladder asking for the same chip icon thirty times downloads it once, and the next app start downloads it not at all.
    /// </summary>
    public static class RemoteImage
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, List<Action<Sprite>>> Pending = new Dictionary<string, List<Action<Sprite>>>();
        private static readonly HashSet<string> Failed = new HashSet<string>();

        private static Runner _runner;

        /// <summary>A sprite already in the cache, or null. Use it to fill instantly and avoid a frame of the old art.</summary>
        public static bool TryGetCached(string url, out Sprite sprite)
        {
            sprite = null;
            var key = Resolve(url);
            return key != null && Cache.TryGetValue(key, out sprite);
        }

        /// <summary>
        /// Fetch <paramref name="url"/> and call back with the sprite, or with null if there's nothing usable —
        /// blank url, bad response, or a url that already failed this session (retried never, so a broken link can't
        /// hammer the server once per card).
        /// </summary>
        public static void Load(string url, Action<Sprite> onLoaded)
        {
            if (onLoaded == null) return;

            var key = Resolve(url);
            if (key == null || Failed.Contains(key)) { onLoaded(null); return; }

            if (Cache.TryGetValue(key, out var cached)) { onLoaded(cached); return; }

            if (Pending.TryGetValue(key, out var waiting)) { waiting.Add(onLoaded); return; }   // already downloading
            Pending[key] = new List<Action<Sprite>> { onLoaded };

            EnsureRunner().StartCoroutine(Download(key));
        }

        /// <summary>Absolute urls pass through; anything else is treated as a path on our own backend, so the admin
        /// can enter "Icons/chip.png" and it follows whatever server the build points at.</summary>
        public static string Resolve(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;

            var baseUrl = AppConfig.Instance != null ? AppConfig.Instance.BaseApiUrl : null;
            if (string.IsNullOrEmpty(baseUrl)) return null;
            return baseUrl + "/" + url.TrimStart('/');
        }

        private static IEnumerator Download(string key)
        {
            Sprite sprite = null;

            // DISK FIRST. Store art is large and it does not change often, so paying for it once per device beats paying
            // for it once per app start — and a player opening the shop offline still sees the art they have seen before.
            var file = DiskPath(key);
            if (file != null && File.Exists(file) && !Expired(file))
            {
                sprite = FromBytes(ReadOrNull(file), key);
                if (sprite != null) Cache[key] = sprite;
            }

            if (sprite == null)
            {
                // Get, not GetTexture: this hands back the original bytes, which is what gets written to disk. Decoding
                // from a texture would re-encode and cost quality and size for nothing.
                using var request = UnityWebRequest.Get(key);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var bytes = request.downloadHandler?.data;
                    sprite = FromBytes(bytes, key);
                    if (sprite != null)
                    {
                        Cache[key] = sprite;
                        WriteQuietly(file, bytes);
                    }
                }

                if (sprite == null)
                {
                    Failed.Add(key);
                    Debug.LogWarning($"RemoteImage: could not load '{key}' ({request.error}) — keeping the prefab's art.");
                }
            }

            if (Pending.TryGetValue(key, out var callbacks))
            {
                Pending.Remove(key);
                // A card may have been destroyed mid-download; the callback is the caller's to make safe.
                for (int i = 0; i < callbacks.Count; i++)
                {
                    try { callbacks[i]?.Invoke(sprite); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
        }

        /// <summary>Drop the session cache (a config change, or memory pressure on a long session). Leaves the disk copy.</summary>
        public static void Clear()
        {
            Cache.Clear();
            Failed.Clear();
        }

        /// <summary>Delete the on-disk copies too — the "my art is stale" button, and what a cache-size purge would call.</summary>
        public static void ClearDisk()
        {
            Clear();
            try { if (Directory.Exists(DiskDir)) Directory.Delete(DiskDir, true); }
            catch (Exception ex) { Debug.LogWarning($"RemoteImage: could not clear the disk cache: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- disk

        private static string DiskDir => Path.Combine(Application.persistentDataPath, "remoteimg");

        /// <summary>
        /// Where one url's bytes live. Named by a hash of the url, not by the url: a url has characters no file system
        /// accepts, and two products can name the same file in different folders.
        /// </summary>
        private static string DiskPath(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try { return Path.Combine(DiskDir, Hash(key) + ".img"); }
            catch { return null; }
        }

        /// <summary>
        /// Days a cached file is trusted before being re-fetched. <b>0 = never expire</b>, which is the default, because the
        /// SERVER says when art is stale: the store catalog carries an images version that moves only when an image url
        /// changes, and StoreCatalog drops this cache when it does. A timer would only re-download art nobody replaced.
        /// Raise it above 0 for art that arrives from somewhere with no such signal.
        /// </summary>
        public static int MaxAgeDays = 0;

        private static bool Expired(string file)
        {
            if (MaxAgeDays <= 0) return false;
            try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(MaxAgeDays); }
            catch { return true; }
        }

        private static byte[] ReadOrNull(string file)
        {
            try { return File.ReadAllBytes(file); }
            catch { return null; }
        }

        private static void WriteQuietly(string file, byte[] bytes)
        {
            if (file == null || bytes == null || bytes.Length == 0) return;
            try
            {
                Directory.CreateDirectory(DiskDir);
                File.WriteAllBytes(file, bytes);
            }
            // A full disk, a sandboxed path, a platform that will not let us write: the art is already on screen, so a
            // cache that could not be written is not worth failing the load over.
            catch (Exception ex) { Debug.LogWarning($"RemoteImage: could not cache '{file}': {ex.Message}"); }
        }

        private static Sprite FromBytes(byte[] bytes, string key)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!texture.LoadImage(bytes, markNonReadable: true))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            sprite.name = key;
            return sprite;
        }

        /// <summary>FNV-1a over the url. Deliberately not System.Security.Cryptography: this only needs a stable file
        /// name, and hashing here has been a stripping hazard on IL2CPP before.</summary>
        private static string Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            // Length joins the hash so two urls that collide must also be the same length — cheap, and a collision here
            // would mean one product wearing another's picture.
            return h.ToString("x16") + "_" + s.Length.ToString();
        }

        private static Runner EnsureRunner()
        {
            if (_runner != null) return _runner;
            var go = new GameObject("[RemoteImage]") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
            return _runner;
        }

        /// <summary>Coroutine host, so this stays a static API that any scene can call without wiring anything.</summary>
        private sealed class Runner : MonoBehaviour { }
    }
}
