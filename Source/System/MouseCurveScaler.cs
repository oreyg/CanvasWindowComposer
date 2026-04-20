using System;
using Microsoft.Win32;

namespace CanvasDesktop;

/// <summary>
/// Applies the Windows mouse acceleration curve and speed setting to raw
/// mouse deltas. Without this, raw HID deltas don't match the cursor speed
/// the user has dialed in via Control Panel; pan feel diverges from cursor
/// feel.
///
/// Reads SPI_GETMOUSESPEED (1..20, default 10) for the linear speed factor
/// and SPI_GETMOUSE for the "Enhance pointer precision" toggle. When enhance
/// is on, additionally reads HKCU\Control Panel\Mouse\SmoothMouse(X|Y)Curve
/// for the 5-point piecewise-linear curve.
///
/// All math is in 16.16 fixed point. A residual carries sub-pixel fractions
/// between calls so slow motion (raw dx=1 producing scaled output &lt; 1) still
/// accumulates to a whole pixel over time instead of being rounded to zero.
///
/// Single-producer (raw input polling thread). Not thread-safe; do not call
/// <see cref="Apply"/> from multiple threads.
/// </summary>
internal sealed class MouseCurveScaler
{
    // Win32 constants (not in CsWin32-generated SYSTEM_PARAMETERS_INFO_ACTION enum)
    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_GETMOUSESPEED = 0x0070;

    // 5-point piecewise-linear curve. Defaults match Windows 8+ if registry read fails.
    private readonly long[] _xs = new long[5];
    private readonly long[] _ys = new long[5];

    private uint _dpiscale = 32;
    private uint _dpidenom = (10u * 120u) << 16; // Win8+ default
    private bool _enhanced;
    private int _lastNode;

    // Sub-pixel residuals (16.16 fixed)
    private long _residualX;
    private long _residualY;

    public MouseCurveScaler()
    {
        Refresh();
    }

    /// <summary>
    /// Reset per-gesture state (sub-pixel residuals + last curve node) without
    /// re-reading SPI/registry. Call between distinct user gestures (e.g. on
    /// drag end) so a fractional pixel left over from the previous gesture
    /// can't bias the first event of the next one.
    /// </summary>
    public void ResetGestureState()
    {
        _lastNode = 0;
        _residualX = 0;
        _residualY = 0;
    }

    /// <summary>
    /// Re-read SPI / registry settings. Call after WM_SETTINGCHANGE if you
    /// want to track live changes; constructor calls it once.
    /// </summary>
    public unsafe void Refresh()
    {
        _residualX = 0;
        _residualY = 0;
        _dpiscale = 32;
        _dpidenom = (10u * 120u) << 16;
        _enhanced = false;

        int v = 10;
        if (PInvoke.SystemParametersInfo((SYSTEM_PARAMETERS_INFO_ACTION)SPI_GETMOUSESPEED, 0, &v, 0))
        {
            v = Math.Max(1, Math.Min(v, 20));
            _dpiscale = (uint)Math.Max(Math.Max(v, (v - 2) * 4), (v - 6) * 8);
        }

        int* spiParams = stackalloc int[3];
        if (PInvoke.SystemParametersInfo((SYSTEM_PARAMETERS_INFO_ACTION)SPI_GETMOUSE, 0, spiParams, 0))
        {
            _enhanced = spiParams[2] != 0;
            if (_enhanced)
                ReadMouseCurve(v);
        }
    }

    /// <summary>
    /// Transform raw HID deltas into screen-space deltas matching the cursor.
    /// Whole-pixel output; sub-pixel fractions carried in <see cref="_residualX"/> /
    /// <see cref="_residualY"/> for the next call.
    ///
    /// <paramref name="chunks"/> is how many native HID polls this event is
    /// estimated to represent. Caller derives it from the time gap since the
    /// previous event. Windows applies its curve per-HID-poll, so when the OS
    /// coalesces several per-tick reports into one event with a summed lLastX,
    /// applying the curve once to that big delta lands it in a high-speed band
    /// and over-amplifies vs. the cursor (which got modest per-tick amplification
    /// repeated). Splitting the delta into <paramref name="chunks"/> equal sub-
    /// events and applying the curve per sub-event mirrors what Windows did to
    /// the cursor. Pass <c>1</c> for genuinely-fast events (no coalescing) so
    /// the curve fully amplifies as Windows would have.
    /// </summary>
    public void Apply(int rawDx, int rawDy, int chunks, out int outDx, out int outDy)
    {
        int absDx = Math.Abs(rawDx);
        int absDy = Math.Abs(rawDy);
        int dominant = Math.Max(absDx, absDy);
        if (dominant == 0)
        {
            outDx = 0;
            outDy = 0;
            return;
        }

        // Splitting finer than 1 unit per chunk doesn't help — sub-pixel chunks
        // all land in band 0 anyway. Cap to the dominant-axis magnitude.
        int n = Math.Max(1, Math.Min(chunks, dominant));
        if (n == 1)
        {
            ApplySingle(rawDx, rawDy, out outDx, out outDy);
            return;
        }

        int signX = Math.Sign(rawDx);
        int signY = Math.Sign(rawDy);
        int dxPer = absDx / n, dxRem = absDx % n;
        int dyPer = absDy / n, dyRem = absDy % n;

        outDx = 0;
        outDy = 0;
        for (int i = 0; i < n; i++)
        {
            int cx = (dxPer + (i < dxRem ? 1 : 0)) * signX;
            int cy = (dyPer + (i < dyRem ? 1 : 0)) * signY;
            ApplySingle(cx, cy, out int oDx, out int oDy);
            outDx += oDx;
            outDy += oDy;
        }
    }

