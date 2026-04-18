using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CanvasDesktop;

/// <summary>
/// Injects/ejects DpiHook.dll into/from target processes via
/// CreateRemoteThread + LoadLibrary/FreeLibrary.
/// </summary>
internal sealed class DllInjector
{
    private const uint RemoteThreadTimeoutMs = 5000;

    private readonly string _dllPath;
    // processId -> remote HMODULE of the injected DLL
    private readonly Dictionary<uint, IntPtr> _injected = new();

    // Process access rights
    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
        uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
        uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out IntPtr lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    public DllInjector()
    {
        _dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DpiHook.dll");
    }

    public bool DllExists => File.Exists(_dllPath);

    /// <summary>
    /// Inject DpiHook.dll into the given process. Returns true if newly injected
    /// or already injected.
    /// </summary>
    public bool Inject(uint processId)
    {
        if (_injected.ContainsKey(processId))
            return true;

        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
        if (hProcess == IntPtr.Zero)
            return false;

        try
        {
            // Write DLL path into target process memory
            byte[] dllPathBytes = Encoding.Unicode.GetBytes(_dllPath + '\0');
            uint pathSize = (uint)dllPathBytes.Length;

            IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, pathSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero)
                return false;

            if (!WriteProcessMemory(hProcess, remoteMem, dllPathBytes, pathSize, out _))
            {
                VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
                return false;
            }

            // Get LoadLibraryW address (same in all processes due to ASLR base)
            IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
            IntPtr loadLibAddr = GetProcAddress(kernel32, "LoadLibraryW");

            // Create remote thread calling LoadLibraryW(dllPath)
            IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                loadLibAddr, remoteMem, 0, out _);

            if (hThread == IntPtr.Zero)
            {
                VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
                return false;
            }

            WaitForSingleObject(hThread, RemoteThreadTimeoutMs);

            // Get the HMODULE of the loaded DLL (returned by LoadLibrary as thread exit code)
            GetExitCodeThread(hThread, out IntPtr remoteModule);

            CloseHandle(hThread);
            VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);

            if (remoteModule == IntPtr.Zero)
                return false;

            _injected[processId] = remoteModule;
            return true;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Eject DpiHook.dll from the given process.
    /// </summary>
    public bool Eject(uint processId)
    {
        if (!_injected.TryGetValue(processId, out IntPtr remoteModule))
            return false;

        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
        if (hProcess == IntPtr.Zero)
            return false;

        try
        {
            IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
            IntPtr freeLibAddr = GetProcAddress(kernel32, "FreeLibrary");

            IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                freeLibAddr, remoteModule, 0, out _);

            if (hThread == IntPtr.Zero)
                return false;

            WaitForSingleObject(hThread, RemoteThreadTimeoutMs);
            CloseHandle(hThread);

            _injected.Remove(processId);
            return true;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>Eject from all injected processes.</summary>
    public void EjectAll()
    {
        foreach (var pid in new List<uint>(_injected.Keys))
            Eject(pid);
    }

    /// <summary>Check if already injected into this process.</summary>
    public bool IsInjected(uint processId) => _injected.ContainsKey(processId);

    /// <summary>All PIDs we've injected into.</summary>
    public IReadOnlyCollection<uint> InjectedPids => _injected.Keys;
}
