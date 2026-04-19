namespace CanvasDesktop;

/// <summary>
/// Wires an <see cref="IOverviewController"/> to its input/canvas signals
/// (drag, hotkey, button, pan/zoom drains, camera sync, pan-surface registration).
/// Lives in its own class so the binding logic can be exercised against a
/// fake controller without instantiating the real form-bound OverviewManager.
/// </summary>
internal sealed class OverviewInputs
{
    private readonly IOverviewController _overview;
    private readonly IInputRouter _input;
    private readonly Canvas _canvas;

    public OverviewInputs(IOverviewController overview, IInputRouter input, Canvas canvas)
    {
        _overview = overview;
        _input = input;
        _canvas = canvas;

        canvas.CameraChanged       += OnCanvasCameraChanged;
        overview.BeforeModeChanged += OnOverviewModeChanged;
        input.DragStarted          += OnDragStarted;
        input.ButtonDown           += OnMouseButtonDown;
        input.OverviewHotkey       += OnOverviewHotkey;
        input.InputAvailable       += OnCanvasInput;
    }

    private void OnCanvasCameraChanged()
    {
        _overview.SyncCamera();
    }

    private void OnOverviewModeChanged(OverviewManager.Mode from, OverviewManager.Mode to)
    {
        if (to == OverviewManager.Mode.Panning)
            _input.SetExtraPanSurfaces(_overview.MonitorHandles);
        else
            _input.ClearExtraPanSurfaces();
    }

    private void OnDragStarted()
    {
        _overview.TransitionTo(OverviewManager.Mode.Panning);
    }

    private void OnMouseButtonDown()
    {
        // A non-pan click while the panning overview is up — close it so the
        // click interacts with the underlying window normally.
        if (_overview.CurrentMode == OverviewManager.Mode.Panning)
            _overview.TransitionTo(OverviewManager.Mode.Hidden);
    }

    private void OnOverviewHotkey()
    {
        if (_overview.CurrentMode == OverviewManager.Mode.Zooming)
            _overview.TransitionTo(OverviewManager.Mode.Hidden);
        else
            _overview.TransitionTo(OverviewManager.Mode.Zooming);
    }

    private void OnCanvasInput()
    {
        if (_input.TryDrainPanDelta(out int dx, out int dy))
        {
            _canvas.Pan(dx, dy);
            _overview.RecordPanDelta(dx, dy);
        }

        if (_input.TryDrainDragEnded())
            _overview.ReleaseInertia();

        if (_input.TryDrainZoom())
        {
            if (_overview.CurrentMode == OverviewManager.Mode.Zooming)
                _overview.TransitionTo(OverviewManager.Mode.Hidden);
            else
                _overview.TransitionTo(OverviewManager.Mode.Zooming);
        }
    }
}
