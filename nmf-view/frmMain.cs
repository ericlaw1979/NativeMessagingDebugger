﻿using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace nmf_view
{
    public partial class frmMain : Form
    {
        // TODO: This doesn't actually seem to help. Canceling the token does not appear to
        // cause the ReadAsync calls to exit. Hrmph.
        readonly CancellationTokenSource ctsApp = new CancellationTokenSource();
        readonly CancellationTokenSource ctsExt = new CancellationTokenSource();
        bool bAppIsShuttingdown = false;

        enum FileType : uint
        {
            FileTypeChar = 0x0002,
            FileTypeDisk = 0x0001,
            FileTypePipe = 0x0003,
            FileTypeRemote = 0x8000,
            FileTypeUnknown = 0x0000,
            FileTypeUnknownError = 0xFFFE,
            FileTypeUnknownHandleInvalid = 0xFFFF,
        }
        const int STD_INPUT_HANDLE = -10;
        const int STD_OUTPUT_HANDLE = -11;
        const int STD_ERROR_HANDLE = -12;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern FileType GetFileType(IntPtr hFile);
        FileType GetFileType2(IntPtr hFile)
        {
            FileType ftResult = GetFileType(hFile);
            
            if (ftResult != FileType.FileTypeUnknown) return ftResult;
            int iError = Marshal.GetLastWin32Error();
            if (0 == iError /* S_OK */) return FileType.FileTypeUnknown;
            if (6 == iError  /* ERROR_INVALID_HANDLE */)
            {
                return FileType.FileTypeUnknownHandleInvalid;
            }
            return FileType.FileTypeUnknownError;
        }

        // [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        // static extern bool FreeConsole();

        private const int SW_SHOW = 5;
        [DllImport("User32")]
        private static extern int ShowWindow(int hwnd, int nCmdShow);

        [DllImport("Kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CancelIoEx(IntPtr hFile, IntPtr pNull);

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
            public bool bLogToStdErr;
            public ulong iParentWindow;
            public string sExtensionID;
            public string sExeName;
            public Stream strmFromApp;
            public Stream strmToApp;
            public Stream strmFromExt;
            public Stream strmToExt;
            //public Stream strmErrToExt;
            public Stream strmErrFromApp;
            public Process procParent;
        }

        static app_state oSettings;
        static List<RegisteredHosts.HostEntry> listHosts;
        static readonly HttpClient client = new HttpClient();
        private StreamWriter swLogfile = null;

        private static async Task WriteToApp(string sMessage)
        {
            try
            {
                if (null == oSettings.strmToApp) return;
                byte[] arrPayload = Encoding.UTF8.GetBytes(sMessage);
                byte[] arrSize = BitConverter.GetBytes((UInt32)arrPayload.Length);
                await oSettings.strmToApp.WriteAsync(arrSize, 0, 4);
                await oSettings.strmToApp.WriteAsync(arrPayload, 0, arrPayload.Length);
                await oSettings.strmToApp.FlushAsync();
            }
            catch (Exception eX)
            {
                MessageBox.Show("Failed to send message\n" + eX.Message, "WriteToApp Failed");
            }
}

        private static async Task WriteToExtension(string sMessage)
        {
            try
            {
                if (null == oSettings.strmToExt) return;
                byte[] arrPayload = Encoding.UTF8.GetBytes(sMessage);
                byte[] arrSize = BitConverter.GetBytes((UInt32)arrPayload.Length);
                await oSettings.strmToExt.WriteAsync(arrSize, 0, 4);
                if (null == oSettings.strmToExt) return;
                await oSettings.strmToExt.WriteAsync(arrPayload, 0, arrPayload.Length);
                if (null == oSettings.strmToExt) return;
                await oSettings.strmToExt.FlushAsync();
                if (null == oSettings.strmToExt) return;
            }
            catch (Exception eX)
            {
                MessageBox.Show("Failed to send message\n" + eX.Message, "WriteToExtension Failed");
            }
        }

        private void MaybeWriteToStdErr(string sMsg)
        {
            try
            {
                if (!oSettings.bLogToStdErr) return;
                Console.Error.WriteLine(sMsg);
            }
            catch (Exception eX)
            {
                Utilities.ReportException(eX);
            }
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

        public bool IsAppAttached()
        {
            return (oSettings.strmToApp != null);
        }

        public bool IsExtensionAttached()
        {
            return (oSettings.strmToExt != null);
        }

        private void markExtensionDetached()
        {
            if (bAppIsShuttingdown) return;
            Trace.WriteLine("markExtensionDetached()");
            this.BeginInvoke((MethodInvoker)delegate
            {
                pbExt.BackColor = Color.DarkGray;
                toolTip1.SetToolTip(pbExt, $"Was connected to {oSettings.sExtensionID ?? "unknown"}.\nDisconnected");
                btnSendToExtension.Enabled = false;
            });
        }

        private void markAppDetached()
        {
            if (bAppIsShuttingdown) return;
            this.BeginInvoke((MethodInvoker)delegate
            {
                pbApp.BackColor = Color.DarkGray;
                toolTip1.SetToolTip(pbApp, $"Was connected to {oSettings.sExeName ?? "unknown"}.\nDisconnected");
                btnSendToApp.Enabled = false;
            });
        }
        private void detachExtension()
        {
            try
            {
                if (!IsExtensionAttached()) return; // TODO: We should have asserts that verify that the UX reflects the current state properly.
                log("Canceling reads...");
                ctsExt.Cancel();  // Doesn't seem to help.
                log("Detaching Extension pipes...");
                // Unfortunately, none of this seems to work to let the other side know we're going away.
                if (null != oSettings.strmToExt)
                {
                    FieldInfo fiHandle = oSettings.strmToExt.GetType().GetField("_handle", BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (null != fiHandle)
                    {
                        SafeFileHandle sh = fiHandle.GetValue(oSettings.strmToExt) as SafeFileHandle;

                        if (null != sh)
                        {
                            log($"stdout handle was 0x{sh.DangerousGetHandle().ToInt64():x}");
                            // typeof(SafeFileHandle).InvokeMember("ReleaseHandle", BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Instance, null, sh, new object[] { });
                        }
                    }

                    oSettings.strmToExt.Close();
                    oSettings.strmToExt = null;
                    CancelIoEx(GetStdHandle(STD_OUTPUT_HANDLE), IntPtr.Zero);
                    CloseHandle(GetStdHandle(STD_OUTPUT_HANDLE));
                    log("stdout closed.");
                }
                if (null != oSettings.strmFromExt)
                {
                    FieldInfo fiHandle = oSettings.strmFromExt.GetType().GetField("_handle", BindingFlags.GetField | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (null != fiHandle)
                    {
                        SafeFileHandle sh = fiHandle.GetValue(oSettings.strmFromExt) as SafeFileHandle;
                        if (null != sh)
                        {
                            log($"stdin handle was 0x{sh.DangerousGetHandle().ToInt64():x}");
                            //typeof(SafeFileHandle).InvokeMember("ReleaseHandle", BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Instance, null, sh, new object[] { });
                        }
                    }
                    log("CancelIoEx(StdInput,0)...");
                    CancelIoEx(GetStdHandle(STD_INPUT_HANDLE), IntPtr.Zero);
                    oSettings.strmFromExt.Close();
                    oSettings.strmFromExt = null;

                    log("CloseHandle(StdInput)...");
                    Application.DoEvents();
                    CloseHandle(GetStdHandle(STD_INPUT_HANDLE));
                    log("stdin closed.");
                }
                log("Extension pipes detached.");
            }
            catch (Exception eX)
            {
                MessageBox.Show(eX.Message, "Error detaching from Extension");
            }
            markExtensionDetached();
            if (oSettings.bPropagateClosures) detachApp();
        }
        private void detachApp()
        {
            Trace.WriteLine("detachApp()");
            if (!IsAppAttached()) return;
            log("Canceling reads...");
            ctsApp.Cancel();
            log("Detaching NativeHost App pipes.");
            if (null != oSettings.strmToApp) { oSettings.strmToApp.Close(); oSettings.strmToApp = null; }
            if (null != oSettings.strmFromApp) { oSettings.strmFromApp.Close(); oSettings.strmFromApp = null; }
            if (null != oSettings.strmErrFromApp) { oSettings.strmErrFromApp.Close(); oSettings.strmErrFromApp = null; }
            log("NativeHost App pipes detached.");
            markAppDetached();
            if (oSettings.bPropagateClosures) detachExtension();
        }

        /// <summary>
        /// This function sits around waiting for messages from the browser extension.
        /// </summary>
        private async Task MessageShufflerForExtension()
        {
            byte[] arrLenBytes = new byte[4];

            while (true)
            {
                int cbSizeRead = 0;
                while (cbSizeRead < 4)
                {
                    int cbThisRead = await oSettings.strmFromExt.ReadAsync(arrLenBytes, cbSizeRead, arrLenBytes.Length - cbSizeRead, ctsExt.Token);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while trying to read message size from (Extension); got only {cbSizeRead} bytes. Disconnecting.");
                        detachExtension();
                        return;
                    }
                    MaybeWriteBytesToLogfile("ReadSizeFromExt-RawRead: ", arrLenBytes, cbSizeRead, cbThisRead);
                    cbSizeRead += cbThisRead;
                }

                UInt32 cbBodyPromised = BitConverter.ToUInt32(arrLenBytes, 0);
                // log($"Extension promised a message of length: {cbBodyPromised:N0}");
                if (cbBodyPromised == 0)
                {
                    log("Got an empty (size==0) message. Is that legal?? Disconnecting.");
                    detachExtension();
                    return;
                }

                if (cbBodyPromised >= Int32.MaxValue)
                {
                    log("Was promised a message >=2gb. Technically this is legal, but this debugger only allows 2GB due to .NET Framework size limits.");
                    detachExtension();
                    return;
                }

                byte[] buffer = new byte[cbBodyPromised];
                UInt32 cbBodyRead = 0;

                while (cbBodyRead < cbBodyPromised)
                {
                    int cbThisRead = await oSettings.strmFromExt.ReadAsync(buffer, (int)cbBodyRead, (int)(cbBodyPromised - cbBodyRead), ctsExt.Token);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while reading message data from (Extension); got only {cbBodyRead} of {cbBodyPromised} bytes. Disconnecting.");
                        detachExtension();
                        return;
                    }
                    MaybeWriteBytesToLogfile("ReadBodyFromExt-RawRead: ", buffer, (int)cbBodyRead, cbThisRead);
                    cbBodyRead += (uint)cbThisRead;
                }
                string sMessage = Encoding.UTF8.GetString(buffer, 0, (int)cbBodyRead);
                log(sMessage, true);

                if (oSettings.bSendToFiddler)
                {
                    log("Forwarding message to Fiddler...");
                    try
                    {
                        HttpContent entity = new ByteArrayContent(buffer);
                        entity.Headers.Add("Content-Type", "application/json; charset=utf-8");

                        HttpResponseMessage response = await client.PostAsync($"{sFiddlerPrefix}/ExtToApp/{oSettings.sExtensionID ?? "noID"}/{oSettings.sExeName}", entity);
                        if (response.IsSuccessStatusCode)
                        {
                            string sNewBody = await response.Content.ReadAsStringAsync();
                            if (!sNewBody.Contains("Fiddler Echo"))
                            {
                                log("Replacing message body with overridden text from Fiddler.");
                                sMessage = sNewBody;
                            }
                        }
                    }
                    catch (HttpRequestException e)
                    {
                        log($"Call to Fiddler failed: {e.Message}");
                    }
                }

                // Validate that the message is well-formed JSON
                if (null == JSON.JsonDecode(sMessage, out JSON.JSONParseErrors oErrors))
                {
                    // TODO: Force logging body if (!oSettings.bLogMessageBodies)
                    log($"!!! ERROR: JSON Parsing failed at offset {oErrors.iErrorIndex} {oErrors.sWarningText}. Note:Strings must be double-quoted.");
                }

                if (oSettings.bReflectToExtension)
                {
                    // Don't reflect messages over 1mb. They're illegal!)
                    if (sMessage.Length < (1024 * 1024))
                    {
                        log("Reflecting message to extension...");
                        await WriteToExtension(sMessage);
                    }
                    else log("!! Message was over 1mb and must not be reflected !!");
                }

                await WriteToApp(sMessage);
            }
        }

        /// <summary>
        /// This function sits around waiting for messages from the browser extension.
        /// </summary>
        private async Task WatchStdErrFromApp()
        {
            //if (null == oSettings.strmErrFromApp) return;
            byte[] arrErrString = new byte[1024];

            while (true)
            {
                int cbThisRead = await oSettings.strmErrFromApp.ReadAsync(arrErrString, 0, arrErrString.Length, ctsApp.Token);
                if (cbThisRead < 1)
                {
                   return;
                }
                //MaybeWriteBytesToLogfile("-RawRead: ", arrLenBytes, cbSizeRead, cbThisRead);
                string sMessage = Encoding.UTF8.GetString(arrErrString, 0, (int)cbThisRead);
                log("App wrote Std_Err: " + sMessage, false);
            }
        }

        /// <summary>
        /// This function sits around waiting for messages from the native host app.
        /// </summary>
        private async Task MessageShufflerForApp()
        {
            byte[] arrLenBytes = new byte[4];

            while (true)
            {
                int cbSizeRead = 0;
                while (cbSizeRead < 4)
                {
                    int cbThisRead = await oSettings.strmFromApp.ReadAsync(arrLenBytes, cbSizeRead, arrLenBytes.Length - cbSizeRead, ctsApp.Token);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while trying to read message size from (App); got only {cbSizeRead} bytes. Disconnecting.");
                        detachApp();
                        return;
                    }
                    MaybeWriteBytesToLogfile("ReadSizeFromApp-RawRead: ", arrLenBytes, cbSizeRead, cbThisRead);
                    cbSizeRead += cbThisRead;
                }

                UInt32 cbBodyPromised = BitConverter.ToUInt32(arrLenBytes, 0);
                log($"App promised a message of length: {cbBodyPromised:N0}");
                if (cbBodyPromised == 0)
                {
                    log("Got an empty (size==0) message. Is that legal?? Disconnecting.");
                    detachApp();
                    return;
                }

                if (cbBodyPromised > 1024 * 1024)
                {
                    log($"Illegal message size from the NativeMessaging App. Messages are limited to 1mb but this app wants to send {cbBodyPromised:N0} bytes.");
                    detachApp();
                    return;
                }

                byte[] buffer = new byte[cbBodyPromised];
                UInt32 cbBodyRead = 0;

                while (cbBodyRead < cbBodyPromised)
                {
                    int cbThisRead = await oSettings.strmFromApp.ReadAsync(buffer, (int)cbBodyRead, (int)(cbBodyPromised - cbBodyRead), ctsApp.Token);
                    if (cbThisRead < 1)
                    {
                        log($"Got EOF while reading message data from (App); got only {cbBodyRead} bytes. Disconnecting.");
                        detachApp();
                        return;
                    }
                    MaybeWriteBytesToLogfile("ReadBodyFromApp-RawRead: ", buffer, (int)cbBodyRead, cbThisRead);
                    cbBodyRead += (uint)cbThisRead;
                }

                string sMessage = Encoding.UTF8.GetString(buffer, 0, (int)cbBodyPromised);
                log(sMessage, true);

                if (oSettings.bSendToFiddler)
                {
                    try
                    {
                        HttpContent entity = new ByteArrayContent(buffer);
                        entity.Headers.Add("Content-Type", "application/json; charset=utf-8");

                        HttpResponseMessage response = await client.PostAsync($"{sFiddlerPrefix}/AppToExt/{oSettings.sExeName}/{oSettings.sExtensionID ?? "noID"}", entity);
                        if (response.IsSuccessStatusCode)
                        {
                            string sNewBody = await response.Content.ReadAsStringAsync();
                            if (!sNewBody.Contains("Fiddler Echo"))
                            {
                                log("Replacing message body with overridden text from Fiddler.");
                                sMessage = sNewBody;
                            }
                        }
                    }
                    catch (HttpRequestException e)
                    {
                        log($"Call to Fiddler failed: {e.Message}");
                    }
                }

                // Validate that the message is well-formed JSON
                if (null == JSON.JsonDecode(sMessage, out JSON.JSONParseErrors oErrors))
                {
                    // TODO: Force logging body if (!oSettings.bLogMessageBodies)
                    log($"!!! ERROR: JSON Parsing failed at offset {oErrors.iErrorIndex} {oErrors.sWarningText}. Note:Strings must be double-quoted.");
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
            Trace.WriteLine(sMsg);
            MaybeWriteToLogfile(sMsg);
            MaybeWriteToStdErr(sMsg);

            if (!bAppIsShuttingdown && (!bIsBody || oSettings.bLogMessageBodies))
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    txtLog.AppendText(sMsg + "\r\n");
                });
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
                try
                {
                    log("Closing log file.", false);
                    swLogfile.Close();
                }
                catch (Exception eX)
                {
                    log($"Closing log file failed; {eX.Message}");
                }
                swLogfile = null;
            }
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            oSettings.procParent = ProcessInfo.GetParent(Process.GetCurrentProcess());

            {
                string sStartupInfo = $"NMF-View was started with the command line: {Environment.CommandLine} by " +
                    ((null == oSettings.procParent) ? " an unknown parent." : oSettings.procParent.ProcessName + "(" + oSettings.procParent.Id.ToString() + ")");
                Trace.WriteLine(sStartupInfo);
                Console.Error.WriteLine($"****\n**** {sStartupInfo}\n****");
            }

            // Configure default options.
            clbOptions.SetItemChecked(2, true); // Propagate closures
            clbOptions.SetItemChecked(3, true); // Record bodies

            // Configure options based on tokens in the executable name.
            string sCurrentExe = Application.ExecutablePath;
            if (sCurrentExe.Contains(".reflect.")) clbOptions.SetItemChecked(0, true);
            if (sCurrentExe.Contains(".fiddler.")) clbOptions.SetItemChecked(1, true);
            if (sCurrentExe.Contains(".log.")) clbOptions.SetItemChecked(4, true);
            if (sCurrentExe.Contains(".logstderr.")) { oSettings.bLogToStdErr = true; log("Echoing log messages to StdErr."); }
            if (sCurrentExe.Contains(".immortal.")) clbOptions.SetItemChecked(5, true);

            string sExtraInfo = $" [{Path.GetFileName(Application.ExecutablePath)}:{Process.GetCurrentProcess().Id}{(Utilities.IsUserAnAdmin() ? " Elevated" : String.Empty)}]";
            log($"I am{sExtraInfo}, launched by [{((null != oSettings.procParent) ? (oSettings.procParent.ProcessName + ':' + oSettings.procParent.Id) : "unknown")}].");
            lblVersion.Text = $"v{Application.ProductVersion} [{((8 == IntPtr.Size) ? "64" : "32")}-bit]";
            Text += sExtraInfo;  // Append extra info to form caption.
            log(Utilities.DescribeStartupHandles());
            log(DescribeStandardHandles());

            var arrArgs = Environment.GetCommandLineArgs();
            if (arrArgs.Length > 1) oSettings.sExtensionID = arrArgs[1];
            if (String.IsNullOrEmpty(oSettings.sExtensionID))
            {
                clbOptions.SetItemChecked(2, false); Debug.Assert(oSettings.bPropagateClosures == false);
                oSettings.sExtensionID = "unknown";
                log("Started without an extension ID.\r\n\r\n"
                  + "Note: This application does not seem to have been started by a Chromium-based browser\r\n"
                  + "to respond to NativeMessaging requests. Use the `Configure Hosts` tab below to reconfigure\r\n"
                  + "a registered NativeMessaging Host to proxy its traffic through an instance of this app.\r\n"
                  + "\r\n---------------------------------\r\n"
                  );
                tcApp.SelectedTab = pageAbout;
            }
            else
            {
                log($"extension: {oSettings.sExtensionID}");
            }
            if (arrArgs.Length > 2)
            {
                // The parent-window value is only non-zero when the calling context is not a background script.
                // https://stackoverflow.com/questions/56544152/when-does-chrome-pass-a-value-other-then-0-to-the-native-messaging-host-for-pa
                // https://source.chromium.org/chromium/chromium/src/+/main:chrome/test/data/native_messaging/native_hosts/echo.py;l=30?q=parent-window&sq=&ss=chromium
                if (arrArgs[2].StartsWith("--parent-window="))
                {
                    if (!ulong.TryParse(arrArgs[2].Substring(16), out oSettings.iParentWindow))
                    {
                        oSettings.iParentWindow = 0;
                    }
                    log($"parent-window: {oSettings.iParentWindow:x8} {((oSettings.iParentWindow==0)?"(Background Script)":String.Empty)}");
                }
            }
            oSettings.sExtensionID = oSettings.sExtensionID.Replace("chrome-extension://", String.Empty).TrimEnd('/');
            toolTip1.SetToolTip(pbExt, $"Connected to {oSettings.sExtensionID}.\nDouble-click to disconnect.");
            toolTip1.SetToolTip(pbApp, $"Click to set the ClientHandler to another instance of this app.");
            log("Listening for messages...");

            PopulateEnvironment();

            // Ensure that our window is showing.
            if (!sCurrentExe.Contains(".noshow.")) ShowWindow((int)this.Handle, SW_SHOW);

            /* if (oSettings.sExtensionID != "unknown") */
            ConnectMostLikelyApp();
            WaitForMessages();
            Utilities.SetCueText(txtSearch, "Search...");
        }

        private void PopulateEnvironment()
        {
            try
            {
                List<string> slEnv = new List<string>();
                foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
                    slEnv.Add($"{de.Key} = {de.Value}");
                slEnv.Sort();
                rtbEnvironment.Text = String.Join("\r\n", slEnv);
            }
            catch (Exception eX)
            {
                rtbEnvironment.Text = eX.Message;
            }
        }

        private void WaitForMessages()
        {
            try
            {
                log(DescribeStandardHandles());
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

        private string DescribeStandardHandles()
        {
            var hIn = GetStdHandle(STD_INPUT_HANDLE);
            var hInType = GetFileType2(hIn);
            var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
            var hOutType = GetFileType2(hOut);
            var hErr = GetStdHandle(STD_ERROR_HANDLE);
            var hErrType = GetFileType2(hErr);
            return ($"GetStdHandle() says stdin=(0x{hIn.ToInt64():x}, {hInType}); " +
                $"stdout=(0x{hOut.ToInt64():x}, {hOutType}); " +
                $"stderr=(0x{hErr.ToInt64():x}, {hErrType}).");
        }

        private void clbOptions_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.Index == 0) { oSettings.bReflectToExtension = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 1) { oSettings.bSendToFiddler = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 2) { oSettings.bPropagateClosures = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 3) { oSettings.bLogMessageBodies = (e.NewValue == CheckState.Checked); return; }
            if (e.Index == 4)
            {
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
            if (e.Index == 5)
            {
                e.NewValue = CheckState.Indeterminate;
                if (e.CurrentValue != CheckState.Unchecked) { return; }
                if (Utilities.DenyProcessTermination())
                {
                    log("Immortality enabled.\r\nTerminateProcess() calls from non-elevated applications will now be ignored with an\r\n" +
                        "ACCESS_DENIED result, but unfortunately this cannot protect us from our Chromium parent\r\n" +
                        "because it already has a handle to us with the Terminate process right granted.\r\n");
                }
                else
                {
                    log("Immortality could not be enabled.");
                }
                return;
            }
        }

        private void pbApp_Click(object sender, EventArgs e)
        {
            if (IsAppAttached()) return;

            if (!ConnectMostLikelyApp())
            {
                ConnectApp(Application.ExecutablePath);
            }
        }

        private bool ConnectMostLikelyApp()
        {
            // If we are `RealHost.proxy.exe`, then see whether `RealHost.exe` exists, and if so, use that.
            // Remove any option flags in the command line.
            string sCurrentExe = Application.ExecutablePath.Replace(".log", string.Empty).Replace(".fiddler", string.Empty);
            if (sCurrentExe.Contains(".proxy"))
            {
                string sCandidate = sCurrentExe.Replace(".proxy", string.Empty);
                log($"Checking for {sCandidate}...");
                if (File.Exists(sCandidate))
                {
                    return ConnectApp(sCandidate);
                }
            }

            /*
             * I'm not sure what I was thinking when I wrote this; it seems almost guaranteed to just
             * find this proxy.
            listHosts = RegisteredHosts.GetAllHosts();

            // @"C:\program files\windows security\browserCore\browserCore.exe";
            if (String.IsNullOrEmpty(oSettings.sExtensionID) ||
                null == listHosts)
            {
                return false;
            }

            foreach (var host in listHosts)
            {
                if (host.AllowedExtensions.IndexOf(oSettings.sExtensionID, StringComparison.OrdinalIgnoreCase)>-1)
                {
                    string sCommand = Path.GetFileName(host.Command);
                    if (!Path.IsPathRooted(sCommand))
                    {
                        sCommand = Path.GetDirectoryName(host.ManifestFilename) + sCommand;
                    }
                    log($"Invocation by {oSettings.sExtensionID} suggests we should launch {sCommand}");

                    if (String.Equals(sCommand, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        log($"...but that would create an infinite loop, bailing out here.");
                        return false;
                    }
                    return ConnectApp(sCommand);
                }
            }
            */
            log ($"Did not find any likely nativeHost for {oSettings.sExtensionID}. If you are using NMMeddler as a mock,\r\nthe Injector tab allows sending messages to the browser.");
            clbOptions.SetItemChecked(2, false); Debug.Assert(oSettings.bPropagateClosures == false);
            return false;
        }

        private bool ConnectApp(string sFilename)
        {
            // https://source.chromium.org/chromium/chromium/src/+/main:chrome/browser/extensions/api/messaging/native_process_launcher_win.cc;bpv=1;bpt=1

            using (Process myProcess = new Process())
            {
                myProcess.StartInfo.FileName = sFilename;
                myProcess.StartInfo.Arguments = $"chrome-extension://{oSettings.sExtensionID} --parent-window={oSettings.iParentWindow}"; // TODO: Parent window
                myProcess.StartInfo.UseShellExecute = false;
                myProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(myProcess.StartInfo.FileName);

                // TODO: If the compat hack lands for Chrome, then we should use the same logic here to show GUI if the app targets SUBSYSTEM_WINDOWS
                // https://weblogs.asp.net/whaggard/223020
                // https://source.chromium.org/chromium/chromium/src/+/main:base/process/launch_win.cc;l=298;drc=1ad438dde6b39e1c0d04b8f8cb27c1a14ba6f90e
                myProcess.StartInfo.CreateNoWindow = true;
                myProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;  // Does this do anything?

                myProcess.StartInfo.RedirectStandardInput = true;
                myProcess.StartInfo.RedirectStandardOutput = true;
                myProcess.StartInfo.RedirectStandardError = true;

                try
                {
                    myProcess.Start();
                }
                catch (Exception eX)
                {
                    log($"Invoking '{myProcess.StartInfo.FileName}' failed. {eX.Message}");
                    return false;
                }

                pbApp.BackColor = Color.FromArgb(159, 255, 159);
                toolTip1.SetToolTip(pbApp, $"#{myProcess.Id} - {myProcess.StartInfo.FileName}\nDouble-click to disconnect.");

                oSettings.sExeName = Path.GetFileName(myProcess.StartInfo.FileName);

                // https://docs.microsoft.com/en-us/dotnet/api/system.console?view=net-5.0#Streams
                oSettings.strmToApp = myProcess.StandardInput.BaseStream;
                oSettings.strmFromApp = myProcess.StandardOutput.BaseStream;
                oSettings.strmErrFromApp = myProcess.StandardError.BaseStream;
                log($"Started {oSettings.sExeName} as the proxied NativeMessagingHost.");
                Task.Run(async () => await MessageShufflerForApp());
                Task.Run(async () => await WatchStdErrFromApp());
                return true;
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
                return;
            }
            if (tcApp.SelectedTab == pageTroubleshooter)
            {
                PopulateTroubleshooter();
                return;
            }
            if (tcApp.SelectedTab == pageInjector)
            {
                btnSendToApp.Enabled = ((txtSendToApp.TextLength > 0) && IsAppAttached());
                btnSendToExtension.Enabled = ((txtSendToExtension.TextLength > 0) && IsExtensionAttached());
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
                    // It would be nice to show the owner here, but we can't do so readily because we cannot get the handle to the pipe
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
            if (sComSpec.IndexOf("cmd.exe", StringComparison.OrdinalIgnoreCase) < 1)
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
            listHosts = RegisteredHosts.GetAllHosts();
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
            if ((e.Modifiers == Keys.Alt) && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ShowSelectedManifestInExplorer();
                return;
            }
        }

        private void lvHosts_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (lvHosts.SelectedItems.Count == 1)
            {
                if (Control.ModifierKeys == Keys.Alt)
                {
                    ShowSelectedManifestInExplorer();
                }
                else if (Control.ModifierKeys == Keys.Shift)
                {
                    ShowSelectedEntryInRegEdit();
                }
            }
        }

        private void ShowSelectedEntryInRegEdit()
        {
            ListViewItem oLVI = lvHosts.SelectedItems[0];
            RegisteredHosts.HostEntry oHE = (RegisteredHosts.HostEntry)oLVI.Tag;
            Utilities.OpenRegeditTo(oHE.RegistryKeyPath);
        }

        private void ShowSelectedManifestInExplorer()
        {
            ListViewItem oLVI = lvHosts.SelectedItems[0];
            RegisteredHosts.HostEntry oHE = (RegisteredHosts.HostEntry)oLVI.Tag;
            Utilities.OpenExplorerTo(oHE.ManifestFilename);
        }

        private void frmMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            bAppIsShuttingdown = true;
            detachApp();
            detachExtension();
            CloseLogfile();
        }

        private void txtSendToApp_TextChanged(object sender, EventArgs e)
        {
            btnSendToApp.Enabled = (/*(txtSendToApp.TextLength > 0) && */ IsAppAttached());
        }

        private void txtSendToExtension_TextChanged(object sender, EventArgs e)
        {
            btnSendToExtension.Enabled = (/*(txtSendToExtension.TextLength > 0) && */ IsExtensionAttached());
        }

        private async void btnSendToApp_Click(object sender, EventArgs e)
        {
            txtSendToApp.Text = txtSendToApp.Text.Trim();
            log($"Injecting message to app '{txtSendToApp.Text}'");
            await WriteToApp(txtSendToApp.Text);
        }

        private async void btnSendToExtension_Click(object sender, EventArgs e)
        {
            txtSendToExtension.Text = txtSendToExtension.Text.Trim();
            log($"Injecting message to extension '{txtSendToExtension.Text}'");
            await WriteToExtension(txtSendToExtension.Text);
        }

        private void txtLog_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Modifiers == Keys.Control) && e.KeyCode == Keys.X)
            {
                e.SuppressKeyPress = true;
                txtLog.Clear();
                return;
            }
        }

        private void frmMain_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                e.SuppressKeyPress = e.Handled = true;
                if (tcApp.SelectedTab == pageRegisteredHosts)
                {
                    PopulateHosts();
                    return;
                }
                if (tcApp.SelectedTab == pageTroubleshooter)
                {
                    PopulateTroubleshooter();
                    return;
                }
            }

            if ((tcApp.SelectedIndex == 0) && (e.KeyCode == Keys.F && (e.Control || e.Alt)))
            {
                e.SuppressKeyPress = e.Handled = true;
                txtSearch.Focus();
            }
        }

        private void btnPokeStdErr_Click(object sender, EventArgs e)
        {
            Console.Error.WriteLine("Poking StdErr @" + DateTime.Now.ToString());
        }

        private void txtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Escape) || (e.KeyCode == Keys.Back && txtSearch.Text.Length <2))
            {
                e.Handled = e.SuppressKeyPress = true;
                txtSearch.Clear();
                txtLog.Select(txtLog.TextLength, 0);
                return;
            }
            if (e.KeyCode == Keys.X && e.Control)
            {
                e.Handled = e.SuppressKeyPress = true;
                txtLog.Clear();
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (txtSearch.Text.Length > 0)
                {
                    int iStartAt = txtLog.SelectionStart + 1;
                    if (iStartAt >= txtLog.TextLength) iStartAt = 0;
                    if (txtLog.Find(txtSearch.Text, iStartAt, RichTextBoxFinds.None) > -1)
                    {
                        e.Handled = e.SuppressKeyPress = true;
                    }
                }
                return;
            }
        }

        private void txtSearch_TextChanged(object sender, EventArgs e)
        {
            if (txtSearch.Text.Length > 0)
            {  // Start at 0 if using TAB key for GoNext
                txtSearch.BackColor = (txtLog.Find(txtSearch.Text, 0, RichTextBoxFinds.None) > -1) ?
                    Color.LightGreen :
                    Color.OrangeRed;
            }
            else
            {
                txtSearch.BackColor = Color.FromKnownColor(KnownColor.Window);
                txtLog.Select(txtLog.TextLength, 0);
            }
        }
    }
}
