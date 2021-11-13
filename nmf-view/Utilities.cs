using System;
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
    }
}
