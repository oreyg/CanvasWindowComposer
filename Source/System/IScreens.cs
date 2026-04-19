using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Left { get { return X; } }
    public int Top { get { return Y; } }
    public int Right { get { return X + Width; } }
    public int Bottom { get { return Y + Height; } }
}

/// <summary>
/// Abstracts monitor topology so layout-dependent code (overview camera,
/// thumbnail source rects, overlay placement) can be tested with synthetic
/// monitor configurations.
/// </summary>
internal interface IScreens
{
    IReadOnlyList<ScreenRect> AllBounds { get; }
    IReadOnlyList<ScreenRect> AllWorkingAreas { get; }
    ScreenRect VirtualScreen { get; }
    ScreenRect PrimaryBounds { get; }
    ScreenRect PrimaryWorkingArea { get; }
}

internal sealed class WinFormsScreens : IScreens
{
    public static readonly WinFormsScreens Instance = new();

    public IReadOnlyList<ScreenRect> AllBounds
    {
        get
        {
            var screens = Screen.AllScreens;
            var list = new ScreenRect[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                list[i] = new ScreenRect(b.X, b.Y, b.Width, b.Height);
            }
            return list;
        }
    }

    public IReadOnlyList<ScreenRect> AllWorkingAreas
    {
        get
        {
            var screens = Screen.AllScreens;
            var list = new ScreenRect[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var w = screens[i].WorkingArea;
                list[i] = new ScreenRect(w.X, w.Y, w.Width, w.Height);
            }
            return list;
        }
    }

    public ScreenRect VirtualScreen
    {
        get
        {
            var vs = SystemInformation.VirtualScreen;
            return new ScreenRect(vs.X, vs.Y, vs.Width, vs.Height);
        }
    }

    public ScreenRect PrimaryBounds
    {
        get
        {
            var b = Screen.PrimaryScreen!.Bounds;
            return new ScreenRect(b.X, b.Y, b.Width, b.Height);
        }
    }

    public ScreenRect PrimaryWorkingArea
    {
        get
        {
            var w = Screen.PrimaryScreen!.WorkingArea;
            return new ScreenRect(w.X, w.Y, w.Width, w.Height);
        }
    }
}
