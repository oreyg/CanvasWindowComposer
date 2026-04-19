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
    bool DisableDllInjection { get; }
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
    public bool DisableGreedyDraw { get; private set; }
    public bool DisableDllInjection { get; private set; }

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

        DisableSearch = GetBool(values, "DisableSearch");
        DisableAltPan = GetBool(values, "DisableAltPan");
        DisableGreedyDraw = GetBool(values, "DisableGreedyDraw");
        DisableDllInjection = GetBool(values, "DisableDllInjection");
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
; Set to true to disable features

; Disable Alt+S window search
DisableSearch=false

; Disable Alt+middle-click pan over windows
DisableAltPan=false

; Disable SetWindowRgn clipping for off-screen windows
; Retains Alt-Tab and taskbar thumbnails at expense of performance
DisableGreedyDraw=false

; Disable DLL injection into managed windows
DisableDllInjection=false
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

    private static bool GetBool(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? val) &&
               val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
