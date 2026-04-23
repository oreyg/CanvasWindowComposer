namespace CanvasDesktop;

/// <summary>The three overview display modes.</summary>
internal enum OverviewMode
{
    Hidden,
    Panning,
    Zooming
}

/// <summary>
/// Per-mode visual + input configuration. Pure data; chosen by
/// <see cref="OverviewState"/> on each transition and read by the manager
/// when applying the mode to passes.
/// </summary>
internal readonly record struct OverviewModeConfig(
    bool GridVisible,
    byte DesktopOpacity,
    bool TaskbarVisible,
    bool InputEnabled,
    bool InertiaAllowed);

/// <summary>
/// Pure mode + config state machine for the overview. No Win32, no events.
/// Owned by <see cref="OverviewManager"/>; the manager fires events around
/// <see cref="SetMode"/> and runs the side-effect work between them.
/// </summary>
internal sealed class OverviewState
{
    public static readonly OverviewModeConfig HiddenConfig = new(
        GridVisible: false,
        DesktopOpacity: 0,
        TaskbarVisible: false,
        InputEnabled: false,
        InertiaAllowed: false);

    public static readonly OverviewModeConfig PanningConfig = new(
        GridVisible: false,
        DesktopOpacity: 255,
        TaskbarVisible: true,
        InputEnabled: false,
        InertiaAllowed: true);

    public static readonly OverviewModeConfig ZoomingConfig = new(
        GridVisible: true,
        DesktopOpacity: 120,
        TaskbarVisible: false,
        InputEnabled: true,
        InertiaAllowed: false);

    public OverviewMode CurrentMode { get; private set; } = OverviewMode.Hidden;
    public OverviewModeConfig CurrentConfig { get; private set; } = HiddenConfig;

    /// <summary>Apply <paramref name="target"/>; returns false if it equals the current mode.</summary>
    public bool SetMode(OverviewMode target)
    {
        if (CurrentMode == target) return false;
        CurrentMode = target;
        CurrentConfig = ConfigFor(target);
        return true;
    }

    public static OverviewModeConfig ConfigFor(OverviewMode mode)
    {
        switch (mode)
        {
            case OverviewMode.Panning: return PanningConfig;
            case OverviewMode.Zooming: return ZoomingConfig;
            default: return HiddenConfig;
        }
    }
}
