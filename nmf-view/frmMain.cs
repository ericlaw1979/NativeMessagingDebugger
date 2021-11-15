using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace nmf_view
{
    public partial class frmMain : Form
    {
        const string sFiddlerPrefix = "http://127.0.0.1:8888/ExtToNative/";
        struct app_state
        {
            public bool bSendToFiddler;
            public bool bEchoToSource;
            public bool bPropagateClosures;
            public bool bLogMessageBodies;
            public bool bLogToDesktop;
            public string sExtensionID;
            public Stream strmToApp;
            public Stream strmFromExt;
            public Stream strmToExt;
        }

        static app_state oSettings;
        static readonly HttpClient client = new HttpClient();

        private StreamWriter swLogfile = null;

        public frmMain()
        {
            InitializeComponent();
        }

        private void markExtensionDetached()
        {
            pbExt.BackColor = Color.DarkGray;
            toolTip1.SetToolTip(pbExt, $"Was connected to {oSettings.sExtensionID}.\nDisconnected");
            if (oSettings.bPropagateClosures) detachApp();
        }

        private void detachApp()
        {
            log("Detaching downstream pipes.");
            pbApp.BackColor = Color.DarkGray;
            if (null != oSettings.strmToApp) oSettings.strmToApp.Close();
            toolTip1.SetToolTip(pbApp, "Disconnected");
            //if (oSettings.bPropagateClosures && null != oSettings.strmApp) oSettings.strmExt.Close();
        }

        private void detachExtension()
        {
            log("Detaching upstream pipes.");
            markExtensionDetached();
            if (null != oSettings.strmFromExt) oSettings.strmFromExt.Close();
            if (null != oSettings.strmToExt) oSettings.strmToExt.Close();
            //if (oSettings.bPropagateClosures && null != oSettings.strmApp) oSettings.strmExt.Close();
        }

        /*     /*. The maximum size of a single message from the native messaging host is 1 MB, 
                *  The maximum size of the message sent to the native messaging host is 4 GB.
        */

        private async Task MessageShufflerTask()
        {
            byte[] arrLenBytes = new byte[4];

            while (true)
            {
                // log("Waiting for a message header....");
                int iSizeRead = 0;
                while (iSizeRead < 4)
                {
                    int iThisSizeRead = await oSettings.strmFromExt.ReadAsync(arrLenBytes, iSizeRead, arrLenBytes.Length - iSizeRead);
                    if (iThisSizeRead < 1)
                    {
                        log($"Got EOF while trying to read message size from (stdin); got only {iSizeRead} bytes. Disconnecting.");
                        markExtensionDetached();
                        return;
                    }
                    iSizeRead += iThisSizeRead;
                }

                Int32 cBytes = BitConverter.ToInt32(arrLenBytes, 0);
                log($"Promised a message of length: {cBytes}");
                if (cBytes == 0)
                {
                    log("Got an empty (size==0) message. Is that legal?? Disconnecting.");
                    markExtensionDetached();
                    return;
                }

                byte[] buffer = new byte[cBytes];
                int iRead = 0;

                while (iRead < cBytes)
                {
                    int iThisRead = await oSettings.strmFromExt.ReadAsync(buffer, 0, buffer.Length);
                    if (iThisRead < 1)
                    {
                        log($"Got EOF while reading message data from (stdin); got only {iRead} bytes. Disconnecting.");
                        markExtensionDetached();
                    }
                    iRead += iThisRead;
                    string sMessage = Encoding.UTF8.GetString(buffer, 0, iThisRead);
                    log(sMessage, true);

                    if (oSettings.bSendToFiddler)
                    {
                        try
                        {
                            StringContent body = new StringContent(sMessage, Encoding.UTF8, "application/json");

                            HttpResponseMessage response = await client.PostAsync($"{sFiddlerPrefix}{oSettings.sExtensionID ?? "noID"}", body);
                            if (response.IsSuccessStatusCode)
                            {
                                string sNewBody = await response.Content.ReadAsStringAsync();
                                if (!sNewBody.Contains("Fiddler Echo"))
                                {
                                    sMessage = sNewBody;
                                }
                            }
                        }
                        catch (HttpRequestException e)
                        {
                            log($"Call to Fiddler failed: {e.Message}");
                        }
                    }

                    if (oSettings.bEchoToSource)
                    {
                        byte[] arrPayload = Encoding.UTF8.GetBytes(sMessage);
                        byte[] arrSize = BitConverter.GetBytes(arrPayload.Length);
                        await oSettings.strmToExt.WriteAsync(arrSize, 0, 4);
                        await oSettings.strmToExt.WriteAsync(arrPayload, 0, arrPayload.Length);
                    }

                    if (null != oSettings.strmToApp)
                    {
                        byte[] arrPayload = Encoding.UTF8.GetBytes(sMessage);
                        byte[] arrSize = BitConverter.GetBytes(arrPayload.Length);
                        await oSettings.strmToApp.WriteAsync(arrSize, 0, 4);
                        await oSettings.strmToApp.WriteAsync(arrPayload, 0, arrPayload.Length);
                        await oSettings.strmToApp.FlushAsync();
                    }
                }
            }
        }

        private void log(string sMsg, bool bIsBody = false)
        {
            sMsg = $"{DateTime.Now:HH:mm:ss:ffff} - {sMsg}";
            if (!bIsBody || oSettings.bLogMessageBodies)
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    txtLog.AppendText(sMsg + "\r\n");
                });
            }
            if (null != swLogfile)
            {
                try
                {
                    swLogfile.WriteLine(sMsg);
                }
                catch { }
            }
        }

        private void CreateLogfile()
        {
            CloseLogfile();

            string sLeafFilename = $"NativeMessages-{DateTime.Now.ToString("MMdd_HH-mm-ss")}.txt";
            try
            {
                swLogfile = File.CreateText(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + Path.DirectorySeparatorChar + sLeafFilename);
                swLogfile.Write(this.txtLog.Text);
            }
            catch (Exception eX)
            {
                log($"Creating log file [{sLeafFilename}] failed; {eX.Message}");
            }
        }

        private void CloseLogfile()
        {
            if (null != swLogfile)
            {
                swLogfile.Close();
            }
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            lblVersion.Text = $"v{Application.ProductVersion} [{((8 == IntPtr.Size) ? "64" : "32")}-bit]";
            this.Text += $" [pid:{Process.GetCurrentProcess().Id}{(Utilities.IsUserAnAdmin()?" Elevated":String.Empty)}]";
            //https://source.chromium.org/chromium/chromium/src/+/main:chrome/test/data/native_messaging/native_hosts/echo.py;l=30?q=parent-window&sq=&ss=chromium
            // string sArgs = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).ToArray());

            if (Environment.GetCommandLineArgs().Length > 2) oSettings.sExtensionID = Environment.GetCommandLineArgs()[1];
            if (String.IsNullOrEmpty(oSettings.sExtensionID))
            {
                oSettings.sExtensionID = "unknown";
                log("Started without an extension ID.\r\n\r\n"
                  + "Note: This application does not seem to have been started by a Chromium-based browser\r\n"
                  + "to respond to NativeMessaging requests. Use the CONFIG tab below to reconfigure a\r\n"
                  + "registered NativeMessaging Host to proxy its traffic through an instance of this app.\r\n"
                  + "\r\n---------------------------------\r\n"
                  );
                tcApp.SelectedTab = pageAbout;
            }

            oSettings.sExtensionID = oSettings.sExtensionID.Replace("chrome-extension://", String.Empty);
            toolTip1.SetToolTip(pbExt, $"Connected to {oSettings.sExtensionID}.\nDouble-click to disconnect.");
            toolTip1.SetToolTip(pbApp, $"Click to set the ClientHandler to another instance of this app.");
            log("Listening for messages...");

            clbOptions.SetItemChecked(2, true);
            clbOptions.SetItemChecked(3, true);

            WaitForMessages();
        }

        private void WaitForMessages()
        {
            try
            {
                oSettings.strmFromExt = Console.OpenStandardInput();
                oSettings.strmToExt = Console.OpenStandardOutput();
                pbExt.BackColor = Color.FromArgb(159, 255, 159);
                Task.Run(async () => await MessageShufflerTask());
            }
            catch (Exception eX)
            {
                log(eX.Message);
            }
        }

        private void clbOptions_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.Index == 0) { oSettings.bEchoToSource = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 1) { oSettings.bSendToFiddler = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 2) { oSettings.bPropagateClosures = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 3) { oSettings.bLogMessageBodies = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 4) {
                oSettings.bLogToDesktop = (e.NewValue == CheckState.Checked);
                if (oSettings.bLogToDesktop)
                {
                    CreateLogfile();
                }
                else
                {
                    CloseLogfile();
                }
                return; 
            }
        }

        private void pbApp_Click(object sender, EventArgs e)
        {
            ConnectApp();
        }

        private void ConnectApp()
        {
            using (Process myProcess = new Process())
            {
                myProcess.StartInfo.FileName = Application.ExecutablePath;
                myProcess.StartInfo.Arguments = "chrome-extension://" + oSettings.sExtensionID;
                myProcess.StartInfo.UseShellExecute = false;
                myProcess.StartInfo.RedirectStandardInput = true;

                myProcess.Start();

                oSettings.strmToApp = myProcess.StandardInput.BaseStream;
                pbApp.BackColor = Color.FromArgb(159, 255, 159);
                toolTip1.SetToolTip(pbApp, $"#{myProcess.Id} - {myProcess.StartInfo.FileName}\nDouble-click to disconnect.");
            }
        }

        private void pbApp_DoubleClick(object sender, EventArgs e)
        {
            detachApp();
        }

        private void pbExt_DoubleClick(object sender, EventArgs e)
        {
            detachExtension();
        }

        private void lnkGithub_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/ericlaw1979/NativeMessagingDebugger");
        }

        private void lnkDocs_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/ericlaw1979/NativeMessagingDebugger/blob/main/DOCS.md");
        }

        private void lnkEric_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://twitter.com/ericlaw");
        }

        private void tcApp_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tcApp.SelectedTab == pageRegisteredHosts)
            {
                lvHosts.Items.Clear();
                List<RegisteredHosts.HostEntry> listHosts = RegisteredHosts.GetAllHosts();
                if (!Utilities.IsUserAnAdmin()) lvHosts.Groups[1].Header = "System-Registered (HKLM); you must run this program as Admin to edit these.";
                foreach (RegisteredHosts.HostEntry oHE in listHosts)
                {
                    var item = lvHosts.Items.Add(oHE.Name);
                    item.Tag = oHE;
                    item.ToolTipText = oHE.RegistryKeyPath;
                    item.SubItems.Add(oHE.iPriority.ToString());
                    item.SubItems.Add(oHE.ManifestFilename);
                    item.SubItems.Add(oHE.Command);
                    item.SubItems.Add(oHE.Description);
                    item.SubItems.Add(oHE.SupportedBrowsers);
                    item.SubItems.Add(oHE.AllowedExtensions);

                    // If the Host is registered system-wide, it's only editable if this app is run at Admin.
                    bool bSystemRegistration = (oHE.iPriority > 6);
                    item.Group = lvHosts.Groups[bSystemRegistration ? 1 : 0];
                    if (bSystemRegistration && !Utilities.IsUserAnAdmin()) item.BackColor = Color.FromArgb(0xE0, 0xE0, 0xE0);
                }
            }
        }

        private void lvHosts_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // If we're not elevated, we cannot readily change anything in the SystemRegistered group.
            // TODO:Note that that's sorta untrue; Chromium prioritizes HKCU over HKLM by default unless
            // you've set a policy to ignore HKCU. So we could conceivably just clone a HKLM registration to HKCU...
            if (!Utilities.IsUserAnAdmin() && lvHosts.Items[e.Index].Group == lvHosts.Groups[1])
            {
                e.NewValue = e.CurrentValue;
            }
        }

        private void CopySelectedInfo(ListView lv)
        {
            if (lv.SelectedItems.Count < 1) return;

            StringBuilder sbInfo = new StringBuilder();
            foreach (ListViewItem lvi in lv.SelectedItems)
            {
                sbInfo.AppendLine(((RegisteredHosts.HostEntry)lvi.Tag).ToString());
            }

            Utilities.CopyToClipboard(sbInfo.ToString());
        }

        private void lvHosts_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Modifiers == Keys.Control) && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                CopySelectedInfo(sender as ListView);
                return;
            }
            if ((e.Modifiers == Keys.Control) && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                (sender as HostListView).SelectAll();
                return;
            }
        }

        private void lvHosts_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if ((Control.ModifierKeys == Keys.Alt) && (lvHosts.SelectedItems.Count==1))
            {
                ListViewItem oLVI = lvHosts.SelectedItems[0];
                RegisteredHosts.HostEntry oHE = (RegisteredHosts.HostEntry)oLVI.Tag;
                Utilities.OpenRegeditTo(oHE.RegistryKeyPath);
            }
        }

        private void frmMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            detachApp();
            detachExtension();
            CloseLogfile();
        }
    }
}
