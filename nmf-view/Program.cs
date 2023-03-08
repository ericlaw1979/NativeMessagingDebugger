using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace nmf_view
{
    static class Program
    {


        private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs ue)
        {
            Exception unhandledException = (Exception)ue.ExceptionObject;
            ReportException(unhandledException);
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

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(UnhandledExceptionHandler);
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new frmMain());
        }
    }
}
