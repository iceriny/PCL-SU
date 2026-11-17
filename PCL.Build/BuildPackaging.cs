using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Build;

public sealed record PreparedBuild(string FullVersion, string BuildMetadata, string FileVersion, IReadOnlyList<string> EmbeddedVersions);

public static class BuildPackaging
{
    public const int RetainedReleaseNotes = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string AllocateReleaseBuild(string historyJson, DateOnly? date = null)
    {
        var today = date ?? BuildStamp.ShanghaiToday;
        using var history = JsonDocument.Parse(historyJson);
        var maximum = BigInteger.Zero;
        var legacyTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in EnumerateReleases(history.RootElement))
        {
            var tag = StringProperty(release, "tag_name") ?? StringProperty(release, "tagName");
            if (!BuildVersion.TryParse(tag, out var version)) continue;
            if (version!.Build is { } build)
            {
                if (build.Date == today && build.Sequence > maximum) maximum = build.Sequence;
                continue;
            }
            var published = StringProperty(release, "published_at") ?? StringProperty(release, "publishedAt")
                ?? StringProperty(release, "created_at");
            if (DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.None, out var publishedAt)
                && DateOnly.FromDateTime(publishedAt.ToOffset(TimeSpan.FromHours(8)).DateTime) == today)
                legacyTags.Add(version.Core);
        }
        var stamp = new BuildStamp(today, BigInteger.Max(maximum, legacyTags.Count) + 1);
        _ = stamp.WindowsRevision;
        return stamp.ToString();
    }

    public static string AllocateLocalBuild(string statePath, DateOnly? date = null)
    {
        statePath = Path.GetFullPath(statePath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        using var stateLock = AcquireLock(statePath + ".lock");
        var today = date ?? BuildStamp.ShanghaiToday;
        var sequence = BigInteger.Zero;
        if (File.Exists(statePath))
        {
            using var state = JsonDocument.Parse(File.ReadAllText(statePath, Encoding.UTF8));
            var previousText = StringProperty(state.RootElement, "build");
            if (!BuildStamp.TryParse(previousText, out var previous))
                throw new InvalidDataException($"Invalid local build counter: {statePath}");
            if (previous!.Date == today) sequence = previous.Sequence;
        }
        var stamp = new BuildStamp(today, sequence + 1);
        _ = stamp.WindowsRevision;
        AtomicWrite(statePath, JsonSerializer.Serialize(new { build = stamp.ToString() }, JsonOptions) + Environment.NewLine);
        return stamp.ToString();
    }

    public static IReadOnlyList<string> SelectReleaseNotes(IEnumerable<string> files, string currentVersion)
    {
        var current = BuildVersion.Parse(currentVersion);
        return files.Select(path => (Path: path, Parsed: BuildVersion.TryParse(Path.GetFileNameWithoutExtension(path), out var version) ? version : null))
            .Where(item => item.Parsed is not null && item.Parsed.CompareTo(current) <= 0)
            .OrderByDescending(item => item.Parsed)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .GroupBy(item => item.Parsed!.ToString(), StringComparer.Ordinal)
            .Select(group => group.First().Path)
            .Take(RetainedReleaseNotes).ToArray();
    }

    public static PreparedBuild Prepare(string metadataPath, string notesPath, string outputPath, string statePath,
        string? buildMetadata = null, string? historyPath = null)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException("Launcher metadata must be an object.");
        var version = metadata["version"] as JsonObject ?? throw new InvalidDataException("Missing launcher version.");
        var baseVersion = version["base"]?.GetValue<string>() ?? throw new InvalidDataException("Missing base version.");
        var suffix = version["suffix"]?.GetValue<string>() ?? "";
        var core = BuildVersion.Parse(baseVersion + (suffix.Length == 0 ? "" : "-" + suffix));
        if (core.Build is not null) throw new InvalidDataException("The source base version must not contain build metadata.");
        buildMetadata = string.IsNullOrWhiteSpace(buildMetadata) ? AllocateLocalBuild(statePath) : buildMetadata;
        if (!BuildStamp.TryParse(buildMetadata, out var stamp)) throw new ArgumentException("Expected build metadata yyyyMMdd plus a positive daily sequence.");
        var numericVersion = $"{core.Major}.{core.Minor}.{core.Patch}.{stamp!.WindowsRevision}";
        var fullVersion = core.Core + "+" + buildMetadata;
        var notes = CollectReleaseNotes(notesPath, fullVersion, historyPath);
        if (!notes.Any(note => note.Version == fullVersion))
            throw new InvalidDataException("Missing exact release notes for " + fullVersion + "; provide current.md or matching frozen notes.");
        outputPath = Path.GetFullPath(outputPath);
        var noteOutputPath = Path.Combine(outputPath, "notes");
        Directory.CreateDirectory(noteOutputPath);
        var selectedNames = notes.Select(note => note.Version + ".md").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes) AtomicWrite(Path.Combine(noteOutputPath, note.Version + ".md"), note.Content);
        foreach (var oldNote in Directory.EnumerateFiles(noteOutputPath, "*.md"))
            if (!selectedNames.Contains(Path.GetFileName(oldNote))) File.Delete(oldNote);
        version["build"] = buildMetadata;
        AtomicWrite(Path.Combine(outputPath, "metadata.json"), metadata.ToJsonString(JsonOptions) + Environment.NewLine);
        AtomicWrite(Path.Combine(outputPath, "version.txt"), fullVersion + Environment.NewLine);
        AtomicWrite(Path.Combine(outputPath, "build.txt"), buildMetadata + Environment.NewLine);
        AtomicWrite(Path.Combine(outputPath, "assembly-version.txt"), $"{core.Major}.{core.Minor}.{core.Patch}.0" + Environment.NewLine);
        AtomicWrite(Path.Combine(outputPath, "file-version.txt"), numericVersion + Environment.NewLine);
        return new PreparedBuild(fullVersion, buildMetadata, numericVersion,
            notes.Select(note => note.Version).ToArray());
    }

    private sealed record ReleaseNote(BuildVersion Parsed, string Content)
    {
        public string Version => Parsed.ToString();
    }

    private static IReadOnlyList<ReleaseNote> CollectReleaseNotes(string directory, string currentVersion, string? historyPath)
    {
        var current = BuildVersion.Parse(currentVersion);
        var notes = new Dictionary<string, ReleaseNote>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (BuildVersion.TryParse(Path.GetFileNameWithoutExtension(path), out var version) && version!.CompareTo(current) <= 0)
                notes[version.ToString()] = new ReleaseNote(version, File.ReadAllText(path, Encoding.UTF8));
        }
        var currentPath = Path.Combine(directory, "current.md");
        if (File.Exists(currentPath))
        {
            var content = File.ReadAllText(currentPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Current release notes are empty.");
            notes[current.ToString()] = new ReleaseNote(current, content);
        }
        if (!string.IsNullOrWhiteSpace(historyPath))
        {
            using var history = JsonDocument.Parse(File.ReadAllText(historyPath, Encoding.UTF8));
            foreach (var release in EnumerateReleases(history.RootElement))
            {
                if (TrueProperty(release, "draft") || TrueProperty(release, "isDraft")) continue;
                var tag = StringProperty(release, "tag_name") ?? StringProperty(release, "tagName");
                var content = StringProperty(release, "body");
                if (!BuildVersion.TryParse(tag, out var version) || version!.CompareTo(current) > 0 || string.IsNullOrWhiteSpace(content)) continue;
                // 已发布的同版本正文优先于仓库草稿，保证历史日志的版本归属。
                notes[version.ToString()] = new ReleaseNote(version, content);
            }
        }
        return notes.Values.OrderByDescending(note => note.Parsed).Take(RetainedReleaseNotes).ToArray();
    }

    private static FileStream AcquireLock(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? StringProperty(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool TrueProperty(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> EnumerateReleases(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub release history must be an array.");
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
                foreach (var nested in EnumerateReleases(item)) yield return nested;
            else if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }
}
