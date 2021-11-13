using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace nmf_view
{
    public class HostListView : ListView
    {
        string EmptyText { get; set; } = "No NativeMessaging Hosts were found in the registry.";

        internal static class LVNative
        {
            internal const Int32 LVN_FIRST = -100;
            internal const Int32 LVN_GETEMPTYMARKUP = LVN_FIRST - 87;

            [StructLayout(LayoutKind.Sequential)]
            public struct NMHDR
            {
                public IntPtr hwndFrom;
                public IntPtr idFrom;
                public Int32 code;
            }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct NMLVEMPTYMARKUP
            {
                public NMHDR hdr;
                public UInt32 dwFlags;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2084)]
                public String szMarkup;
            }
        }

        public HostListView()
        {
            this.DoubleBuffered = true;
        }

        public void SelectAll()
        {
            BeginUpdate();
            foreach (ListViewItem lvi in Items)
            {
                lvi.Selected = true;
            }
            EndUpdate();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x204E:  // WM_NOTIFY | WM_REFLECT
                    LVNative.NMHDR nmhdr = (LVNative.NMHDR)m.GetLParam(typeof(LVNative.NMHDR));

                    if (LVNative.LVN_GETEMPTYMARKUP == nmhdr.code)
                    {
                        var markup = (LVNative.NMLVEMPTYMARKUP)m.GetLParam(typeof(LVNative.NMLVEMPTYMARKUP));
                        markup.szMarkup = EmptyText;
                        markup.dwFlags = 1;     // EMF_CENTERED;
                        Marshal.StructureToPtr(markup, m.LParam, true);
                        m.Result = (IntPtr)1;
                        return;
                    }
                    break;
            }
            base.WndProc(ref m);
        }
    }
}
