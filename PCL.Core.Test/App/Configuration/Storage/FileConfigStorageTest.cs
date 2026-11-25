using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Configuration.Storage;

namespace PCL.Core.Test.App.Configuration.Storage;

[TestClass]
public class FileConfigStorageTest
{
    [TestMethod]
    public void Stop_ShouldPersistQueuedWrite()
    {
        var provider = new TestFileProvider();
        var storage = new FileConfigStorage(provider);

        storage.SetValue("JvmArgs", "-Xmx2G");
        storage.Stop();

        Assert.AreEqual("-Xmx2G", provider.Get<string>("JvmArgs"));
    }

    [TestMethod]
    public void ConcurrentAccessSharesOneStorageAndInvalidationFlushesWrites()
    {
        var created = 0;
        var provider = new TestFileProvider();
        var cache = new DynamicCacheConfigStorage
        {
            StorageFactory = _ =>
            {
                Interlocked.Increment(ref created);
                return new FileConfigStorage(provider);
            }
        };

        try
        {
            Parallel.For(0, 32, index => cache.SetValue($"key-{index}", index, "instance"));
            Assert.AreEqual(1, created);
            Assert.IsTrue(cache.InvalidateCache("instance"));
            for (var index = 0; index < 32; index++)
                Assert.AreEqual(index, provider.Get<int>($"key-{index}"));
        }
        finally
        {
            cache.Stop();
        }
    }

    private sealed class TestFileProvider : IKeyValueFileProvider
    {
        private readonly Dictionary<string, object?> _values = [];

        public string FilePath => "test://config";

        public T Get<T>(string key) => (T)_values[key]!;

        public void Set<T>(string key, T value) => _values[key] = value;

        public bool Exists(string key) => _values.ContainsKey(key);

        public void Remove(string key) => _values.Remove(key);

        public void Sync() { }
    }
}
