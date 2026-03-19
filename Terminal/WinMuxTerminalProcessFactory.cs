using EasyWindowsTerminalControl.Internals;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SelfContainedDeployment.Terminal
{
    internal sealed class WinMuxTerminalProcessFactory : IProcessFactory
    {
        private const int ExtendedStartupInfoPresent = 0x00080000;
        private const int CreateUnicodeEnvironment = 0x00000400;

        private readonly string _workingDirectory;
        private readonly IReadOnlyDictionary<string, string> _environmentVariables;

        public WinMuxTerminalProcessFactory(string workingDirectory, IReadOnlyDictionary<string, string> environmentVariables)
        {
            _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
            _environmentVariables = environmentVariables;
        }

        public event Action<Process> ProcessStarted;

        public IProcess Start(string command, nuint attributes, PseudoConsole console)
        {
            if (console is null)
            {
                throw new ArgumentNullException(nameof(console));
            }

            string commandLineText = string.IsNullOrWhiteSpace(command)
                ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                : command;
            IReadOnlyDictionary<string, string> launchEnvironment = ConPtyConnection.BuildLaunchEnvironment(_workingDirectory, _environmentVariables);

            IntPtr attributeList = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;
            PROCESS_INFORMATION processInfo = default;

            try
            {
                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
                }

                IntPtr pseudoConsoleHandle = console.GetDangerousHandle;
                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)attributes,
                    pseudoConsoleHandle,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
                }

                STARTUPINFOEX startupInfo = new()
                {
                    StartupInfo =
                    {
                        cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    },
                    lpAttributeList = attributeList,
                };

                StringBuilder commandLine = new(commandLineText);
                environmentBlock = ConPtyConnection.BuildEnvironmentBlockPointer(launchEnvironment);

                if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environmentBlock,
                    _workingDirectory,
                    ref startupInfo,
                    out processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
                }

                CloseHandleIfOpen(ref processInfo.hThread);

                Process process = Process.GetProcessById((int)processInfo.dwProcessId);
                process.EnableRaisingEvents = true;
                ProcessStarted?.Invoke(process);
                return new WinMuxProcessHandle(process);
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }

                CloseHandleIfOpen(ref processInfo.hThread);
                CloseHandleIfOpen(ref processInfo.hProcess);
            }
        }

        private static void CloseHandleIfOpen(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return;
            }

            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private sealed class WinMuxProcessHandle : IProcess
        {
            private readonly Process _process;

            public WinMuxProcessHandle(Process process)
            {
                _process = process ?? throw new ArgumentNullException(nameof(process));
            }

            public bool HasExited => _process.HasExited;

            public void WaitForExit() => _process.WaitForExit();

            public void Kill(bool entireProcessTree = false) => _process.Kill(entireProcessTree);

            public void Dispose()
            {
                _process.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
