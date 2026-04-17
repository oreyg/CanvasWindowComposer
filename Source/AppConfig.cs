using System;
using System.Collections.Generic;
using System.IO;

namespace CanvasDesktop;

/// <summary>
/// Reads configuration from %APPDATA%/CanvasWindowComposer/config.ini
/// Creates a default config file if it doesn't exist.
/// </summary>
internal sealed class AppConfig
{
    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CanvasWindowComposer");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.ini");

    public static bool DisableSearch { get; private set; }
    public static bool DisableAltPan { get; private set; }
    public static bool DisableGreedyDraw { get; private set; }
    public static bool DisableDllInjection { get; private set; }

    public static void Load()
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

    private static long _lastReloadTick;
    private const long ReloadDebounceMs = 200;

    /// <summary>Watch config.ini for changes and reload automatically.</summary>
    public static void StartObservingChanges()
    {
        Directory.CreateDirectory(ConfigDir);

        var watcher = new FileSystemWatcher(ConfigDir, "config.ini")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnConfigFileChanged;
        watcher.Created += OnConfigFileChanged;
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        long now = Environment.TickCount64;
        if (now - _lastReloadTick < ReloadDebounceMs) return;
        _lastReloadTick = now;

        try { Load(); }
        catch { }
    }

    private static bool GetBool(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? val) &&
               val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
