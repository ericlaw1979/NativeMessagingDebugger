using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace nmf_view
{
    static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDPIAware();
        private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs ue)
        {
            Exception unhandledException = (Exception)ue.ExceptionObject;
            Utilities.ReportException(unhandledException);
        }

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
