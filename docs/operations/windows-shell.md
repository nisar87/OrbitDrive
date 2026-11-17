# Windows Explorer integration (D6)

## What exists
- `apps/desktop/OrbitDrive.ShellExtension/` — C++17 COM DLL `OrbitDriveShell.dll` implementing `IExplorerCommand`
  ("Send to my phone via OrbitDrive", one sub-command per receiver, shown with last-seen time). It does no hashing, networking
  or business logic: it reads `receivers.txt`, then hands the selected paths as data to the running agent over its named pipe,
  or writes a spool file the agent picks up (and starts the app). Folders and unknown/empty receiver lists are shown disabled.
- `AppxManifest.xml` — sparse package that gives the DLL package identity (required for the Windows 11 modern menu).
  Publisher is the placeholder `CN=OrbitDrive Development` and the package is **unsigned**.
- `register-dev.ps1` — developer registration for a machine in developer mode. `build.ps1` builds with MSVC.
- Classic fallback: the agent can create `OrbitDrive - <name>.lnk` shortcuts in the user's SendTo folder (config `SendToShortcuts`).

## What is not verified
Invocation from the modern menu in real Windows 11 Explorer. CI runs on Windows Server, which has no modern Explorer menu;
the CI test drives the DLL through COM with real `IShellItemArray`s only.

## Owner steps (not done)
1. A Windows 11 machine, standard user.
2. A code-signing identity; set its subject as the manifest `Publisher`, sign the MSIX/sparse package, install it.
3. Right-click a file → Show more options is not needed: the command must appear in the modern menu. Record a screenshot.
