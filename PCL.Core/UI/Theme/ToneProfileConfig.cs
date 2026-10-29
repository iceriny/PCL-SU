namespace PCL.Core.UI.Theme;

public record ToneProfileConfig
{
    public static readonly ToneProfile DefaultLight = new();

    public static readonly ToneProfile DefaultDark = new(
        L1: 0.96, L2: 0.75, L3: 0.6, L4: 0.65,
        L5: 0.45, L6: 0.25, L7: 0.225, L8: 0.2,
        LBackground: 0.3, LForeground: 1, LWhite: 0.275
    );

    public static readonly ToneProfile MistRoseLight = new(
        L1: 0.3347, L2: 0.4924, L3: 0.5433, L4: 0.61,
        L5: 0.7831, L6: 0.92, L7: 0.94, L8: 0.96,
        LWhite: 0.983, LBackground: 0.975,
        C1: 0.0412, C2: 0.0895, C3: 0.0914, C4: 0.09,
        C5: 0.0724, C6: 0.032, C7: 0.024, C8: 0.015
    );

    public static readonly ToneProfile MistRoseDark = new(
        L1: 0.94, L2: 0.7831, L3: 0.82, L4: 0.86,
        L5: 0.52, L6: 0.33, L7: 0.3, L8: 0.265,
        LWhite: 0.2879, LForeground: 1, LBackground: 0.2416,
        C1: 0.014, C2: 0.0724, C3: 0.075, C4: 0.06,
        C5: 0.06, C6: 0.035, C7: 0.03, C8: 0.025
    );

    public ToneProfile Light { get => field ?? DefaultLight; init; } = null!;

    public ToneProfile Dark { get => field ?? DefaultDark; init; } = null!;
}
