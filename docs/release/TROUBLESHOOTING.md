# Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| Phone says the PC is **offline** | PC asleep, shut down, signed out, paused, or no internet | Wake/sign in; check the tray state (Paused?); check the PC's internet; wait about a minute after reconnecting |
| Pairing code **expired** | Codes live 2 minutes | Create a new one on the PC and approve promptly |
| Pairing "codes do not match" | Not your request | Deny on the PC; remove unknown browsers under Devices |
| Download stops at a network change | The connection was cut | Start the download again; it resumes from the saved position when the browser supports it |
| "This file changed" | The file was edited during the transfer | Retry; OrbitDrive refuses to join two different versions |
| A folder is missing / "unavailable" | Disk removed, folder moved or renamed | Re-add the shared folder; roots are bound to their disk |
| Uploads refused | Uploads are off for that folder, quota or size limit, or a blocked file type | Turn uploads on for the folder under Shared folders, check the allowance |
| Windows warns about the publisher | The build is unsigned | Verify the checksum; wait for the signed release |
| Agent says "already running" | One agent per user | Use the tray icon; Quit first |
| Web app shows a blank page after an update | Old cached files | Reload; the page is never cached, assets are versioned |

## Support bundle
Console, **Diagnostics**: preview what will be included, then save. It contains versions, settings that matter, recent activity events and counters. It **never** contains file names, folder paths, tokens, or file contents (names are replaced by fixed words). Nothing is sent anywhere automatically.

## Logs and where state lives
`%LOCALAPPDATA%\OrbitDrive\agent` holds settings, paired browsers and a rotating activity history. Delete that folder to reset the PC completely (then Disconnect this PC on the website).
