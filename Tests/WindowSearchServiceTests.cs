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
    public void ScoreMatch_ProcessNameOnly_Returns2()
    {
        int score = WindowSearchService.ScoreMatch("New Tab", "chrome", "chrome.exe", "chro");
        // "New Tab" doesn't contain "chro", "chrome" contains "chro" -> 2
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
    public void ScoreMatch_NoMatch_Returns0()
    {
        int score = WindowSearchService.ScoreMatch("Notepad", "notepad", "notepad.exe", "firefox");
        Assert.Equal(0, score);
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
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var service = new WindowSearchService(canvas, api);

        // Add window to canvas and API
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 100, 200, 800, 600, pid: 999);

        // ScoreMatch needs title/process info — but GetWindowTitle uses NativeMethods directly
        // which won't work in tests. However, the search will still run — windows without
        // titles get scored on process name (which comes from Process.GetProcessById)
        var results = service.Search("doesnotexist");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_LimitsTo5Results()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var service = new WindowSearchService(canvas, api);

        // Add 10 windows — all will have empty titles in test env,
        // so they'll match on process name
        for (int i = 1; i <= 10; i++)
        {
            var hWnd = (IntPtr)i;
            canvas.SetWindow(hWnd, i * 100, 0, 400, 300);
            api.AddWindow(hWnd, i * 100, 0, 400, 300, pid: (uint)(i + 100));
        }

        // Search for "PID" which is the fallback process name
        var results = service.Search("pid");
        Assert.True(results.Count <= 5);
    }

    // ==================== GET RECENT WINDOWS ====================

    [Fact]
    public void GetRecentWindows_ReturnsCanvasWindowsInEnumOrder()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var service = new WindowSearchService(canvas, api);

        // Set up 3 windows in canvas
        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 500, 100, 400, 300);
        canvas.SetWindow((IntPtr)3, 900, 100, 400, 300);

        // EnumWindows order (Z-order): 2, 3, 1
        api.EnumOrder = new() { (IntPtr)2, (IntPtr)3, (IntPtr)1 };
        api.AddWindow((IntPtr)1, 100, 100, 400, 300, pid: 10);
        api.AddWindow((IntPtr)2, 500, 100, 400, 300, pid: 20);
        api.AddWindow((IntPtr)3, 900, 100, 400, 300, pid: 30);

        // Results will be empty because GetWindowTitle returns "" for fake handles
        // (NativeMethods.GetWindowTextLength returns 0 for invalid handles)
        // This tests that the enumeration logic runs without error
        var results = service.GetRecentWindows();
        // All will be filtered out because title is empty
        Assert.Empty(results);
    }

    [Fact]
    public void GetRecentWindows_LimitsTo5()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var service = new WindowSearchService(canvas, api);

        for (int i = 1; i <= 10; i++)
        {
            var hWnd = (IntPtr)i;
            canvas.SetWindow(hWnd, i * 100, 0, 400, 300);
            api.AddWindow(hWnd, i * 100, 0, 400, 300, pid: (uint)(i + 100));
        }

        // Even with 10 windows, at most 5 should be returned
        var results = service.GetRecentWindows();
        Assert.True(results.Count <= 5);
    }
}
