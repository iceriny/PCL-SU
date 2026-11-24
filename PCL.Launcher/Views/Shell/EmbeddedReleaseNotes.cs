using System.IO;
using PCL.Core.App;
using PCL.Core.Utils;

namespace PCL.Views.Shell;

internal static class EmbeddedReleaseNotes
{
    public static string? ReadCurrent()
    {
        using var stream = typeof(EmbeddedReleaseNotes).Assembly.GetManifestResourceStream(
            $"PCL.ReleaseNotes.{Basics.VersionName}.md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static bool RecordStartupVersion()
    {
        var previousCode = States.System.LastVersion;
        var previousName = States.System.LastVersionName;
        var currentName = Basics.VersionName;
        if (previousCode == Basics.VersionCode && previousName == currentName) return false;

        var isUpgrade = SemVer.TryParse(previousName, out var previous) &&
                        SemVer.TryParse(currentName, out var current)
            ? LauncherVersionComparer.Compare(current!, previous!) > 0
            : Basics.VersionCode > previousCode;
        States.System.LastVersion = Basics.VersionCode;
        States.System.LastVersionName = currentName;
        return previousCode > 0 && isUpgrade;
    }
}
