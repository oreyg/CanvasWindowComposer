using System;
using System.Collections.Generic;
using System.IO;

namespace CanvasDesktop;

/// <summary>
/// User-toggleable feature flags. Implementations may be backed by a config
/// file (<see cref="AppConfig"/>) or a fake (tests).
/// </summary>
internal interface IAppConfig
{
    bool DisableSearch { get; }
    bool DisableAltPan { get; }
    bool DisableGreedyDraw { get; }

    /// <summary>
    /// When true, Alt+Q is not registered as a global hotkey, leaving it
    /// available to other apps. The overview can still be opened via the
    /// middle-click drag pan flow.
    /// </summary>
    bool DisableZoomHotkey { get; }

    /// <summary>
    /// When false (default), raw mouse deltas are passed through Windows'
    /// pointer acceleration curve before being applied to the canvas, so pan
    /// speed matches cursor speed and the cursor stays anchored to the same
    /// pixel of any window thumbnail under it. When true, raw HID deltas go
    /// to the canvas directly (1 mouse count = 1 canvas pixel) — useful if
    /// you've turned off "Enhance pointer precision" and want truly linear pan.
    /// </summary>
    bool DisableMouseCurve { get; }
}

/// <summary>
/// Reads configuration from %APPDATA%/CanvasWindowComposer/config.ini and
/// reloads on file change. Creates a default config file if it doesn't exist.
/// </summary>
internal sealed class AppConfig : IAppConfig
{
    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CanvasWindowComposer");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.ini");
    private const long ReloadDebounceMs = 200;

    private readonly IClock _clock;
    private FileSystemWatcher? _watcher;
    private long _lastReloadTick;

    public bool DisableSearch { get; private set; }
    public bool DisableAltPan { get; private set; }
    public bool DisableGreedyDraw { get; private set; } = true;
    public bool DisableMouseCurve { get; private set; }
    public bool DisableZoomHotkey { get; private set; }

    public AppConfig(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public void Load()
    {
        if (!File.Exists(ConfigPath))
        {
            WriteDefault();
            return;
        }

        var values = ParseIni(ConfigPath);

        DisableSearch = GetBool(values, "DisableSearch", defaultValue: false);
        DisableAltPan = GetBool(values, "DisableAltPan", defaultValue: false);
        DisableGreedyDraw = GetBool(values, "DisableGreedyDraw", defaultValue: true);
        DisableMouseCurve = GetBool(values, "DisableMouseCurve", defaultValue: false);
        DisableZoomHotkey = GetBool(values, "DisableZoomHotkey", defaultValue: false);
    }

    /// <summary>Watch config.ini for changes and reload automatically.</summary>
    public void StartObservingChanges()
    {
        Directory.CreateDirectory(ConfigDir);

        _watcher = new FileSystemWatcher(ConfigDir, "config.ini")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        long now = _clock.TickCount64;
        if (now - _lastReloadTick < ReloadDebounceMs) return;
        _lastReloadTick = now;

        try { Load(); }
        catch { }
    }

    private static void WriteDefault()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath,
@"[CanvasWindowComposer]
; All flags below are commented out and set to their default values.
; Uncomment a line and flip to true to opt out of the feature.

; Disable Alt+S window search
;DisableSearch=false

; Disable Alt+middle-click pan over windows
;DisableAltPan=false

; Disable SetWindowRgn clipping for off-screen windows
; Retains Alt-Tab and taskbar thumbnails at expense of performance
; Default on - with clipping enabled, you might see gray windows,
; if this app terminates unexpectedly
;DisableGreedyDraw=true

; Disable Windows pointer acceleration curve on pan deltas
; (default off = curve on, pan tracks cursor; on = raw HID deltas)
;DisableMouseCurve=false

; Disable the Alt+Q overview/zoom global hotkey
; (frees Alt+Q for other apps; the overview is still reachable via pan)
;DisableZoomHotkey=false
");
    }

    private static Dictionary<string, string> ParseIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            result[key] = val;
        }

        return result;
    }

    private static bool GetBool(Dictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out string? val))
            return defaultValue;
        return val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
