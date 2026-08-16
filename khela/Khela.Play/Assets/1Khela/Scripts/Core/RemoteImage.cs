using System;
using System.Collections;
using System.Collections.Generic;
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
    /// Caches by url for the session and de-duplicates in-flight requests — a 31-day ladder asking for the same chip
    /// icon thirty times downloads it once.
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
            using var request = UnityWebRequestTexture.GetTexture(key);
            yield return request.SendWebRequest();

            Sprite sprite = null;
            if (request.result == UnityWebRequest.Result.Success)
            {
                var texture = DownloadHandlerTexture.GetContent(request);
                if (texture != null)
                {
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                    sprite.name = key;
                    Cache[key] = sprite;
                }
            }

            if (sprite == null)
            {
                Failed.Add(key);
                Debug.LogWarning($"RemoteImage: could not load '{key}' ({request.error}) — keeping the prefab's art.");
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

        /// <summary>Drop the session cache (a config change, or memory pressure on a long session).</summary>
        public static void Clear()
        {
            Cache.Clear();
            Failed.Clear();
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
