using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Storage
{
    /// <summary>
    /// Cloudflare R2, through its S3-compatible API.
    ///
    /// R2 differs from S3 in two ways that matter here. It has no regions — the endpoint is the account's own
    /// <c>{account}.r2.cloudflarestorage.com</c> and the SDK still wants *a* region to sign with, so it is given
    /// <c>auto</c>. And objects are not made public by an ACL: a bucket is exposed through its r2.dev subdomain or a
    /// custom domain, which is what <see cref="StorageOptions.PublicBaseUrl"/> points at. So this never sets an ACL —
    /// R2 rejects the header — and reading is a plain CDN GET that never touches this service.
    /// </summary>
    public sealed class R2ObjectStorage : IObjectStorage, IDisposable
    {
        private readonly StorageOptions _options;
        private readonly ILogger<R2ObjectStorage> _logger;
        private readonly IAmazonS3 _s3;

        public R2ObjectStorage(IOptions<StorageOptions> options, ILogger<R2ObjectStorage> logger)
        {
            _options = options.Value ?? new StorageOptions();
            _logger = logger;

            var r2 = _options.R2;
            _s3 = new AmazonS3Client(r2.AccessKeyId, r2.SecretAccessKey, new AmazonS3Config
            {
                ServiceURL = r2.ServiceUrl,
                // R2 has no regions, but the SDK signs with one; "auto" is what Cloudflare documents.
                AuthenticationRegion = "auto",
                ForcePathStyle = true,
            });
        }

        public string ProviderName => "Cloudflare R2";
        public bool CanWrite => _options.R2.IsConfigured;

        public string UrlFor(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (StorageKeys.IsAbsolute(key)) return key;                 // a legacy absolute url still resolves
            var baseUrl = (_options.PublicBaseUrl ?? "").TrimEnd('/');
            var normalised = StorageKeys.Normalise(key);
            if (normalised == null) return null;
            return string.IsNullOrEmpty(baseUrl) ? normalised : baseUrl + "/" + normalised;
        }

        public async Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
        {
            var normalised = StorageKeys.Normalise(key) ?? throw new ArgumentException("Unusable storage key.", nameof(key));
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _options.R2.Bucket,
                Key = normalised,
                InputStream = content,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? StorageKeys.ContentType(normalised) : contentType,
                // A week, matching what the local static route serves. Keys are stable, and a replaced image gets a new
                // images-version so clients drop their cache anyway.
                Headers = { CacheControl = "public,max-age=604800" },
                DisablePayloadSigning = true,
            }, ct);

            _logger.LogInformation("R2: stored {Key} in {Bucket}.", normalised, _options.R2.Bucket);
            return UrlFor(normalised);
        }

        public async Task DeleteAsync(string key, CancellationToken ct = default)
        {
            var normalised = StorageKeys.Normalise(key);
            if (normalised == null) return;
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.R2.Bucket, Key = normalised }, ct);
            _logger.LogInformation("R2: deleted {Key} from {Bucket}.", normalised, _options.R2.Bucket);
        }

        public async Task<List<StoredObject>> ListAsync(string prefix, int max = 500, CancellationToken ct = default)
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _options.R2.Bucket,
                Prefix = StorageKeys.Normalise(prefix),
                MaxKeys = Math.Clamp(max, 1, 1000),
            };
            var response = await _s3.ListObjectsV2Async(request, ct);
            return (response.S3Objects ?? new List<S3Object>())
                .OrderByDescending(o => o.LastModified)
                .Select(o => new StoredObject
                {
                    Key = o.Key,
                    Size = o.Size,
                    ModifiedUtc = o.LastModified.ToUniversalTime(),
                    Url = UrlFor(o.Key),
                })
                .ToList();
        }

        public void Dispose() => _s3?.Dispose();
    }
}
