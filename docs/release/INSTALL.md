# Installing, updating and removing OrbitDrive (development build)

**Status: release candidate, unsigned, no installer.** This build is a self-contained folder (no separate .NET install needed). There is no MSIX/installer yet
(needs the owner's signing identity), so Windows SmartScreen will warn that the publisher is unknown. Only run files whose checksum you verified.

## 1. Verify the download
1. Put `OrbitDrive-<version>-win-x64.zip` and `SHA256SUMS` in one folder.
2. PowerShell: `(Get-FileHash .\OrbitDrive-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLower()` and compare with the line for that file in `SHA256SUMS`. They must be identical; if not, delete the file.
3. `release-manifest.json` names the exact source commit and CI run the files were built from.

## 2. Install
1. Extract the zip to a folder you own, for example `C:\Users\<you>\OrbitDrive\` (not `Program Files`: this build runs as you, without administrator rights, and needs no elevation).
2. Start `OrbitDrive.exe`. It lives in the notification area (tray). Closing the window keeps it running; **Quit** in the tray menu stops it.
3. Open **Shared folders** and add the folders you want to reach from your phone. Everything is **read-only** unless you switch uploads on for a folder.
4. The relay and web addresses of the development service are already filled in from `defaults.json` next to `OrbitDrive.exe` (only used while none is set; plain-http non-local addresses are ignored). Change them in Settings if you use another service.
5. Pair a phone: open the web address on the phone, sign in, choose "Pair a PC", enter or scan the code, and **approve on the PC** after checking that the code on both screens matches.

## 3. Update (no reinstall, no uninstall)
Install once. After that, open **Settings > Software updates** and press **Check for updates**, then **Update now** when one is offered (the app also checks by itself shortly after it starts and every few hours, and shows a banner on Overview).
**Update now** downloads the new build, checks the signature, size and SHA-256 against the signed update notice, waits for running transfers to finish (new ones are paused), swaps the program folder, and starts the new version.
Nothing in `%LOCALAPPDATA%\OrbitDrive\agent` (folders, paired browsers, settings) is touched. If anything fails, the old version is put back and started again, and the app tells you what happened.
- This build is **not Windows-code-signed**, so SmartScreen may warn the first time you run the downloaded zip ("More info" > "Run anyway"). Later updates are applied by OrbitDrive itself from a file whose signature it verified, so they do not trigger that warning.
- The update key is separate from a Windows certificate: it proves "this came from the OrbitDrive project", it is not trusted by Windows.
- Install the program in a folder you own (not `Program Files`), because the updater replaces the folder without administrator rights.
- If the app says "Updates are not set up in this build yet", the owner has not yet added the update key; see `OWNER_HANDOFF.md`.
- Manual fallback: Quit from the tray, extract a newer zip over the old folder, start it again.

## 4. Uninstall (nothing else is left behind except what you choose to keep)
1. Quit from the tray. If "start when I sign in" was on, switch it off first (it only adds one value, `OrbitDrive`, under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
2. Delete the program folder.
3. To remove paired browsers and settings as well, delete `%LOCALAPPDATA%\OrbitDrive`. Your own files are never touched by OrbitDrive (it is read-only by default and never deletes).
4. On the website, **Disconnect this PC** (or delete the account) so the service forgets it.

## 5. What it needs
See `SUPPORT_MATRIX.md`. In short: Windows 10/11 64-bit, the PC switched on, you signed in to Windows, and an internet connection (the PC only connects outwards; no router settings or open ports).
