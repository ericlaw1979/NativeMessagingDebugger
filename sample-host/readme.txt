NOTE: You will need to follow these instructions to get this demo working. By default, these files assume you checked out into the folder path `C:\src\nm\` but you can easily adjust them.

This folder contains the manifest for the NativeMessageHost executable. You will need to update the manifest.json with your extension's ID string as seen on the about://extensions page, and you will need to update the path to the executable in both the JSON and the InstallRegKeys.json file.

This folder also contains installation scripts that update the registry so that Chromium-based browsers can map our Native Host's ID/name ("com.bayden.nmf.demo") to the location of the executable on disk. Use |InstallRegKeys.reg| to install for any Chromium-based browser
in the current Windows user account.

(The |InstallRegKeysEdgeHKLM| script installs for any Edge browser under any Windows user account; Chrome will not see this registration
due to the "Edge" in the path name.)

