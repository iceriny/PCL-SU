using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.Test.Network;
using static PCL.Comp;
using static PCL.LocalComp;

namespace PCL.Core.Test.Minecraft;

[TestClass]
[DoNotParallelize]
public sealed class CompDependencyScanTest
{
    [TestMethod]
    public void ReusesLoadedProjectIdentityAndIgnoresDisabledMods()
    {
        DownloadTestEnvironment.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("pcl-dependency-scan-");
        var previousInput = CompResourceListLoader.input;
        var previousOutput = CompResourceListLoader.output;
        var prefix = Guid.NewGuid().ToString("N");
        var enabledId = prefix + "-enabled";
        var disabledId = prefix + "-disabled";
        var rootId = prefix + "-root";
        try
        {
            var enabled = CreateLocalFile("enabled.jar", enabledId);
            var disabled = CreateLocalFile("disabled.jar.disabled", disabledId);
            CompResourceListLoader.input = new CompLocalLoaderData
            {
                compPath = folder.FullName + Path.DirectorySeparatorChar,
                compType = CompType.Mod
            };
            CompResourceListLoader.output = [enabled, disabled];

            var root = new CompFile(new JsonObject
            {
                ["FromCurseForge"] = false,
                ["RawDependencies"] = new JsonArray(enabledId, disabledId)
            }, CompType.Mod);
            var request = CompDependency.BuildRequest(root, CreateProject(rootId), "1.20.1", [CompLoaderType.Fabric], folder.FullName);
            var queried = new List<string>();
            var result = new ModDependencyResolver().Resolve(new ModDependencyRequest
            {
                TargetMinecraftVersion = request.TargetMinecraftVersion,
                TargetLoaders = request.TargetLoaders,
                InstalledMods = request.InstalledMods,
                RequiredDependencies = request.RequiredDependencies,
                ProjectResolver = (_, id) => { queried.Add(id); return null; }
            });

            Assert.AreEqual(enabledId, request.InstalledMods.Single().SourceProjectId);
            Assert.AreEqual(enabledId, result.Satisfied.Single().ProjectId);
            CollectionAssert.AreEqual(new[] { disabledId }, queried);
            Assert.AreEqual(disabledId, result.Unresolved.Single().ProjectId);
        }
        finally
        {
            CompResourceListLoader.input = previousInput;
            CompResourceListLoader.output = previousOutput;
            foreach (var projectId in new[] { enabledId, disabledId, rootId })
                compProjectCache.TryRemove(projectId, out _);
            folder.Delete(recursive: true);
        }

        LocalCompFile CreateLocalFile(string fileName, string projectId)
        {
            var path = Path.Combine(folder.FullName, fileName);
            File.WriteAllText(path, "local mod file");
            return new LocalCompFile(path)
            {
                Comp = CreateProject(projectId),
                CompFile = new CompFile(new JsonObject
                {
                    ["FromCurseForge"] = false,
                    ["GameVersions"] = new JsonArray("1.20.1"),
                    ["ModLoaders"] = new JsonArray((int)CompLoaderType.Fabric)
                }, CompType.Mod)
            };
        }
    }

    private static CompProject CreateProject(string id) => new(new JsonObject
    {
        ["DataSource"] = "Modrinth",
        ["Type"] = (int)CompType.Mod,
        ["Slug"] = id,
        ["Id"] = id,
        ["Tags"] = new JsonArray("test")
    });
}
