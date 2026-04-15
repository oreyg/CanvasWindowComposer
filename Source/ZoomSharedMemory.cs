using System;
using System.IO.MemoryMappedFiles;

namespace CanvasDesktop;

/// <summary>
/// Named shared memory region that stores the zoom scale factor (a double).
/// Written by CanvasDesktop.exe, read by DpiHook.dll in target processes.
/// </summary>
internal sealed class ZoomSharedMemory : IDisposable
{
    private const string MapName = "CanvasDesktopZoom";
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public ZoomSharedMemory()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, sizeof(double));
        _accessor = _mmf.CreateViewAccessor(0, sizeof(double));
        Write(1.0); // initial scale
    }

    public void Write(double scale)
    {
        _accessor.Write(0, scale);
    }

    public double Read()
    {
        return _accessor.ReadDouble(0);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
