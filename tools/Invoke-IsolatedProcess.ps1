param(
    [Parameter(Mandatory = $true)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [string]$WorkingDirectory = (Get-Location).Path,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
# No SwitchDesktop, input injection, registry changes or machine-wide WER settings.
# A separate desktop contains windows. Set the initial child-tree error mode;
# CLR hosts can reset it, so ComicVerse --smoke also reapplies it inside the app.
# Nonzero exits and timeouts always remain failures.
if (-not ('ComicVerse.Tools.IsolatedProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ComicVerse.Tools {
    public sealed class IsolatedProcess : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {
            public int Length; public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo {
            public int Size; public string Reserved; public string Desktop; public string Title;
            public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public ushort Show; public ushort ReservedSize;
            public IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {
            public IntPtr Process, Thread; public uint ProcessId, ThreadId;
        }
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
            public long ProcessTime, JobTime; public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses;
            public UIntPtr Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {
            public BasicLimits Basic; public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern uint GetErrorMode();
        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string workingDirectory, ref StartupInfo startup, out ProcessInfo process);

        IntPtr desktop, job, process;
        public uint Id { get; private set; }
        public string DesktopName { get; private set; }
        public uint ChildErrorMode { get; private set; }
        public bool Wait(int milliseconds) {
            uint result = WaitForSingleObject(process, (uint)milliseconds);
            if (result == 0) return true;
            if (result == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait failed");
        }
        public int ExitCode {
            get { uint code; if (!GetExitCodeProcess(process, out code)) throw new Win32Exception(); return unchecked((int)code); }
        }
        public void Stop() { if (job != IntPtr.Zero) TerminateJobObject(job, 124); }
        static string Quote(string value) {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
        static IntPtr OpenLog(string path, bool read) {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
            IntPtr handle = CreateFileW(path, read ? 0x80000000u : 0x40000000u, 3, ref attributes, read ? 3u : 2u, 0x80, IntPtr.Zero);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open process log");
            return handle;
        }
        public IsolatedProcess(string executable, string[] arguments, string workingDirectory, string stdout, string stderr) {
            IntPtr input = IntPtr.Zero, output = IntPtr.Zero, error = IntPtr.Zero, thread = IntPtr.Zero;
            try {
                DesktopName = "ComicVerse-QA-" + Guid.NewGuid().ToString("N");
                desktop = CreateDesktopW(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, 0xC7, IntPtr.Zero);
                if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create isolated desktop; refusing default-desktop fallback");
                job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create bounded job");
                var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits)))) throw new Win32Exception();
                input = OpenLog("NUL", true); output = OpenLog(stdout, false); error = OpenLog(stderr, false);
                var startup = new StartupInfo {
                    Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = DesktopName, Flags = 0x101, Show = 0,
                    Input = input, Output = output, Error = error
                };
                var command = new StringBuilder(Quote(executable));
                foreach (string argument in arguments) command.Append(' ').Append(Quote(argument));
                ProcessInfo info;
                uint previous = GetErrorMode();
                ChildErrorMode = previous | 0x8003u; // critical/file/native fault dialogs: this child tree only
                SetErrorMode(ChildErrorMode);
                bool started; int lastError;
                try {
                    started = CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x08000004u, IntPtr.Zero, workingDirectory, ref startup, out info); // NO_WINDOW | SUSPENDED
                    lastError = Marshal.GetLastWin32Error();
                } finally { SetErrorMode(previous); }
                if (!started) throw new Win32Exception(lastError, "Cannot start isolated child");
                process = info.Process; thread = info.Thread; Id = info.ProcessId;
                if (!AssignProcessToJobObject(job, process)) {
                    int assignmentError = Marshal.GetLastWin32Error(); TerminateProcess(process, 124);
                    throw new Win32Exception(assignmentError, "Cannot bound child lifetime; refusing uncontained test");
                }
                if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            } catch { Dispose(); throw; }
            finally {
                if (thread != IntPtr.Zero) CloseHandle(thread);
                if (input != IntPtr.Zero) CloseHandle(input);
                if (output != IntPtr.Zero) CloseHandle(output);
                if (error != IntPtr.Zero) CloseHandle(error);
            }
        }
        public void Dispose() {
            if (job != IntPtr.Zero) { TerminateJobObject(job, 124); CloseHandle(job); job = IntPtr.Zero; }
            if (process != IntPtr.Zero) { WaitForSingleObject(process, 5000); CloseHandle(process); process = IntPtr.Zero; }
            if (desktop != IntPtr.Zero) { CloseDesktop(desktop); desktop = IntPtr.Zero; }
        }
    }
}
'@
}

$executable = (Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop).Source
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$workingPath = (Resolve-Path -LiteralPath $WorkingDirectory).Path
if (Test-Path -LiteralPath (Join-Path $evidencePath 'execution-start.json')) { throw 'Evidence folder already contains a run; choose a fresh folder.' }
New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null
$dataPath = Join-Path $evidencePath 'data'
$smokePath = Join-Path $evidencePath 'smoke'
New-Item -ItemType Directory -Path $dataPath, $smokePath -Force | Out-Null
$priorData = $env:COMICVERSE_DATA_DIR
$priorSmoke = $env:SMOKE_OUT_DIR
$env:COMICVERSE_DATA_DIR = $dataPath
$env:SMOKE_OUT_DIR = $smokePath
$started = [DateTimeOffset]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$child = $null
try {
    $child = [ComicVerse.Tools.IsolatedProcess]::new($executable, $ArgumentList, $workingPath,
        (Join-Path $evidencePath 'stdout.log'), (Join-Path $evidencePath 'stderr.log'))
    @{
        StartedUtc=$started; Pid=$child.Id; Executable=$executable; Arguments=$ArgumentList
        Desktop=$child.DesktopName; ErrorMode=$child.ChildErrorMode; TimeoutSeconds=$TimeoutSeconds
        DataDirectory=$dataPath; WorkingDirectory=$workingPath
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidencePath 'execution-start.json') -Encoding UTF8
    Write-Output ("Isolated child PID {0}, desktop {1}, limit {2}s" -f $child.Id, $child.DesktopName, $TimeoutSeconds)
    $timedOut = $false
    while (-not $child.Wait(1000)) {
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $timedOut = $true; $child.Stop(); $child.Wait(5000) | Out-Null; break }
        if ([int]$timer.Elapsed.TotalSeconds % 15 -eq 0) { Write-Output ('Still running: {0:N1}s' -f $timer.Elapsed.TotalSeconds) }
    }
    $exitCode = $child.ExitCode
    @{
        StartedUtc=$started; EndedUtc=[DateTimeOffset]::UtcNow; Pid=$child.Id
        ExitCode=$exitCode; TimedOut=$timedOut; ElapsedSeconds=$timer.Elapsed.TotalSeconds
        Desktop=$child.DesktopName; ErrorMode=$child.ChildErrorMode
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidencePath 'execution-result.json') -Encoding UTF8
    Write-Output ('Child exit {0}, timedOut={1}, elapsed {2:N1}s' -f $exitCode, $timedOut, $timer.Elapsed.TotalSeconds)
    if ($timedOut) { exit 124 }
    exit $exitCode
} finally {
    if ($null -ne $child) { $child.Dispose() }
    $env:COMICVERSE_DATA_DIR = $priorData
    $env:SMOKE_OUT_DIR = $priorSmoke
}
