using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PCL.Build;

public sealed record BuildStamp(DateOnly Date, BigInteger Sequence)
{
    public static DateOnly ShanghaiToday => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);

    public static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out BuildStamp? stamp)
    {
        stamp = null;
        if (text is null || text.Length < 9 || !text.All(char.IsAsciiDigit)
            || text[8] == '0' || !DateOnly.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            || !BigInteger.TryParse(text[8..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence < 1) return false;
        stamp = new BuildStamp(date, sequence);
        return true;
    }

    public int WindowsRevision => Sequence <= ushort.MaxValue
        ? (int)Sequence : throw new InvalidOperationException("Daily build sequence exceeds the Windows file version limit of 65535.");

    public override string ToString() => Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + Sequence.ToString(CultureInfo.InvariantCulture);
}

public sealed class BuildVersion : IComparable<BuildVersion>
{
    private static readonly Regex Pattern = new(
        @"\Av?(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<phase>alpha|beta|rc)\.(?<preview>0|[1-9][0-9]*))?(?:\+(?<build>[0-9]+))?\z",
        RegexOptions.CultureInvariant);

    public required int Major { get; init; }
    public required int Minor { get; init; }
    public required int Patch { get; init; }
    public string Phase { get; init; } = "";
    public BigInteger Preview { get; init; }
    public BuildStamp? Build { get; init; }
    public string Core => $"{Major}.{Minor}.{Patch}" + (Phase.Length == 0 ? "" : $"-{Phase}.{Preview}");

    public static bool TryParse(string? text, out BuildVersion? version)
    {
        version = null;
        if (text is null) return false;
        var match = Pattern.Match(text);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, out var patch)) return false;
        BuildStamp? build = null;
        if (match.Groups["build"].Success && !BuildStamp.TryParse(match.Groups["build"].Value, out build)) return false;
        version = new BuildVersion
        {
            Major = major, Minor = minor, Patch = patch, Phase = match.Groups["phase"].Value,
            Preview = match.Groups["preview"].Success ? BigInteger.Parse(match.Groups["preview"].Value, CultureInfo.InvariantCulture) : 0,
            Build = build
        };
        return true;
    }

    public static BuildVersion Parse(string text) => TryParse(text, out var version)
        ? version! : throw new ArgumentException($"Invalid launcher version: {text}");

    public int CompareTo(BuildVersion? other)
    {
        if (other is null) return 1;
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        static int Rank(string phase) => phase switch { "alpha" => 0, "beta" => 1, "rc" => 2, _ => 3 };
        comparison = Rank(Phase).CompareTo(Rank(other.Phase));
        if (comparison != 0) return comparison;
        comparison = Preview.CompareTo(other.Preview);
        if (comparison != 0) return comparison;
        if (Build is null) return other.Build is null ? 0 : -1;
        if (other.Build is null) return 1;
        comparison = Build.Date.CompareTo(other.Build.Date);
        return comparison != 0 ? comparison : Build.Sequence.CompareTo(other.Build.Sequence);
    }

    public override string ToString() => Core + (Build is null ? "" : "+" + Build);
}
