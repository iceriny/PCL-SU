using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.Test.Network;
using static PCL.ModComp;
using static PCL.ModLocalComp;

namespace PCL.Core.Test.Minecraft;

[TestClass]
[DoNotParallelize]
public sealed class ModCompDependencyTest
{
    [TestMethod]
    public void ReusesLoadedProjectIdentityAndDoesNotSatisfyDisabledMods()
    {
        DownloadTestEnvironment.EnsureInitialized();
        var folder = Path.Combine(DownloadTestEnvironment.RootDirectory, "dependency-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousInput = compResourceListLoader.input;
        var previousOutput = compResourceListLoader.output;
        var enabled = new LocalCompFile(Path.Combine(folder, "enabled.jar"));
        var disabled = new LocalCompFile(Path.Combine(folder, "disabled.jar.disabled"));
        try
        {
            // Invalid jars make an accidental full metadata parse visible in this test.
            File.WriteAllText(enabled.path, "loaded enabled mod");
            File.WriteAllText(disabled.path, "loaded disabled mod");
            foreach (var local in new[] { enabled, disabled })
            {
                var id = ReferenceEquals(local, enabled) ? "dependency-enabled" : "dependency-disabled";
                local.Comp = new CompProject(new JsonObject
                {
                    ["DataSource"] = "Modrinth", ["Type"] = (int)CompType.Mod,
                    ["Slug"] = id, ["Id"] = id, ["RawName"] = id,
                    ["Description"] = "", ["Website"] = "https://example.test/", ["DownloadCount"] = 1,
                    ["Tags"] = new JsonArray("test"),
                });
                local.compFile = new CompFile(new JsonObject
                {
                    ["FromCurseForge"] = false, ["Id"] = id + "-file", ["DisplayName"] = id,
                    ["ReleaseDate"] = DateTime.UtcNow, ["DownloadCount"] = 1, ["Status"] = 1,
                    ["GameVersions"] = new JsonArray("1.20.1"),
                    ["ModLoaders"] = new JsonArray((int)CompLoaderType.Fabric),
                }, CompType.Mod);
            }
            compResourceListLoader.input = new CompLocalLoaderData { compPath = folder + Path.DirectorySeparatorChar };
            compResourceListLoader.output = [enabled, disabled];

            var installed = global::PCL.ModCompDependency.ScanInstalledMods(folder, "1.20.1", [CompLoaderType.Fabric]);
            var queried = new List<string>();
            var result = new ModDependencyResolver().Resolve(new ModDependencyRequest
            {
                TargetMinecraftVersion = "1.20.1", TargetLoaders = ["Fabric"], InstalledMods = installed,
                RequiredDependencies = [
                    new() { ProjectId = "dependency-enabled", Source = "Modrinth" },
                    new() { ProjectId = "dependency-disabled", Source = "Modrinth" },
                ],
                ProjectResolver = (_, id) => { queried.Add(id); return null; },
            });

            Assert.AreEqual("dependency-enabled", installed.Single().SourceProjectId);
            Assert.AreEqual("dependency-enabled", result.Satisfied.Single().ProjectId);
            CollectionAssert.AreEqual(new[] { "dependency-disabled" }, queried);
            Assert.AreEqual("dependency-disabled", result.Unresolved.Single().ProjectId);
        }
        finally
        {
            compResourceListLoader.input = previousInput;
            compResourceListLoader.output = previousOutput;
            compProjectCache.TryRemove("dependency-enabled", out _);
            compProjectCache.TryRemove("dependency-disabled", out _);
            Directory.Delete(folder, true);
        }
    }
}
