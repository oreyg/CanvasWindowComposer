using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowSearchServiceTests
{
    // ==================== SCORE MATCH ====================

    [Fact]
    public void ScoreMatch_TitleMatch_Returns3()
    {
        int score = WindowSearchService.ScoreMatch("Google Chrome", "chrome", "chrome.exe", "google");
        Assert.Equal(3, score);
    }

    [Fact]
    public void ScoreMatch_ProcessNameMatch_Returns2()
    {
        int score = WindowSearchService.ScoreMatch("New Tab", "chrome", "chrome.exe", "chrome");
        Assert.Equal(2, score);
    }

    [Fact]
    public void ScoreMatch_ExeNameOnly_Returns1()
    {
        int score = WindowSearchService.ScoreMatch("My Window", "myapp", "myapp.exe", ".exe");
        // title doesn't match, procName doesn't match, exeName matches
        Assert.Equal(1, score);
    }

    [Fact]
    public void ScoreMatch_CaseInsensitive()
    {
        int score = WindowSearchService.ScoreMatch("VISUAL STUDIO CODE", "code", "code.exe", "visual");
        Assert.Equal(3, score);
    }

    [Fact]
    public void ScoreMatch_TitleTakesPrecedenceOverProcess()
    {
        // Both title and process contain "code", but title match (3) wins
        int score = WindowSearchService.ScoreMatch("Visual Studio Code", "code", "code.exe", "code");
        Assert.Equal(3, score);
    }

    // ==================== SEARCH ====================

    [Fact]
    public void Search_ReturnsMatchingCanvasWindows()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 100, 200, 800, 600, pid: 999, title: "Notepad - foo.txt");
        api.Processes[999] = ("notepad", "notepad.exe");

        var hits = service.Search("foo");
        Assert.Single(hits);
        Assert.Equal((IntPtr)1, hits[0].HWnd);
        Assert.Equal("Notepad - foo.txt — notepad", hits[0].Display);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, title: "Notepad");
        api.Processes[1] = ("notepad", "notepad.exe");

        Assert.Empty(service.Search("doesnotexist"));
    }

    [Fact]
    public void Search_OrdersByScore_TitleBeatsProcess()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        canvas.SetWindow((IntPtr)2, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, title: "Some Random Title");
        api.AddWindow((IntPtr)2, 0, 0, 400, 300, pid: 2, title: "chrome - new tab");
        api.Processes[1] = ("chrome", "chrome.exe");  // process matches "chrome"
        api.Processes[2] = ("explorer", "explorer.exe");  // title matches "chrome"

        var hits = service.Search("chrome");
        Assert.Equal(2, hits.Count);
        Assert.Equal((IntPtr)2, hits[0].HWnd); // title-match wins
        Assert.Equal((IntPtr)1, hits[1].HWnd);
    }

    [Fact]
    public void Search_SkipsWindowsWithEmptyTitle()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, title: ""); // no title
        api.Processes[1] = ("matchme", "matchme.exe");

        Assert.Empty(service.Search("matchme"));
    }

    [Fact]
    public void Search_SkipsNonManageableWindows()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, manageable: false, title: "Hidden");
        api.Processes[1] = ("ghost", "ghost.exe");

        Assert.Empty(service.Search("hidden"));
    }

    [Fact]
    public void Search_LimitsTo5Results()
    {
        var (canvas, api, service) = MakeService();
        for (int i = 1; i <= 10; i++)
        {
            var hWnd = (IntPtr)i;
            canvas.SetWindow(hWnd, 0, 0, 400, 300);
            api.AddWindow(hWnd, 0, 0, 400, 300, pid: (uint)i, title: $"win {i} - foo");
            api.Processes[(uint)i] = ("ignored", "ignored.exe");
        }

        var hits = service.Search("foo");
        Assert.Equal(5, hits.Count);
    }

    [Fact]
    public void Search_ReturnsWorldRectFromCanvasIfTracked()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 123, 456, 800, 600);
        api.AddWindow((IntPtr)1, 123, 456, 800, 600, pid: 1, title: "match");
        api.Processes[1] = ("p", "p.exe");

        var hits = service.Search("match");
        Assert.Single(hits);
        Assert.Equal(123, hits[0].World.X);
        Assert.Equal(456, hits[0].World.Y);
    }

    // ==================== GET RECENT WINDOWS ====================

    [Fact]
    public void GetRecentWindows_ReturnsCanvasWindowsInZOrder()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        canvas.SetWindow((IntPtr)2, 0, 0, 400, 300);
        canvas.SetWindow((IntPtr)3, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, title: "first");
        api.AddWindow((IntPtr)2, 0, 0, 400, 300, pid: 2, title: "second");
        api.AddWindow((IntPtr)3, 0, 0, 400, 300, pid: 3, title: "third");
        api.EnumOrder = new() { (IntPtr)2, (IntPtr)3, (IntPtr)1 };
        api.Processes[1] = ("a", ""); api.Processes[2] = ("b", ""); api.Processes[3] = ("c", "");

        var results = service.GetRecentWindows();
        Assert.Equal(3, results.Count);
        Assert.Equal((IntPtr)2, results[0].HWnd);
        Assert.Equal((IntPtr)3, results[1].HWnd);
        Assert.Equal((IntPtr)1, results[2].HWnd);
    }

    [Fact]
    public void GetRecentWindows_SkipsWindowsNotInCanvas()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, title: "tracked");
        api.AddWindow((IntPtr)2, 0, 0, 400, 300, pid: 2, title: "untracked");
        api.Processes[1] = ("a", ""); api.Processes[2] = ("b", "");

        var results = service.GetRecentWindows();
        Assert.Single(results);
        Assert.Equal((IntPtr)1, results[0].HWnd);
    }

    [Fact]
    public void GetRecentWindows_LimitsTo5()
    {
        var (canvas, api, service) = MakeService();
        for (int i = 1; i <= 10; i++)
        {
            canvas.SetWindow((IntPtr)i, 0, 0, 400, 300);
            api.AddWindow((IntPtr)i, 0, 0, 400, 300, pid: (uint)i, title: $"w{i}");
            api.Processes[(uint)i] = ("p", "p.exe");
        }

        var results = service.GetRecentWindows();
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public void GetRecentWindows_SkipsEmptyTitle()
    {
        var (canvas, api, service) = MakeService();
        canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300, pid: 1, title: "");
        api.Processes[1] = ("p", "p.exe");

        Assert.Empty(service.GetRecentWindows());
    }

    private static (Canvas canvas, FakeWindowApi api, WindowSearchService service) MakeService()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var service = new WindowSearchService(canvas, api);
        return (canvas, api, service);
    }
}
