using System;
using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeOverviewController : IOverviewController
{
    public event Action<OverviewManager.Mode, OverviewManager.Mode>? BeforeModeChanged;

    public List<IntPtr> Monitors = new();
    public OverviewManager.Mode CurrentMode { get; private set; } = OverviewManager.Mode.Hidden;

    public int CancelInertiaCalls;
    public int SyncCameraCalls;
    public int ReleaseInertiaCalls;
    public List<(int dx, int dy)> RecordedDeltas = new();
    public List<OverviewManager.Mode> Transitions = new();

    public IReadOnlyList<IntPtr> MonitorHandles { get { return Monitors; } }

    public void TransitionTo(OverviewManager.Mode target, bool syncCameraOnClose = true)
    {
        Transitions.Add(target);
        if (target == CurrentMode) return;
        var from = CurrentMode;
        CurrentMode = target;
        BeforeModeChanged?.Invoke(from, target);
    }

    public void CancelInertia()
    {
        CancelInertiaCalls++;
    }

    public void SyncCamera()
    {
        SyncCameraCalls++;
    }

    public void RecordPanDelta(int dx, int dy)
    {
        RecordedDeltas.Add((dx, dy));
    }

    public void ReleaseInertia()
    {
        ReleaseInertiaCalls++;
    }
}
