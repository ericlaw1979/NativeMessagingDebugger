// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Modified from https://github.com/PowerShell/PowerShell/blob/3dc95ced87d38c347a9fc3a222eb4c52eaad4615/src/System.Management.Automation/engine/ProcessCodeMethods.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace nmf_view
{
    /// <summary>
    /// Helper functions for process info.
    /// </summary>
    public static class ProcessInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        internal class PROCESS_BASIC_INFORMATION
        {
            public int ExitStatus = 0;
            public IntPtr PebBaseAddress = (IntPtr)0;
            public IntPtr AffinityMask = (IntPtr)0;
            public int BasePriority = 0;
            public IntPtr UniqueProcessId = (IntPtr)0;
            public IntPtr InheritedFromUniqueProcessId = (IntPtr)0;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        internal static extern int NtQueryInformationProcess(IntPtr processHandle, int query, PROCESS_BASIC_INFORMATION info, int size, int[] returnedSize);

        private const int InvalidProcessId = -1;

        internal static Process GetParent(this Process process)
        {
            try
            {
                var pid = GetParentPid(process);
                if (pid == InvalidProcessId)
                {
                    return null;
                }

                var candidate = Process.GetProcessById(pid);

                // if the candidate was started later than process, the pid has been recycled
                return candidate.StartTime > process.StartTime ? null : candidate;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// CodeMethod for getting the parent process of a process.
        /// </summary>
        /// <param name="process">The process whose parent should be returned</param>
        /// <returns>The parent process, or null if the parent is no longer running.</returns>
        public static object GetParentProcess(Process process)
        {
            return process?.GetParent();
        }

        /// <summary>
        /// Returns the parent id of a process or -1 if it fails.
        /// </summary>
        /// <param name="process"></param>
        /// <returns>The pid of the parent process.</returns>
        internal static int GetParentPid(Process process)
        {
            PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(process.Handle, 0, pbi, (int)Marshal.SizeOf(pbi), null);
            return status != 0 ? InvalidProcessId : pbi.InheritedFromUniqueProcessId.ToInt32();
        }
    }
}