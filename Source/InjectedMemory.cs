using System;
using System.IO.MemoryMappedFiles;

namespace CanvasDesktop;

/// <summary>
/// Named shared memory region shared with injected DLLs.
/// Layout: [uint hostPid (4 bytes)]
/// </summary>
internal sealed class InjectedMemory : IDisposable
{
    private const string MapName = "CanvasDesktopZoom";
    private const int Size = sizeof(uint);
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public InjectedMemory()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, Size);
        _accessor = _mmf.CreateViewAccessor(0, Size);
        _accessor.Write(0, (uint)Environment.ProcessId);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
