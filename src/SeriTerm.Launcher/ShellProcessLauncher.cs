using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SeriTerm.Launcher;

internal static class ShellProcessLauncher
{
    private const uint TitleIsLinkName = 0x00000800;
    private const uint TitleIsAppId = 0x00001000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    internal static bool TryStartWithShellIdentity(ProcessStartInfo startInfo)
    {
        GetStartupInfo(out var parent);
        var identityFlags = parent.Flags & (TitleIsLinkName | TitleIsAppId);
        if (identityFlags == 0 || parent.Title == IntPtr.Zero)
            return false;

        // Windows 通过 STARTUPINFO 把原快捷方式或 AppUserModelID 交给启动器。
        // 转交给实际创建窗口的进程，使 Explorer 能把窗口归到原来的固定项。
        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
            Title = parent.Title,
            Flags = identityFlags,
        };

        var commandLine = new StringBuilder();
        AppendArgument(commandLine, startInfo.FileName);
        foreach (var argument in startInfo.ArgumentList)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        var environment = new StringBuilder();
        foreach (var (name, value) in startInfo.Environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            environment.Append(name).Append('=').Append(value).Append('\0');
        var environmentBlock = Marshal.StringToCoTaskMemUni(environment.Append('\0').ToString());
        try
        {
            if (!CreateProcess(startInfo.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environmentBlock, startInfo.WorkingDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(environmentBlock);
        }
    }

    // Windows 参数解析要求在引号前转义反斜杠，包括参数末尾紧邻闭引号的反斜杠。
    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        commandLine.Append('"');
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            commandLine.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        commandLine.Append('\\', slashes * 2).Append('"');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public uint ConsoleColumns;
        public uint ConsoleRows;
        public uint ConsoleFill;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedLength;
        public IntPtr ReservedData;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetStartupInfoW", ExactSpelling = true)]
    private static extern void GetStartupInfo(out StartupInfo startupInfo);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern void CloseHandle(IntPtr handle);
}
