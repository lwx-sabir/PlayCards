using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Khela.Game.Services.Storage
{
    /// <summary>One stored object as the admin browses it.</summary>
    public sealed class StoredObject
    {
        /// <summary>The key — the path inside the bucket, e.g. <c>shop/chips_04.png</c>. This is what a product stores.</summary>
        public string Key { get; set; }
        public long Size { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        /// <summary>The absolute url a client fetches it from.</summary>
        public string Url { get; set; }
    }

    /// <summary>
    /// Where the game's binary assets live — shop art first, then anything else the admin names by url (reward icons,
    /// avatar pieces, promo images).
    ///
    /// The catalog and every other document store a KEY, never an absolute url, and this turns a key into the url a
    /// client fetches. That indirection is the point: the bucket, the CDN domain, even the provider can change and not
    /// one product row has to be edited. It also keeps the admin honest — an admin pasting a random third-party url
    /// into a product is how a shop ends up with art nobody controls.
    ///
    /// Two implementations: <see cref="R2ObjectStorage"/> for real deployments, and <see cref="LocalObjectStorage"/> so
    /// a developer with no credentials still has a working shop.
    /// </summary>
    public interface IObjectStorage
    {
        /// <summary>Which backing store is live — shown in the admin so nobody wonders where an upload went.</summary>
        string ProviderName { get; }

        /// <summary>True when this store is configured enough to accept an upload.</summary>
        bool CanWrite { get; }

        /// <summary>The absolute url for a key, or null if the key is empty. Already-absolute urls pass through unchanged,
        /// so a product carrying a legacy full url keeps working.</summary>
        string UrlFor(string key);

        /// <summary>Store bytes under <paramref name="key"/>, replacing anything already there. Returns the public url.</summary>
        Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);

        Task DeleteAsync(string key, CancellationToken ct = default);

        /// <summary>Everything under a prefix, newest first. <paramref name="max"/> caps a listing the admin has to render.</summary>
        Task<List<StoredObject>> ListAsync(string prefix, int max = 500, CancellationToken ct = default);
    }
}
