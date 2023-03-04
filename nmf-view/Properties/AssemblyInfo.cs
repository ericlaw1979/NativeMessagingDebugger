using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("NativeMessaging Meddler")]
[assembly: AssemblyDescription("Debug communication with Chromium NativeMessaging Hosts.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Eric Lawrence")]
[assembly: AssemblyProduct("NMM")]
[assembly: AssemblyCopyright("Copyright ©2023 Eric Lawrence")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.4.0")]

// v1.0.3
// Add ".reflect." and ".noshow." name tokens.
// Ensure form is visible by default, even if launched directly with SW_HIDE.
// Log stdio handle values.