    private void ApplySingle(int rawDx, int rawDy, out int outDx, out int outDy)
    {
        long ix = (long)rawDx << 16;
        long iy = (long)rawDy << 16;

        if (!_enhanced)
        {
            // Flat scale. Skip DPI factor — we don't have a per-monitor DPI
            // story here yet; on hi-DPI displays the curve will be ~1.x slow.
            ix *= _dpiscale;
            iy *= _dpiscale;
            ix /= 32;
            iy /= 32;
        }
        else
        {
            ApplyEnhancedCurve(ref ix, ref iy);
        }

        // Accumulate sub-pixel residual; emit whole pixels.
        _residualX += ix;
        _residualY += iy;
        outDx = (int)(_residualX >> 16);
        outDy = (int)(_residualY >> 16);
        _residualX -= ((long)outDx) << 16;
        _residualY -= ((long)outDy) << 16;
    }

    // The curve output must be multiplied by the actual display DPI (e.g. 96
    // at 100% scaling, 144 at 150%). The 96 isn't optional padding — it's a
    // load-bearing factor that the rest of the math (denom, slope, intercept)
    // is calibrated against. Without it, output is ~96x too small.
    // We hardcode 96 (USER_DEFAULT_SCREEN_DPI) for now; pan will be ~1.5x slow
    // on a 144 DPI / 150% display. Real per-monitor DPI tracking is a TODO.
    private const long ScreenDpi = 96;

    private void ApplyEnhancedCurve(ref long ix, ref long iy)
    {
        long absx = Math.Abs(ix);
        long absy = Math.Abs(iy);
        // Approximation Windows itself uses instead of true
        // vector magnitude — keep it as-is so behavior matches the OS cursor.
        long speed = Math.Min(absx, absy) + (Math.Max(absx, absy) << 1);
        if (speed == 0) return;

        int j = 1;
        for (int i = 1; i < 5; i++)
        {
            j = i;
            if (speed < _xs[j]) break;
        }
        int idx = j - 1;
        int prevNode = _lastNode;
        _lastNode = idx;

        uint denom = _dpidenom;
        long scale = 0;
        long xdiff = _xs[idx + 1] - _xs[idx];
        long ydiff = _ys[idx + 1] - _ys[idx];
        if (xdiff != 0)
        {
            long slope = ydiff / xdiff;
            long inter = slope * _xs[idx] - _ys[idx];
            scale += slope - inter / speed;
        }

        // If we crossed a curve node since the last sample, fold in the previous
        // node's contribution too — sub-pixel accumulation across the boundary.
        if (idx > prevNode)
        {
            denom <<= 1;
            xdiff = _xs[prevNode + 1] - _xs[prevNode];
            ydiff = _ys[prevNode + 1] - _ys[prevNode];
            if (xdiff != 0)
            {
                long slope = ydiff / xdiff;
                long inter = slope * _xs[prevNode] - _ys[prevNode];
                scale += slope - inter / speed;
            }
        }

        scale *= ScreenDpi;
        ix *= scale;
        iy *= scale;
        ix /= denom;
        iy /= denom;
    }

    private void ReadMouseCurve(int v)
    {
        // Windows 8+ default curve. Pre-Win8 used different defaults; not
        // detected here since anything running .NET 8 on Windows is post-Win8.
        uint[] xbuff = {
            0x00000000, 0,
            0x00006e15, 0,
            0x00014000, 0,
            0x0003dc29, 0,
            0x00280000, 0
        };
        uint[] ybuff = {
            0x00000000, 0,
            0x000111fd, 0,
            0x00042400, 0,
            0x0012fc00, 0,
            0x01bbc000, 0
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            if (key != null)
            {
                if (key.GetValue("SmoothMouseXCurve") is byte[] xbytes && xbytes.Length >= 40)
                    BlitToUInts(xbytes, xbuff);
                if (key.GetValue("SmoothMouseYCurve") is byte[] ybytes && ybytes.Length >= 40)
                    BlitToUInts(ybytes, ybuff);
            }
        }
        catch
        {
            // Registry unavailable — fall through to defaults.
        }

        _xs[0] = 0;
        _ys[0] = 0;
        for (int i = 1; i < 5; i++)
        {
            _xs[i] = 7L * xbuff[i * 2];
            _ys[i] = ((long)v * ybuff[i * 2]) << 17;
        }
    }

    private static void BlitToUInts(byte[] src, uint[] dst)
    {
        int n = Math.Min(dst.Length, src.Length / 4);
        for (int i = 0; i < n; i++)
        {
            dst[i] = (uint)(src[i * 4]
                          | (src[i * 4 + 1] << 8)
                          | (src[i * 4 + 2] << 16)
                          | (src[i * 4 + 3] << 24));
        }
    }
}
