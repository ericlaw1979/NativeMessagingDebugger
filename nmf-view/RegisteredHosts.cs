using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace nmf_view
{
    class RegisteredHosts
    {
        internal struct HostEntry
        {
            /// <summary>
            /// For example, com.bayden.moarTLS
            /// </summary>
            public string Name;
            public string ManifestFilename;
            public string Command;
            public string OriginalCommand;
            public string Description;
            public string SupportedBrowsers;
            public string AllowedExtensions;
            public int iPriority;
            public string RegistryKeyPath;

            public override string ToString()
            {
                return "--------------------------------\n" +
                       $"Name:\t\t{Name}\n" +
                       $"Priority:\t{iPriority}\n" +
                       $"Manifest:\t{ManifestFilename}\n" +
                       $"Command:\t{OriginalCommand}\n" +
                       $"Description:\t{Description}\n" +
                       $"Browsers:\t{SupportedBrowsers}\n" +
                       $"Extensions:\t{AllowedExtensions}\n" +
                       $"RegKey:\t\t{RegistryKeyPath}\n" +
                       "--------------------------------\n";
            }
        };

        // TODO: Add support for Mozilla
        // HKEY_CURRENT_USER\Software\Mozilla\NativeMessagingHosts\ping_pong
        // HKEY_LOCAL_MACHINE\Software\Mozilla\NativeMessagingHosts\ping_pong

        // HKCU is prioritized over HKLM.
        private static readonly RegistryHive[] hives = { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        private static readonly string[] keysPriorityOrder = {
                @"SOFTWARE\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\",
                @"SOFTWARE\WOW6432Node\Chromium\NativeMessagingHosts\",
                @"SOFTWARE\WOW6432Node\Google\Chrome\NativeMessagingHosts\",
                @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\",
                @"SOFTWARE\Chromium\NativeMessagingHosts\",
                @"SOFTWARE\Google\Chrome\NativeMessagingHosts\"};

        // https://docs.microsoft.com/en-us/microsoft-edge/extensions-chromium/developer-guide/native-messaging?tabs=windows

        private static void ReadRegistry(List<HostEntry> listResults) {
            int iCurrentPriority = 0;
            foreach (RegistryHive rhHive in hives)
            {
                // TODO: This doesn't seem to be quite right. Are we getting everything??          Should we have the wow32 nodes above or should we just change the registry view??
                RegistryKey rkBase = RegistryKey.OpenBaseKey(rhHive, RegistryView.Registry64);
                foreach (string sKey in keysPriorityOrder)
                {
                    ++iCurrentPriority;
                    // Explicit permissions improves performance.
                    RegistryKey oReg = rkBase.OpenSubKey(sKey,
                        RegistryKeyPermissionCheck.ReadSubTree,
                        System.Security.AccessControl.RegistryRights.ReadKey);

                    if (null == oReg) continue;

                    string[] arrEntries = oReg.GetSubKeyNames();
                    foreach (string sHost in arrEntries)
                    {
                        RegistryKey rkEntry = oReg.OpenSubKey(sHost,
                                RegistryKeyPermissionCheck.ReadSubTree,
                                System.Security.AccessControl.RegistryRights.ReadKey);

                        var oHE = new HostEntry()
                        {
                            Name = sHost,
                            ManifestFilename = rkEntry.GetValue(String.Empty) as string,
                            iPriority = iCurrentPriority,
                            RegistryKeyPath = rkEntry.ToString()
                        };
                        switch (iCurrentPriority)
                        {
                            case 2:
                            case 5:
                            case 8:
                            case 11:
                                oHE.SupportedBrowsers = "Chromium; Edge";
                                break;
                            case 1:
                            case 4:
                            case 7:
                            case 10:
                                oHE.SupportedBrowsers = "Edge";
                                break;
                        }
                        FillHostEntry(ref oHE);
                        listResults.Add(oHE);
                    }
                    oReg.Close();
                }
                rkBase.Close();
            }
        }

        private static void FillHostEntry(ref HostEntry oHE)
        {
            try
            {
                if (String.IsNullOrEmpty(oHE.ManifestFilename))
                {
                    oHE.Description = "ERROR: No manifest PATH specified in registry.";
                    oHE.AllowedExtensions = oHE.Command = String.Empty;
                    return;
                }
                if (!File.Exists(oHE.ManifestFilename)) 
                {
                    oHE.Description = "ERROR: Specified manifest PATH does not exist.";
                    oHE.AllowedExtensions = oHE.Command = String.Empty;
                    return;
                }

                // TODO: Check Manifest filename for special characters that cause problems.

                string sJSON = File.ReadAllText(oHE.ManifestFilename, Encoding.UTF8);
                if (!(JSON.JsonDecode(sJSON, out JSON.JSONParseErrors oErrors) is Hashtable htManifest))
                {
                    oHE.Description = $"ERROR: Manifest Parsing failed at offset {oErrors.iErrorIndex} {oErrors.sWarningText}. Note:Strings must be double-quoted.";
                    oHE.Command = "???";
                    oHE.AllowedExtensions = "???";
                    return;
                }
                // assert oHE.Name == htManifest["name"] as string;
                oHE.Description = htManifest["description"] as string ?? String.Empty;
                oHE.Command = htManifest["path"] as string ?? String.Empty;

                // TODO: Check PATH (after full-qualification) filename for special characters that cause problems.

                oHE.OriginalCommand = oHE.Command;  // TODO: Fix this.

                ArrayList alAllowedOrigins = htManifest["allowed_origins"] as ArrayList;
                if (alAllowedOrigins != null)
                {
                    oHE.AllowedExtensions = string.Join(";", alAllowedOrigins.ToArray().Select(s => (s as String).Trim().TrimEnd('/').Replace("chrome-extension://", String.Empty)));
                }
                else
                {
                    oHE.AllowedExtensions = String.Empty; // TODO: Mark BAD
                }

            }
            catch (Exception eX)
            {
                oHE.Description = $"ERROR: {eX.Message}.";
            }
        }

        internal static List<HostEntry> GetAllHosts()
        {
            List<HostEntry> listResults = new List<HostEntry>();
            ReadRegistry(listResults);
            return listResults;
        }
    }
}
