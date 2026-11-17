using System;
using System.Globalization;

namespace PCL.Core.Utils;

/// <summary>启动器版本在 SemVer 优先级相同后，继续比较日期和当日构建序号。</summary>
public static class LauncherVersionComparer
{
    public static int Compare(string left, string right) => Compare(SemVer.Parse(left), SemVer.Parse(right));

    public static int Compare(SemVer left, SemVer right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var precedence = left.CompareTo(right);
        if (precedence != 0) return precedence;

        var leftHasBuild = TryParseBuildMetadata(left.BuildMetadata, out var leftDate, out var leftSequence);
        var rightHasBuild = TryParseBuildMetadata(right.BuildMetadata, out var rightDate, out var rightSequence);
        if (leftHasBuild != rightHasBuild) return leftHasBuild ? 1 : -1;
        if (!leftHasBuild) return 0;

        var dateOrder = leftDate.CompareTo(rightDate);
        if (dateOrder != 0) return dateOrder;
        var sequenceLengthOrder = leftSequence.Length.CompareTo(rightSequence.Length);
        return sequenceLengthOrder != 0 ? sequenceLengthOrder : string.CompareOrdinal(leftSequence, rightSequence);
    }

    public static bool TryParseBuildMetadata(string? metadata, out DateOnly date, out string sequence)
    {
        date = default;
        sequence = string.Empty;
        if (metadata is null || metadata.Length < 9 || metadata[8] == '0') return false;
        foreach (var digit in metadata)
            if (!char.IsAsciiDigit(digit)) return false;
        if (!DateOnly.TryParseExact(metadata.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            return false;

        sequence = metadata[8..];
        return true;
    }
}
