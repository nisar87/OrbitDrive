# Installing, updating and removing OrbitDrive (release candidate)

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
4. Set the relay address and web address given to you by the person who deployed the service (Settings, or `orbitdrive-agent run --gateway <https-url> --web-origin <https-url>`).
5. Pair a phone: open the web address on the phone, sign in, choose "Pair a PC", enter or scan the code, and **approve on the PC** after checking that the code on both screens matches.

## 3. Update
Until the signed installer exists, update by hand: Quit from the tray, extract the new zip over (or next to) the old folder, start it again. Your folders, paired browsers and settings are kept in
`%LOCALAPPDATA%\OrbitDrive\agent` and survive. The in-app signed-update engine is built and tested, but no update feed has been published.

## 4. Uninstall (nothing else is left behind except what you choose to keep)
1. Quit from the tray. If "start when I sign in" was on, switch it off first (it only adds one value, `OrbitDrive`, under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
2. Delete the program folder.
3. To remove paired browsers and settings as well, delete `%LOCALAPPDATA%\OrbitDrive`. Your own files are never touched by OrbitDrive (it is read-only by default and never deletes).
4. On the website, **Disconnect this PC** (or delete the account) so the service forgets it.

## 5. What it needs
See `SUPPORT_MATRIX.md`. In short: Windows 10/11 64-bit, the PC switched on, you signed in to Windows, and an internet connection (the PC only connects outwards; no router settings or open ports).
