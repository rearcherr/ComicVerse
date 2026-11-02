using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ComicVerse.App;

public partial class App
{
    private static readonly bool IsSmokeProcess = Environment.GetCommandLineArgs().Contains("--smoke");

    static App()
    {
        // CLR hosts may reset inherited error modes. Apply inside the actual test
        // process before WPF constructs App resources; regular interactive runs keep
        // their usual error reporting. No registry or system-wide setting is changed.
        if (IsSmokeProcess) ConfigureSmokeErrorMode();
    }

    private static void ConfigureSmokeErrorMode()
        => SmokeNative.SetErrorMode(SmokeNative.GetErrorMode() | 0x8003u);

    private static void RecordSmokeFailure(Exception exception)
    {
        try
        {
            string output = Environment.GetEnvironmentVariable("SMOKE_OUT_DIR")
                ?? Path.Combine(Path.GetTempPath(), "comicverse-smoke");
            Directory.CreateDirectory(output);
            File.AppendAllText(Path.Combine(output, "smoke-crash.txt"), exception + Environment.NewLine);
        }
        catch { }
    }

    internal static object GetSmokeProcessEnvironment()
    {
        var name = new StringBuilder(256);
        bool hasName = SmokeNative.GetUserObjectInformationW(
            SmokeNative.GetThreadDesktop(SmokeNative.GetCurrentThreadId()), 2, name, 512, out _);
        return new
        {
            ProcessId = Environment.ProcessId,
            Desktop = hasName ? name.ToString() : "unknown",
            ErrorMode = SmokeNative.GetErrorMode(),
            SmokeProcess = IsSmokeProcess
        };
    }

    private static class SmokeNative
    {
        [DllImport("kernel32.dll")] internal static extern uint GetErrorMode();
        [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, uint size, out uint needed);
    }
}
