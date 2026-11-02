using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ComicVerse.Tests;

internal static class TestProcessSafety
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Console tests also load WPF/native image and PDF components. Retain
        // failure exit codes without displaying native dialogs on any desktop.
        if (OperatingSystem.IsWindows()) SetErrorMode(GetErrorMode() | 0x8003u);
    }

    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}
