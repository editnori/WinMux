using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;

namespace SelfContainedDeployment
{
    internal static class TerminalNative
    {
        internal const int ProcThreadAttributePseudoConsole = 0x00020016;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal const uint Infinite = 0xFFFFFFFF;
        internal const uint WaitObject0 = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Coord
        {
            internal short X;
            internal short Y;

            internal Coord(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int nLength;
            internal IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int cb;
            internal string lpReserved;
            internal string lpDesktop;
            internal string lpTitle;
            internal int dwX;
            internal int dwY;
            internal int dwXSize;
            internal int dwYSize;
            internal int dwXCountChars;
            internal int dwYCountChars;
            internal int dwFillAttribute;
            internal int dwFlags;
            internal short wShowWindow;
            internal short cbReserved2;
            internal IntPtr lpReserved2;
            internal IntPtr hStdInput;
            internal IntPtr hStdOutput;
            internal IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr hProcess;
            internal IntPtr hThread;
            internal int dwProcessId;
            internal int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SecurityAttributes lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(
            Coord size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll", SetLastError = false)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = false)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool CreateProcessW(
            string lpApplicationName,
            IntPtr lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref StartupInfoEx lpStartupInfo,
            out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        internal static SafeFileHandle CreateOwnedHandle(IntPtr handle)
        {
            return new SafeFileHandle(handle, ownsHandle: true);
        }
    }
}
