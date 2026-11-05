using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using CompFile = PCL.ModComp.CompFile;
using CompFileStatus = PCL.ModComp.CompFileStatus;
using CompLoaderType = PCL.ModComp.CompLoaderType;
using CompProject = PCL.ModComp.CompProject;
using LocalCompFile = PCL.ModLocalComp.LocalCompFile;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.App.Localization;
using PCL.Network;

namespace PCL;

public static class ModCompDependency
{
    public static ModDependencyRequest BuildRequest(
        CompFile file,
        CompProject project,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        string targetModsFolder,
        CancellationToken cancellationToken = default,
        IProgress<ModDependencyProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(project);
        targetLoaders ??= new List<CompLoaderType>();

        var source = GetSource(project.FromCurseForge);
        cancellationToken.ThrowIfCancellationRequested();
        var dependencies = file.RawDependencies.Concat(file.Dependencies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dependencyId => new ModDependencyReference
            {
                ProjectId = dependencyId,
                Source = source,
                IsRequired = true,
            })
            .ToList();

        return new ModDependencyRequest
        {
            TargetMinecraftVersion = targetMinecraftVersion ?? string.Empty,
            TargetLoaders = ToLoaderNames(targetLoaders),
            RequiredDependencies = dependencies,
            InstalledMods = ScanInstalledMods(targetModsFolder, targetMinecraftVersion, targetLoaders,
                cancellationToken, progress),
            RootProject = new ModDependencyReference { ProjectId = project.Id, Source = source },
            ProjectResolver = (dependencySource, dependencyId) =>
                ResolveProjectFiles(dependencySource, dependencyId, cancellationToken),
        };
    }

    public static List<InstalledModIdentity> ScanInstalledMods(
        string targetModsFolder,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        CancellationToken cancellationToken = default,
        IProgress<ModDependencyProgress>? progress = null)
    {
        var result = new List<InstalledModIdentity>();
        if (string.IsNullOrWhiteSpace(targetModsFolder) || !Directory.Exists(targetModsFolder))
        {
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetModsFolder));
        var loader = ModLocalComp.compResourceListLoader;
        var loadedFiles = loader.input is { } input
                          && string.Equals(Path.TrimEndingDirectorySeparator(input.compPath), folder,
                              StringComparison.OrdinalIgnoreCase)
            ? loader.output?.Where(local => !local.IsFolder).ToDictionary(local => local.path,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, LocalCompFile>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LocalCompFile>(StringComparer.OrdinalIgnoreCase);
        var cache = new Lazy<JsonObject>(() =>
        {
            var cachePath = Path.Combine(ModBase.pathTemp, "Cache", "LocalComp.json");
            try
            {
                return File.Exists(cachePath)
                       && JsonNode.Parse(File.ReadAllText(cachePath)) is JsonObject cached
                       && cached["version"]?.GetValue<int>() == ModLocalComp.localModCacheVersion
                    ? cached : new JsonObject();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                ModBase.Log(ex, "读取本地 Mod 信息缓存失败");
                return new JsonObject();
            }
        });
        var paths = Directory.GetFiles(folder)
            .Where(path => LocalCompFile.IsModFile(path)
                           && !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                           && !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)).ToList();
        progress?.Report(new(ModDependencyStage.ScanningInstalledMods, 0, paths.Count));
        var completed = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localFile = loadedFiles.GetValueOrDefault(path) ?? new LocalCompFile(path);
            if (localFile.Comp is null && localFile.compFile is null)
            {
                var key = localFile.ModrinthHash + targetMinecraftVersion + string.Join("", targetLoaders);
                if (cache.Value[key] is JsonObject cached) localFile.FromJson(cached);
            }
            var projectId = localFile.Comp?.Id ?? localFile.compFile?.ProjectId;
            var fromCurseForge = localFile.Comp?.FromCurseForge ?? localFile.compFile?.FromCurseForge;

