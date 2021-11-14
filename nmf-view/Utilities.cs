using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace nmf_view
{
    class Utilities
    {
        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsUserAnAdmin();

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
    }
}
