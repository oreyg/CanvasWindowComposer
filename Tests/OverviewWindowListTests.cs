using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewWindowListTests
{
    private static (Canvas canvas, FakeWindowApi api, OverviewWindowList list) Make()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var list = new OverviewWindowList(canvas, api);
        return (canvas, api, list);
    }

    [Fact]
    public void Refresh_PopulatesFromCanvasInZOrder()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 500, 100, 400, 300);
        canvas.SetWindow((IntPtr)3, 900, 100, 400, 300);
        api.AddWindow((IntPtr)1, 100, 100, 400, 300);
        api.AddWindow((IntPtr)2, 500, 100, 400, 300);
        api.AddWindow((IntPtr)3, 900, 100, 400, 300);
        // EnumWindows order = topmost first
        api.EnumOrder = new() { (IntPtr)2, (IntPtr)3, (IntPtr)1 };

        list.Refresh();

        Assert.Equal(3, list.Count);
        Assert.Equal((IntPtr)2, list.Windows[0].HWnd);
        Assert.Equal((IntPtr)3, list.Windows[1].HWnd);
        Assert.Equal((IntPtr)1, list.Windows[2].HWnd);
    }

    [Fact]
    public void Refresh_SkipsCollapsedCanvasWindows()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 500, 100, 400, 300);
        canvas.CollapseWindow((IntPtr)1);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);

        list.Refresh();

        Assert.Single(list.Windows);
        Assert.Equal((IntPtr)2, list.Windows[0].HWnd);
    }

    [Fact]
    public void Refresh_SkipsWindowsNotInCanvas()
    {
        var (_, api, list) = Make();
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);

        list.Refresh();

        Assert.Empty(list.Windows);
    }

    [Fact]
    public void Refresh_ResetsSelection()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        list.Refresh();
        list.SelectNext();
        Assert.Equal(0, list.SelectedIndex);

        list.Refresh();
        Assert.Equal(-1, list.SelectedIndex);
    }

    [Fact]
    public void SelectNext_WrapsAround()
    {
        var (canvas, api, list) = Make();
        for (int i = 1; i <= 3; i++)
        {
            canvas.SetWindow((IntPtr)i, 0, 0, 400, 300);
            api.AddWindow((IntPtr)i, 0, 0, 400, 300);
        }
        list.Refresh();

        list.SelectNext(); Assert.Equal(0, list.SelectedIndex);
        list.SelectNext(); Assert.Equal(1, list.SelectedIndex);
        list.SelectNext(); Assert.Equal(2, list.SelectedIndex);
        list.SelectNext(); Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void SelectPrev_WrapsBackward()
    {
        var (canvas, api, list) = Make();
        for (int i = 1; i <= 3; i++)
        {
            canvas.SetWindow((IntPtr)i, 0, 0, 400, 300);
            api.AddWindow((IntPtr)i, 0, 0, 400, 300);
        }
        list.Refresh();
        list.SelectNext(); // index 0
        Assert.Equal(0, list.SelectedIndex);

        list.SelectPrev(); Assert.Equal(2, list.SelectedIndex);
        list.SelectPrev(); Assert.Equal(1, list.SelectedIndex);
    }

    [Fact]
    public void SelectPrev_FromUnselected_LandsOnLastIndex()
    {
        var (canvas, api, list) = Make();
        for (int i = 1; i <= 3; i++)
        {
            canvas.SetWindow((IntPtr)i, 0, 0, 400, 300);
            api.AddWindow((IntPtr)i, 0, 0, 400, 300);
        }
        list.Refresh();
        Assert.Equal(-1, list.SelectedIndex);

        list.SelectPrev();
        Assert.Equal(2, list.SelectedIndex);
    }

    [Fact]
    public void SelectNext_OnEmptyList_StaysUnselected()
    {
        var (_, _, list) = Make();
        list.SelectNext();
        Assert.Equal(-1, list.SelectedIndex);
    }

    [Fact]
    public void HitTest_InsideRect_ReturnsIndex()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        api.AddWindow((IntPtr)1, 100, 100, 400, 300);
        list.Refresh();

        Assert.Equal(0, list.HitTest(200, 150));
    }

    [Fact]
    public void HitTest_OutsideAllRects_ReturnsMinusOne()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        api.AddWindow((IntPtr)1, 100, 100, 400, 300);
        list.Refresh();

        Assert.Equal(-1, list.HitTest(5000, 5000));
    }

    [Fact]
    public void HitTest_ChecksTopmostFirst()
    {
        var (canvas, api, list) = Make();
        // Two overlapping windows; #2 is topmost
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 400);
        canvas.SetWindow((IntPtr)2, 0, 0, 400, 400);
        api.AddWindow((IntPtr)1, 0, 0, 400, 400);
        api.AddWindow((IntPtr)2, 0, 0, 400, 400);
        api.EnumOrder = new() { (IntPtr)2, (IntPtr)1 };
        list.Refresh();

        // (200, 200) is inside both — topmost (#2) wins, which is index 0
        Assert.Equal(0, list.HitTest(200, 200));
        Assert.Equal((IntPtr)2, list.Windows[0].HWnd);
    }

    [Fact]
    public void MoveToFront_MovesIndexToZero()
    {
        var (canvas, api, list) = Make();
        for (int i = 1; i <= 3; i++)
        {
            canvas.SetWindow((IntPtr)i, 0, 0, 400, 300);
            api.AddWindow((IntPtr)i, 0, 0, 400, 300);
        }
        api.EnumOrder = new() { (IntPtr)1, (IntPtr)2, (IntPtr)3 };
        list.Refresh();

        list.MoveToFront(2);

        Assert.Equal((IntPtr)3, list.Windows[0].HWnd);
        Assert.Equal((IntPtr)1, list.Windows[1].HWnd);
        Assert.Equal((IntPtr)2, list.Windows[2].HWnd);
    }

    [Fact]
    public void MoveToFront_OnIndexZero_IsNoOp()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        canvas.SetWindow((IntPtr)2, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);
        list.Refresh();

        list.MoveToFront(0);

        Assert.Equal((IntPtr)1, list.Windows[0].HWnd);
        Assert.Equal((IntPtr)2, list.Windows[1].HWnd);
    }

    [Fact]
    public void TranslateAt_ShiftsWorldPosition()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        api.AddWindow((IntPtr)1, 100, 100, 400, 300);
        list.Refresh();

        list.TranslateAt(0, 50, -25);

        var w = list.Windows[0].World;
        Assert.Equal(150, w.X);
        Assert.Equal(75, w.Y);
        Assert.Equal(400, w.W);
        Assert.Equal(300, w.H);
    }

    [Fact]
    public void Clear_EmptiesAndDeselects()
    {
        var (canvas, api, list) = Make();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        list.Refresh();
        list.SelectNext();

        list.Clear();

        Assert.Equal(0, list.Count);
        Assert.Equal(-1, list.SelectedIndex);
    }
}
