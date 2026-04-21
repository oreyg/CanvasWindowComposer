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
        input.EscPressed           += OnEscPressed;
    }

    private void OnCanvasCameraChanged()
    {
        _overview.SyncCamera();
    }

    private void OnOverviewModeChanged(OverviewMode from, OverviewMode to)
    {
        if (to == OverviewMode.Panning)
            _input.SetExtraPanSurfaces(_overview.MonitorHandles);
        else
            _input.ClearExtraPanSurfaces();

        // Panning overlay is WS_EX_TRANSPARENT; without a low-level block a
        // middle click during panning would reach whatever app is under the
        // cursor (Chrome closes tabs, etc.).
        if (to == OverviewMode.Panning)
            _input.EnableMiddleButtonBlock();
        else if (from == OverviewMode.Panning)
            _input.DisableMiddleButtonBlock();

        // Esc closes Zooming via a global hotkey rather than form KeyDown:
        // only _passes[0] gets Activate() and keyboard focus can drift in
        // multi-monitor setups, so the form-level handler isn't reliable.
        // Panning is by design — Esc is left alone there.
        if (to == OverviewMode.Zooming)
            _input.EnableEscHotkey();
        else if (from == OverviewMode.Zooming)
            _input.DisableEscHotkey();
    }

    private void OnEscPressed()
    {
        if (_overview.CurrentMode == OverviewMode.Zooming)
            _overview.TransitionTo(OverviewMode.Hidden);
    }

    private void OnDragStarted()
    {
        _overview.TransitionTo(OverviewMode.Panning);
    }

    private void OnMouseButtonDown()
    {
        // A non-pan click while the panning overview is up — close it so the
        // click interacts with the underlying window normally.
        if (_overview.CurrentMode == OverviewMode.Panning)
            _overview.TransitionTo(OverviewMode.Hidden);
    }

    private void OnOverviewHotkey()
    {
        if (_overview.CurrentMode == OverviewMode.Zooming)
            _overview.TransitionTo(OverviewMode.Hidden);
        else
            _overview.TransitionTo(OverviewMode.Zooming);
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
            if (_overview.CurrentMode == OverviewMode.Zooming)
                _overview.TransitionTo(OverviewMode.Hidden);
            else
                _overview.TransitionTo(OverviewMode.Zooming);
        }
    }
}
