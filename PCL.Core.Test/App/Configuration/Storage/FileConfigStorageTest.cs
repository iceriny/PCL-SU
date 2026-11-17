using System;
using System.Collections.Generic;
using System.IO;
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
    public void InvalidatingInstanceCacheFlushesBeforeItsDirectoryIsRenamed()
    {
        Network.DownloadTestEnvironment.EnsureInitialized();
        var root = Path.Combine(Path.GetTempPath(), "PCLTest", "Rename", Guid.NewGuid().ToString("N"));
        var oldPath = Path.Combine(root, "old");
        var newPath = Path.Combine(root, "new");
        var storage = new DynamicCacheConfigStorage
        {
            StorageFactory = context => new FileConfigStorage(
                new YamlFileProvider(Path.Combine((string)context!, "PCL", "Setup.yml")))
        };
        try
        {
            storage.SetValue("JvmArgs", "-Xmx2G", oldPath);
            Assert.IsTrue(storage.InvalidateCache(oldPath));
            Directory.Move(oldPath, newPath);
            Assert.IsTrue(storage.GetValue<string>("JvmArgs", out var value, newPath));
            Assert.AreEqual("-Xmx2G", value);
            storage.SetValue("JvmArgs", "-Xmx4G", newPath);
            storage.Stop();
            Assert.IsFalse(Directory.Exists(oldPath));
            Assert.AreEqual("-Xmx4G", new YamlFileProvider(Path.Combine(newPath, "PCL", "Setup.yml")).Get<string>("JvmArgs"));
        }
        finally
        {
            storage.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
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
