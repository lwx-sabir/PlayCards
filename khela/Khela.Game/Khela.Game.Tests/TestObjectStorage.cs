using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Khela.Game.Services.Storage;

namespace Khela.Game.Tests
{
    /// <summary>
    /// A storage stand-in for tests: keys come back as themselves.
    ///
    /// Deliberately identity rather than a fake bucket url — a test asserting on what a catalog serves should read the
    /// key it put in, not a url invented here that nothing else in the test knows about.
    /// </summary>
    public sealed class TestObjectStorage : IObjectStorage
    {
        public string ProviderName => "Test";
        public bool CanWrite => true;

        public string UrlFor(string key) => string.IsNullOrWhiteSpace(key) ? null : key;

        public Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
            => Task.FromResult(key);

        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

        public Task<List<StoredObject>> ListAsync(string prefix, int max = 500, CancellationToken ct = default)
            => Task.FromResult(new List<StoredObject>());
    }
}
