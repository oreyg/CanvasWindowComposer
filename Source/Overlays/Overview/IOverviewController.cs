using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// The slice of <see cref="OverviewManager"/> that the orchestrator drives.
/// Lets tests stand in a fake without spinning up D3D11 + per-monitor forms.
/// </summary>
internal interface IOverviewController
{
    event Action<OverviewMode, OverviewMode>? BeforeModeChanged;
    IReadOnlyList<IntPtr> MonitorHandles { get; }
    OverviewMode CurrentMode { get; }
    void TransitionTo(OverviewMode target, bool syncCameraOnClose = true);
    void CancelInertia();
    void SyncCamera();
    void RecordPanDelta(int dx, int dy);
    void ReleaseInertia();
}
