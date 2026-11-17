using System.IO;
using System.Reflection;
using PCL.Core.App;
using PCL.Core.Utils;

namespace PCL;

internal static class EmbeddedReleaseNotes
{
    private const string Prefix = "PCL.ReleaseNotes.";

    internal static string? ReadCurrent() => Read(typeof(EmbeddedReleaseNotes).Assembly, Basics.VersionName);

    internal static string? Read(Assembly assembly, string version)
    {
        if (!SemVer.TryParse(version, out var parsed)) return null;
        using var stream = assembly.GetManifestResourceStream(Prefix + parsed + ".md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static int CompareStartupVersion(string previous, string current)
        => SemVer.TryParse(previous, out var oldVersion) && SemVer.TryParse(current, out var newVersion)
            ? LauncherVersionComparer.Compare(newVersion!, oldVersion!) : 0;
}