            if (projectId is not null && fromCurseForge is not null)
            {
                result.Add(new InstalledModIdentity
                {
                    SourceProjectId = projectId,
                    Source = GetSource(fromCurseForge.Value),
                    GameVersions = localFile.compFile?.GameVersions.ToList() ?? [],
                    Loaders = ToLoaderNames(localFile.compFile?.ModLoaders),
                });
            }
            completed++;
            progress?.Report(new(ModDependencyStage.ScanningInstalledMods, completed, paths.Count - completed));
        }

        return result;
    }

    public static ModDependencyProject? ResolveProjectFiles(string source, string projectId,
        CancellationToken cancellationToken = default)
    {
        var fromCurseForge = string.Equals(source, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var files = ModComp.CompFilesGet(projectId, fromCurseForge, loadDependencies: false,
            cancellationToken: cancellationToken);
        if (!ModComp.compProjectCache.TryGetValue(projectId, out var compProject))
        {
            return null;
        }

        if (compProject.FromCurseForge != fromCurseForge)
        {
            return null;
        }

        return new ModDependencyProject
        {
            ProjectId = compProject.Id,
            Source = source,
            ProjectName = compProject.TranslatedName ?? compProject.RawName,
            Files = files.Select(compFile => new ModDependencyFile
            {
                Id = compFile.Id,
                DisplayName = compFile.DisplayName,
                Version = compFile.Version,
                GameVersions = compFile.GameVersions?.Where(static version => !string.IsNullOrWhiteSpace(version)).ToList()
                               ?? new List<string>(),
                Loaders = ToLoaderNames(compFile.ModLoaders),
                ReleaseType = MapReleaseType(compFile.Status),
                ReleaseDate = compFile.ReleaseDate,
                RequiredDependencies = compFile.RawDependencies.Concat(compFile.Dependencies)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(dependencyId => new ModDependencyReference
                    {
                        ProjectId = dependencyId,
                        Source = source,
                        IsRequired = true,
                    })
                    .ToList(),
            }).ToList(),
        };
    }

    public static ModDependencyFile? SelectCompatibleDependencyFile(
        ModDependencyResolutionResult result,
        string projectId,
        string source)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.ToInstall
            .FirstOrDefault(install =>
                string.Equals(install.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(install.Source, source, StringComparison.OrdinalIgnoreCase))
            ?.File;
    }

    public static List<(string Filename, DownloadFile File)> BuildDependencyDownloads(
        ModDependencyResolutionResult result,
        string targetModsFolder)
    {
        ArgumentNullException.ThrowIfNull(result);

        var downloads = new List<(string, DownloadFile)>();
        foreach (var install in result.ToInstall.AsEnumerable().Reverse())
        {
            if (!ModComp.compProjectCache.TryGetValue(install.ProjectId, out var depProject))
            {
                continue;
            }

            var fromCurseForge = string.Equals(install.Source, "CurseForge", StringComparison.OrdinalIgnoreCase);
            if (depProject.FromCurseForge != fromCurseForge)
            {
                continue;
            }

            var depCompFile = ModComp.CompFilesGet(install.ProjectId, fromCurseForge, loadDependencies: false)
                .FirstOrDefault(file => string.Equals(file.Id, install.File.Id, StringComparison.OrdinalIgnoreCase));
            if (depCompFile is null)
            {
                continue;
            }

            var depFileName = ModComp.CompFileNameGet(depProject, depCompFile);
            var targetPath = Path.Combine(targetModsFolder ?? string.Empty, depFileName);
            downloads.Add((depFileName, depCompFile.ToNetFile(targetPath, ModComp.DownloadReason.Dependency)));
        }

        return downloads;
    }
    private static string GetSource(bool fromCurseForge)
    {
        return fromCurseForge ? "CurseForge" : "Modrinth";
    }

    private static List<string> ToLoaderNames(IEnumerable<CompLoaderType>? loaders)
    {
        if (loaders is null)
        {
            return new List<string>();
        }

        return loaders
            .Where(static loader => loader != CompLoaderType.Any)
            .Select(static loader => loader.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int MapReleaseType(CompFileStatus status)
    {
        return status switch
        {
            CompFileStatus.Release => 1,
            CompFileStatus.Beta => 2,
            CompFileStatus.Alpha => 3,
            _ => 1,
        };
    }
}
