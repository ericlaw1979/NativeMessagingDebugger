using Microsoft.Win32;
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
        #region Boilerplate
        public frmMain()
        {
            InitializeComponent();
        }
        #endregion Boilerplate

        const string sFiddlerPrefix = "http://127.0.0.1:8888";

        struct app_state
        {
            public bool bSendToFiddler;
            public bool bReflectToExtension;
            public bool bPropagateClosures;
            public bool bLogMessageBodies;
            public bool bLogToDesktop;
            public ulong iParentWindow;
            public string sExtensionID;
            public string sExeName;
            public Stream strmFromApp;
            public Stream strmToApp;
            public Stream strmFromExt;
            public Stream strmToExt;
        }

        static app_state oSettings;
        static readonly HttpClient client = new HttpClient();

        private StreamWriter swLogfile = null;

        private static async Task WriteToApp(string sMessage)
        {
            byte[] arrPayload = Encoding.UTF8.GetBytes(sMessage);
            byte[] arrSize = BitConverter.GetBytes(arrPayload.Length);
            await oSettings.strmToApp.WriteAsync(arrSize, 0, 4);
            await oSettings.strmToApp.WriteAsync(arrPayload, 0, arrPayload.Length);
            await oSettings.strmToApp.FlushAsync();
        }

        private static async Task WriteToExtension(string sMessage)
        {
            byte[] arrPayload = Encoding.UTF8.GetBytes(sMessage);
            byte[] arrSize = BitConverter.GetBytes(arrPayload.Length);
            await oSettings.strmToExt.WriteAsync(arrSize, 0, 4);
            await oSettings.strmToExt.WriteAsync(arrPayload, 0, arrPayload.Length);
            await oSettings.strmToExt.FlushAsync();
        }

        private void MaybeWriteToLogfile(string sMsg)
        {
            if (!oSettings.bLogToDesktop || (null == swLogfile)) return;

            try
            {
                swLogfile.WriteLine(sMsg);
            }
            catch { }
        }
        private void MaybeWriteBytesToLogfile(string sPrefix, byte[] arrMsg, int iStart, int iLength)
        {
            if (!oSettings.bLogToDesktop || (null == swLogfile)) return;

            try
            {
                swLogfile.WriteLine($"{DateTime.Now:HH:mm:ss:ffff} - {sPrefix} {Convert.ToBase64String(arrMsg, iStart, iLength)}");
            }
            catch { }
        }

        public bool IsAppAttached() {
            return (oSettings.strmToApp != null);
        }

        public bool IsExtensionAttached()
        {
            return (oSettings.strmToExt != null);
        }

        private void markExtensionDetached()
        {
            pbExt.BackColor = Color.DarkGray;
            toolTip1.SetToolTip(pbExt, $"Was connected to {oSettings.sExtensionID}.\nDisconnected");
            if (oSettings.bPropagateClosures) detachApp();
        }
        private void markAppDetached()
        {
            pbApp.BackColor = Color.DarkGray;
            toolTip1.SetToolTip(pbApp, $"Was connected to {oSettings.sExeName}.\nDisconnected");
            if (oSettings.bPropagateClosures) detachExtension();
        }
        private void detachApp()
        {
            if (IsAppAttached()) log("Detaching App pipes.");
            pbApp.BackColor = Color.DarkGray;
            if (null != oSettings.strmToApp) oSettings.strmToApp.Close();
            if (null != oSettings.strmFromApp) oSettings.strmFromApp.Close();
            toolTip1.SetToolTip(pbApp, "Disconnected");
            //if (oSettings.bPropagateClosures && null != oSettings.strmApp) oSettings.strmExt.Close();
        }

        private void detachExtension()
        {
            if (IsExtensionAttached()) log("Detaching Extension pipes.");
            markExtensionDetached();
            if (null != oSettings.strmToExt) oSettings.strmToExt.Close();
            if (null != oSettings.strmFromExt) oSettings.strmFromExt.Close();
            //if (oSettings.bPropagateClosures && null != oSettings.strmApp) oSettings.strmExt.Close();
        }
        private async Task MessageShufflerForExtension()
        {
            byte[] arrLenBytes = new byte[4];

            while (true)
            {
                int cbSizeRead = 0;
                while (cbSizeRead < 4)
                {
                    int cbThisRead = await oSettings.strmFromExt.ReadAsync(arrLenBytes, cbSizeRead, arrLenBytes.Length - cbSizeRead);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while trying to read message size from (Extension); got only {cbSizeRead} bytes. Disconnecting.");
                        markExtensionDetached();
                        return;
                    }
                    MaybeWriteBytesToLogfile("ReadSizeFromExt-RawRead: ", arrLenBytes, cbSizeRead, cbThisRead);
                    cbSizeRead += cbThisRead;
                }

                Int32 cbBodyPromised = BitConverter.ToInt32(arrLenBytes, 0);
                log($"Extension promised a message of length: {cbBodyPromised:N0}");
                if (cbBodyPromised == 0)
                {
                    log("Got an empty (size==0) message. Is that legal?? Disconnecting.");
                    markExtensionDetached();
                    return;
                }

                byte[] buffer = new byte[cbBodyPromised];
                int cbBodyRead = 0;

                while (cbBodyRead < cbBodyPromised)
                {
                    int cbThisRead = await oSettings.strmFromExt.ReadAsync(buffer, cbBodyRead, cbBodyPromised - cbBodyRead);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while reading message data from (Extension); got only {cbBodyRead} bytes. Disconnecting.");
                        markExtensionDetached();
                    }
                    MaybeWriteBytesToLogfile("ReadBodyFromExt-RawRead: ", buffer, cbBodyRead, cbThisRead);
                    cbBodyRead += cbThisRead;
                }
                string sMessage = Encoding.UTF8.GetString(buffer, 0, cbBodyRead);
                log(sMessage, true);

                if (oSettings.bSendToFiddler)
                {
                    try
                    {
                        // TODO: Should we POST the byte array instead?
                        StringContent body = new StringContent(sMessage, Encoding.UTF8, "application/json");

                        HttpResponseMessage response = await client.PostAsync($"{sFiddlerPrefix}/ExtToApp/{oSettings.sExtensionID ?? "noID"}/{oSettings.sExeName}", body);
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
                if (oSettings.bReflectToExtension &&
                    (sMessage.Length < (1024*1024)))    // Don't reflect messages over 1mb. They're illegal!
                {
                    await WriteToExtension(sMessage);
                }
                if (null != oSettings.strmToApp)
                {
                    await WriteToApp(sMessage);
                }
            }
        }

        private async Task MessageShufflerForApp()
        {
            byte[] arrLenBytes = new byte[4];

            while (true)
            {
                int cbSizeRead = 0;
                while (cbSizeRead < 4)
                {
                    int cbThisRead = await oSettings.strmFromApp.ReadAsync(arrLenBytes, cbSizeRead, arrLenBytes.Length - cbSizeRead);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while trying to read message size from (App); got only {cbSizeRead} bytes. Disconnecting.");
                        markAppDetached();
                        return;
                    }
                    MaybeWriteBytesToLogfile("ReadSizeFromApp-RawRead: ", arrLenBytes, cbSizeRead, cbThisRead);
                    cbSizeRead += cbThisRead;
                }

                Int32 cbBodyPromised = BitConverter.ToInt32(arrLenBytes, 0);
                log($"App promised a message of length: {cbBodyPromised:N0}");
                if (cbBodyPromised == 0)
                {
                    log("Got an empty (size==0) message. Is that legal?? Disconnecting.");
                    markExtensionDetached();
                    return;
                }

                if (cbBodyPromised > 1024*1024)
                {
                    log($"Illegal message size from the NativeMessaging App. Messages are limited to 1mb but this app wants to send {cbBodyPromised:N0} bytes.");
                    // TODO: Skip it? Detach?
                }

                byte[] buffer = new byte[cbBodyPromised];
                int cbBodyRead = 0;

                while (cbBodyRead < cbBodyPromised)
                {
                    int cbThisRead = await oSettings.strmFromApp.ReadAsync(buffer, cbBodyRead, cbBodyPromised - cbBodyRead);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while reading message data from (App); got only {cbBodyRead} bytes. Disconnecting.");
                        markAppDetached();
                    }
                    MaybeWriteBytesToLogfile("ReadBodyFromApp-RawRead: ", buffer, cbBodyRead, cbThisRead);
                    cbBodyRead += cbThisRead;
                }

                string sMessage = Encoding.UTF8.GetString(buffer, 0, cbBodyPromised);
                log(sMessage, true);

                if (oSettings.bSendToFiddler)
                {
                    try
                    {
                        StringContent body = new StringContent(sMessage, Encoding.UTF8, "application/json");

                        HttpResponseMessage response = await client.PostAsync($"{sFiddlerPrefix}/AppToExt/{oSettings.sExeName}/{oSettings.sExtensionID ?? "noID"}", body);
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
                if (null != oSettings.strmToExt)
                {
                    await WriteToExtension(sMessage);
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
            MaybeWriteToLogfile(sMsg);
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

            var arrArgs = Environment.GetCommandLineArgs();
            if (arrArgs.Length > 1) oSettings.sExtensionID = arrArgs[1];
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
            if (arrArgs.Length > 2)
            {
                // The parent-window value is only non-zero when the calling context is not a background script.
                if (arrArgs[2].StartsWith("--parent-window="))
                {
                    if (!ulong.TryParse(arrArgs[2].Substring(16), out oSettings.iParentWindow))
                        oSettings.iParentWindow = 0;
                    log($"parent-window: {oSettings.iParentWindow:x8}");
                }
            }

            oSettings.sExtensionID = oSettings.sExtensionID.Replace("chrome-extension://", String.Empty).TrimEnd('/');
            toolTip1.SetToolTip(pbExt, $"Connected to {oSettings.sExtensionID}.\nDouble-click to disconnect.");
            toolTip1.SetToolTip(pbApp, $"Click to set the ClientHandler to another instance of this app.");
            log("Listening for messages...");

            // clbOptions.SetItemChecked(1, true); Fiddler
            clbOptions.SetItemChecked(2, true);
            clbOptions.SetItemChecked(3, true);

            //if (oSettings.sExtensionID != "unknown") ConnectApp();
            WaitForMessages();
        }

        private void WaitForMessages()
        {
            try
            {
                oSettings.strmFromExt = Console.OpenStandardInput();
                oSettings.strmToExt = Console.OpenStandardOutput();
                pbExt.BackColor = Color.FromArgb(159, 255, 159);
                Task.Run(async () => await MessageShufflerForExtension());
            }
            catch (Exception eX)
            {
                log(eX.Message);
            }
        }

        private void clbOptions_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.Index == 0) { oSettings.bReflectToExtension = (e.NewValue == CheckState.Checked); return; }
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
            if (!IsAppAttached()) ConnectApp();
        }

        private void ConnectApp()
        {
            // https://source.chromium.org/chromium/chromium/src/+/main:chrome/browser/extensions/api/messaging/native_process_launcher_win.cc;bpv=1;bpt=1

            using (Process myProcess = new Process())
            {
                myProcess.StartInfo.FileName = /*Application.ExecutablePath;//  */ @"C:\program files\windows security\browserCore\browserCore.exe";
                myProcess.StartInfo.Arguments = $"chrome-extension://{oSettings.sExtensionID} --parent-window={oSettings.iParentWindow}"; // TODO: Parent window
                myProcess.StartInfo.UseShellExecute = false;
                myProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(myProcess.StartInfo.FileName);

                // Hide by default; TODO: allow showing https://source.chromium.org/chromium/chromium/src/+/main:base/process/launch_win.cc;l=298;drc=1ad438dde6b39e1c0d04b8f8cb27c1a14ba6f90e
                myProcess.StartInfo.CreateNoWindow = true;
                myProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;  // Does this do anything?

                myProcess.StartInfo.RedirectStandardInput = true;
                myProcess.StartInfo.RedirectStandardOutput = true;
                // TODO: STDERR

                //todo: catch errors.
                myProcess.Start();

                pbApp.BackColor = Color.FromArgb(159, 255, 159);
                toolTip1.SetToolTip(pbApp, $"#{myProcess.Id} - {myProcess.StartInfo.FileName}\nDouble-click to disconnect.");

                oSettings.sExeName = Path.GetFileName(myProcess.StartInfo.FileName);

                // https://docs.microsoft.com/en-us/dotnet/api/system.console?view=net-5.0#Streams
                oSettings.strmToApp = myProcess.StandardInput.BaseStream;
                oSettings.strmFromApp = myProcess.StandardOutput.BaseStream;
                Task.Run(async () => await MessageShufflerForApp());
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
                PopulateHosts();
            }else
                if (tcApp.SelectedTab == pageTroubleshooter)
            {
                PopulateTroubleshooter();
            }
        }

        static IEnumerable<string> EnumPipes(string sFilter)
        {
            bool MoveNextSafe(IEnumerator<string> enumerator)
            {
                const int Retries = 100;
                for (int i = 0; i < Retries; i++)
                {
                    try
                    {
                        return enumerator.MoveNext();
                    }
                    catch (ArgumentException) { }
                }
                return false;
            }

            using (var enumerator = Directory.EnumerateFiles(@"\\.\pipe\").GetEnumerator())
            {
                while (MoveNextSafe(enumerator))
                {
                    if (!enumerator.Current.Contains(sFilter)) continue;
                    yield return enumerator.Current;
                }
            }
        }

        private void PopulateTroubleshooter()
        {
            try
            {
                rtbTroubleshoot.AppendText("Chromium uses named pipes to write data into stdio for the NativeMessaging host application.\r\n\r\n" 
                                         + "Currently active NativeMessaging named pipes:\r\n\r\n");
                foreach (var sPipe in EnumPipes(".nativeMessaging."))
                {
                    // https://source.chromium.org/chromium/chromium/src/+/main:chrome/browser/extensions/api/messaging/native_process_launcher_win.cc;l=134;drc=09a4396a448775456084fe36bb84662f5757d988
                    rtbTroubleshoot.AppendText($"{sPipe}\r\n");
                    // It would be nice to show the owner here, but we can't because we cannot get the handle to the pipe
                    // which only allows one connection.
                    // https://docs.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid
                }
            }
            catch (Exception eX)
            {
                rtbTroubleshoot.AppendText($"Enumeration of named pipes failed: {eX.Message}");
            }

            rtbTroubleshoot.AppendText("=======================\r\n");

            string sComSpec = Environment.GetEnvironmentVariable("COMSPEC");
            if (sComSpec.IndexOf("cmd.exe", StringComparison.OrdinalIgnoreCase)<1)
            {
                rtbTroubleshoot.AppendText($"FATAL: Non-cmd.exe COMSPEC value will not work; {sComSpec}");
            }

            UInt64 ulPolicy = Convert.ToUInt64(Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\System", "DisableCMD", 0));
            if (ulPolicy != 0)
            {
                rtbTroubleshoot.AppendText($"FATAL: cmd.exe is disabled by DisableCMD policy. NativeMessaging will not work.\r\n");
            }
        // todo: more troubleshooters
        //
        // https://bugs.chromium.org/p/chromium/issues/detail?id=416474&q=%22parent-window%22%20%22Native%20Messaging%20Host%22&can=1
        //   ^ or & in path
        //    https://bugs.chromium.org/p/chromium/issues/detail?id=335558&q=%22parent-window%22%20%22Native%20Messaging%20Host%22&can=1
        }

        private void PopulateHosts()
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

        private void lvHosts_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // If we're not elevated, we cannot readily change anything in the SystemRegistered group.
            // TODO: Note that that's sorta untrue; Chromium prioritizes HKCU over HKLM by default unless
            // you've set a policy to ignore HKCU. So we could conceivably just clone a HKLM registration to 32-bit HKCU...
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

        private void txtSendToApp_TextChanged(object sender, EventArgs e)
        {
            btnSendToApp.Enabled = ((txtSendToApp.TextLength > 0) && IsAppAttached());
        }

        private void txtSendToExtension_TextChanged(object sender, EventArgs e)
        {
            btnSendToExtension.Enabled = ((txtSendToExtension.TextLength > 0) && IsExtensionAttached());
        }

        private void btnSendToApp_Click(object sender, EventArgs e)
        {
            txtSendToApp.Text = txtSendToApp.Text.Trim();
            WriteToApp(txtSendToApp.Text);
        }

        private void btnSendToExtension_Click(object sender, EventArgs e)
        {
            txtSendToApp.Text = txtSendToApp.Text.Trim();
            WriteToExtension(txtSendToExtension.Text);
        }
    }
}
