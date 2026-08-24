using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Storage
{
    /// <summary>
    /// Assets on this server's own disk, served by the static-files middleware — the fallback when no bucket is
    /// configured, so a fresh clone with no credentials still has a working shop.
    ///
    /// Not for production behind more than one instance: two servers would each hold half the uploads. That is the
    /// whole reason the bucket exists, and the admin says which provider is live so this can never be mistaken for it.
    /// </summary>
    public sealed class LocalObjectStorage : IObjectStorage
    {
        private readonly StorageOptions _options;
        private readonly IWebHostEnvironment _env;
        private readonly IHttpContextAccessor _http;
        private readonly ILogger<LocalObjectStorage> _logger;

        public LocalObjectStorage(IOptions<StorageOptions> options, IWebHostEnvironment env,
            IHttpContextAccessor http, ILogger<LocalObjectStorage> logger)
        {
            _options = options.Value ?? new StorageOptions();
            _env = env; _http = http; _logger = logger;
        }

        public string ProviderName => "Local disk";
        public bool CanWrite => true;

        private string Root => Path.Combine(_env.ContentRootPath, _options.Local.Root);

        public string UrlFor(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (StorageKeys.IsAbsolute(key)) return key;
            var normalised = StorageKeys.Normalise(key);
            if (normalised == null) return null;

            var path = "/" + _options.Local.RequestPath.Trim('/') + "/" + normalised;

            // An explicit base wins; otherwise the url is built from the request, so the same file resolves whether the
            // build points at localhost, the staging box or the live host.
            var baseUrl = (_options.PublicBaseUrl ?? "").TrimEnd('/');
            if (!string.IsNullOrEmpty(baseUrl)) return baseUrl + path;

            var request = _http.HttpContext?.Request;
            return request == null ? path : $"{request.Scheme}://{request.Host}{path}";
        }

        public async Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            var normalised = StorageKeys.Normalise(key) ?? throw new ArgumentException("Unusable storage key.", nameof(key));
            var full = Path.Combine(Root, normalised.Replace('/', Path.DirectorySeparatorChar));

            // Belt and braces on top of Normalise: whatever the key contained, the file must land inside the root.
            var rootFull = Path.GetFullPath(Root);
            if (!Path.GetFullPath(full).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Storage key escapes the asset root.", nameof(key));

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var file = File.Create(full)) await content.CopyToAsync(file, ct);
            _logger.LogInformation("Local storage: wrote {Key}.", normalised);
            return UrlFor(normalised);
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            var normalised = StorageKeys.Normalise(key);
            if (normalised == null) return Task.CompletedTask;
            var full = Path.Combine(Root, normalised.Replace('/', Path.DirectorySeparatorChar));
            var rootFull = Path.GetFullPath(Root);
            if (Path.GetFullPath(full).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                File.Delete(full);
                _logger.LogInformation("Local storage: deleted {Key}.", normalised);
            }
            return Task.CompletedTask;
        }

        public Task<List<StoredObject>> ListAsync(string prefix, int max = 500, CancellationToken ct = default)
        {
            var result = new List<StoredObject>();
            var root = Root;
            if (!Directory.Exists(root)) return Task.FromResult(result);

            var normalisedPrefix = StorageKeys.Normalise(prefix);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var key = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (normalisedPrefix != null && !key.StartsWith(normalisedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                result.Add(new StoredObject { Key = key, Size = info.Length, ModifiedUtc = info.LastWriteTimeUtc, Url = UrlFor(key) });
            }
            return Task.FromResult(result.OrderByDescending(o => o.ModifiedUtc).Take(Math.Clamp(max, 1, 1000)).ToList());
        }
    }
}
