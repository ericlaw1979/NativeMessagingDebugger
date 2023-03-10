using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

namespace nmf_view
{
    class Utilities
    {
        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsUserAnAdmin();

        public static bool DenyProcessTermination()
        {
            try
            {
                var hCurrentProcess = Process.GetCurrentProcess().SafeHandle;
                var processSecurity = new ProcessSecurity(hCurrentProcess);
                SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                // Create a rule to deny process termination.
                ProcessAccessRule rule = new ProcessAccessRule(sid, ProcessAccessRights.Terminate, false,
                                                                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
                processSecurity.AddAccessRule(rule);
                processSecurity.SaveChanges(hCurrentProcess);
            }
            catch (Exception eX)
            {
                ReportException(eX);
                return false;
            }
            return true;
        }
        internal static void CopyToClipboard(string s)
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.Text, s);
            Clipboard.SetDataObject(data, true);
        }

        internal static void OpenRegeditTo(string registryKeyPath)
        {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", $"Computer\\{registryKeyPath}");
            try
            {
                Process.Start("regedit.exe", "/m");
            }
            catch
            {
                /* User may abort */
            }
        }

        /// <summary>
        /// Open Windows Explorer with the specified file selected.
        /// </summary>
        /// <param name="manifestFilename"></param>
        internal static void OpenExplorerTo(string manifestFilename)
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{manifestFilename}\"");
            }
            catch
            {
                /* User may abort */
            }
        }

        internal static void ReportException(Exception eX)
        {
            string sTitle = "Sorry, you may have found an error...";
            ReportException(eX, sTitle, null);
        }
        public static void ReportException(Exception eX, string sTitle, string sCallerMessage)
        {
            Trace.WriteLine("******ReportException()******\n" + eX.Message + "\n" + eX.StackTrace + "\n" + eX.InnerException);

            if ((eX is System.Threading.ThreadAbortException))         // TODO: What about ObjectDisposedException?
            {
                return;
            }

            string sMessage;
            if (eX is OutOfMemoryException)
            {
                sTitle = "Insufficient Memory Address Space";
                sMessage = "An out-of-memory exception was encountered.\nGC Total Allocated: " + GC.GetTotalMemory(false).ToString("N0") + " bytes.";
            }
            else
            {
                if (String.IsNullOrEmpty(sCallerMessage))
                {
                    sMessage = "NMF-View encountered an unexpected problem. If you believe this is a bug, please copy this message by hitting CTRL+C, and submit a issue report on GitHub";
                }
                else
                {
                    sMessage = sCallerMessage;
                }
            }

            MessageBox.Show(
                sMessage + "\n\n" +
                eX.Message + "\n\n" +
                "Type: " + eX.GetType().ToString() + "\n" +
                "Source: " + eX.Source + "\n" +
                eX.StackTrace + "\n\n" +
                eX.InnerException + "\n" +
                "NMF-View v" + Application.ProductVersion + ((8 == IntPtr.Size) ? " (x64 " : " (x86 ") + Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") + ") [.NET " + Environment.Version + " on " + Environment.OSVersion.VersionString + "] ",
                sTitle);
        }
    }
    internal class ProcessSecurity : NativeObjectSecurity
    {
        public ProcessSecurity(SafeHandle processHandle)
            : base(false, ResourceType.KernelObject, processHandle, AccessControlSections.Access) { }

        public void AddAccessRule(ProcessAccessRule rule)
        {
            base.AddAccessRule(rule);
        }

        public void SaveChanges(SafeHandle processHandle)
        {
            Persist(processHandle, AccessControlSections.Access);
        }

        public override Type AccessRightType
        {
            get { return typeof(ProcessAccessRights); }
        }

        public override AccessRule AccessRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
        {
            return new ProcessAccessRule(identityReference, (ProcessAccessRights)accessMask, isInherited, inheritanceFlags, propagationFlags, type);
        }

        public override Type AccessRuleType
        {
            get { return typeof(ProcessAccessRule); }
        }

        public override AuditRule AuditRuleFactory(System.Security.Principal.IdentityReference identityReference, int accessMask, bool isInherited, InheritanceFlags inheritanceFlags,
                                                    PropagationFlags propagationFlags, AuditFlags flags)
        {
            throw new NotImplementedException();
        }

        public override Type AuditRuleType
        {
            get { throw new NotImplementedException(); }
        }
    }

    internal class ProcessAccessRule : AccessRule
    {
        public ProcessAccessRule(IdentityReference identityReference, ProcessAccessRights accessMask, bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
            : base(identityReference, (int)accessMask, isInherited, inheritanceFlags, propagationFlags, type) { }

        public ProcessAccessRights ProcessAccessRights { get { return (ProcessAccessRights)AccessMask; } }
    }
    [Flags]
    internal enum ProcessAccessRights
    {
        Terminate = 1
    }
}
