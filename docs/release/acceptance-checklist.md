# End-to-end acceptance checklist (owner-run, real hardware)

The release stays **"release candidate awaiting owner-side evidence"** until every line below for each advertised platform has a recorded result (date, machine, Windows build, phone model/OS/browser, who ran it). Automated CI results do not tick these boxes.
Record results in `docs/evidence/phase-evidence.md` under "Acceptance run N" and update `docs/evidence/REQUIREMENTS_MATRIX.md`.

Use your own files: one folder of mixed small files and one **1 GiB** file whose SHA-256 you computed on the PC (`Get-FileHash`).

| # | Step | Pass when | Result |
|---|---|---|---|
| 1 | Verify download checksum, extract, start (standard user, no admin prompt) | hash equal; app shows in tray | ☐ |
| 2 | Approve a root (a folder that is not Documents' parent, not a system folder) | appears on phone only after approval | ☐ |
| 3 | Pair a phone on **mobile data** (not Wi-Fi) with matching codes | PC shows the browser; wrong code is refused | ☐ |
| 4 | Browse, sort, search, preview an image/PDF/text | correct names/sizes; nothing outside the root reachable | ☐ |
| 5 | Download the 1 GiB file to the phone | phone's saved file SHA-256 equals the PC's | ☐ |
| 6 | Interrupt (switch Wi-Fi/mobile data or airplane mode for 10 s) and resume through the supported path | completes; hash equal; no duplicate or partial leftovers | ☐ |
| 7 | Send from real Explorer (modern menu, signed build) and classic Send to | phone's Inbox receives; accept; file correct | ☐ |
| 8 | Receive on the phone and accept; decline another | declined one is gone | ☐ |
| 9 | Lock the PC, sleep the PC, wake | behaviour matches `SUPPORT_MATRIX.md` | ☐ |
| 10 | Revoke the phone from the PC; try browsing/downloading from it | every request is refused; a running download stops | ☐ |
| 11 | Uploads: enable for one folder, upload a file; name collision; try to overwrite | no overwrite; file arrives with Mark-of-the-Web | ☐ |
| 12 | Uninstall per `INSTALL.md` | nothing left but what INSTALL.md says | ☐ |
| 13 | Accessibility spot-check: keyboard only on the console; 200 % text; high contrast | usable | ☐ |
