using System;
using System.IO.MemoryMappedFiles;

namespace CanvasDesktop;

/// <summary>
/// Named shared memory region shared with DpiHook.dll.
/// Layout: [uint hostPid (4 bytes)] [double scale (8 bytes)]
/// </summary>
internal sealed class ZoomSharedMemory : IDisposable
{
    private const string MapName = "CanvasDesktopZoom";
    private const int Size = sizeof(uint) + sizeof(double); // 12 bytes
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public ZoomSharedMemory()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, Size);
        _accessor = _mmf.CreateViewAccessor(0, Size);
        _accessor.Write(0, (uint)Environment.ProcessId);
        WriteScale(1.0);
    }

    public void WriteScale(double scale)
    {
        _accessor.Write(sizeof(uint), scale);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
