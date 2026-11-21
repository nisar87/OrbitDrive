# Owner console redesign (Windows)

Status: five pages rebuilt and green in CI on `feature/console-redesign` (not merged: the owner reviews the screenshots first). Toasts and the tray flyout are not built yet.
Scope: the WPF owner console UI layer only. No protocol, gateway, agent security or Core filesystem-policy change. Where the UI would like data the agent does not
expose, it is listed below instead of being invented.

## 1. Information architecture

Left navigation, five pages (Ctrl+1 to Ctrl+5), one purpose each:

| Page | Job | One main action |
|---|---|---|
| Home | Is sharing on, paused or broken? What needs me? | the button that fixes the current state (Resume / Start / Set up / Update) |
| Shared folders | What can my devices see? | Share a folder… |
| Devices | Pair a phone, answer a request, remove a device | Pair a device (Approve while a request waits) |
| Activity | What is being sent, what happened lately | none (read only; Clear history is destructive and confirmed) |
| Settings | General, Sending, Privacy, Advanced (relay address, updates, diagnostics, Quit) | Save and connect (Advanced) |

Tray menu is unchanged: Open OrbitDrive, Pause/resume remote access, Quit OrbitDrive. Quit is also in Settings > Advanced. Close-to-tray, single instance,
start at sign-in, lock/metered pause and bounded crash recovery are unchanged behaviour (tests prove it).

## 2. Structure

`MainWindow.xaml` (shell: Mica-ready window, navigation `ListBox`, page host) + `ViewModels/*PageViewModel` + `Views/*Page.xaml` + shared `Controls/` and `Theme/`.
CommunityToolkit.Mvvm (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand`) with hand-written properties (the build treats warnings as errors; no source
generators). Services own everything with side effects: `AgentConnection` (local channel, bounded restarts), `ConsoleHost` (1 s refresh, typed `ConsoleStatus`,
device list), `PausePolicies` (lock/metered), `TrayService`. View models take dialogs as delegates (`PickFolder`, `Confirm`, `CopyText`) so they have no window
dependency. Pages show the agent's real state and keep none of their own.

## 3. Tokens (`Theme/Tokens.xaml`)

Fonts (Segoe UI Variable Display/Text, Segoe Fluent Icons), size scale (Caption 12 … Title 28, Stat 32), spacing and `Inset.*` thicknesses, radii (Card, Control,
Pill), `Nav.Width` 216 / `Nav.WidthCompact` 56, `Page.MaxWidth` 860, motion durations, `Size.TouchTarget` 34, `Opacity.Muted` 0.74.
Colours are not redefined: status colours are Fluent's own theme-aware brushes (`SystemFillColor{Success,Caution,Critical,Neutral}Brush`, `AccentFillColorDefaultBrush`
and the `…BackgroundBrush` tints) used with `DynamicResource`, so light, dark and high contrast follow Windows. (A brush defined in a dictionary with a dynamic
`Color` throws when first used; CI found that. See phase evidence.) Pages contain no hard-coded colours or sizes except a few layout constants (QR size 208).

## 4. Components

`StatusPill` (glyph + word + tint), `InfoBar` (severity, title, message, optional action), `EmptyState` (glyph, title, message, optional action), `Style.Card`,
`Style.CardItem` (selectable card row), `Style.RowItem` (plain list row), `Style.NavItem`, `Style.Text.*` (Base, PageTitle, Group, Section, Strong, Muted, Pill,
Icon, Error), `Style.FocusVisual`. Device card, folder row and transfer row are data templates over small records (`DeviceItem`, `FolderItem`, `TransferRow`).

## 5. States designed on every page

Empty (EmptyState with the next step), loading ("Starting…"), error (plain-language text next to the action that failed, critical glyph), offline / agent not running
(Home headline + Start button; other pages keep what they last showed and Home explains why), paused (headline and Resume), metered (InfoBar), update available
(InfoBar), pairing request waiting (InfoBar on Home, card on Devices), unavailable folder (pill + note).

## 6. Copy

Short, plain, no jargon. Examples: "Sharing is on", "Sharing is paused", "Nothing is shared yet", "Handed to the relay". The Privacy panel states what is true: files pass
through the relay, are encrypted in transit (HTTPS), are **not** end-to-end encrypted, the relay can see contents and names while they pass and keeps no copy.
PC-side wording never claims a file was saved on the phone ("sent to the relay"). The history shows the agent's fixed codes in plain words; unknown codes are shown as recorded.

## 7. Accessibility review (code review + screenshots; no assistive-technology run)

Done: every control has an AutomationId and a name; list items carry a full spoken name (for example "Pixel 9, Last seen just now"); state is never colour alone (pills,
glyph + word, critical glyph beside error text); visible keyboard focus via one `FocusVisual` style; every control is reachable by Tab on every page (UI test);
Ctrl+1..5; secondary text measured on screenshots at about 5.5:1 (light) / 7:1 (dark) and full strength in Windows high contrast; selected card rows use a subtle fill
plus an accent bar so text keeps contrast; the nav collapses to icons below 860 px; headings are marked (level 1 page title, level 2 groups).
Remaining / unverified: Narrator and NVDA reading order; 100–200 % DPI on a real display; Windows high contrast (known Fluent crash reports in .NET, to test on Windows 11);
reduce-animation setting (there are almost no animations, not verified); the disabled picture-preview switch contrast; the pairing link text box is read-only text,
not a link; the QR code has a name but is by nature not usable by a screen reader (the short code beside it is the accessible route); toast notifications not built.

## 8. Verified vs assumed

Verified in CI on windows-2025 (Windows Server): the app starts, every page loads in light and dark, ~108 UI Automation checks against a real agent and gateway
(pairing, approval, revoke, folders, hidden items, uploads off by default, limits, previews switch, Explorer Send-to on/off, history, lock policy, close-to-tray, single
instance, crash recovery), screenshots reviewed.
Assumed / not verified: Mica backdrop (`ThemeMode` is expected to apply it), real Windows 11 rendering, DPI scaling, high contrast, lock/unlock on a real session, Explorer
drag-and-drop onto the Folders page, the folder picker and the confirmation dialogs on Windows 11 (modal; the test answers the Remove confirmation by keyboard), clipboard
copy, real phone QR scan from the redesigned panel.

## 9. Not finished

- Toast notifications (owner decision: Deny and Review only, never Approve; transfer-finished toasts without buttons; tray-balloon fallback) and the tray flyout.
- The software-update panel is a XAML card (`Views/UpdateCard`) bound to `UpdatePanel`, which now holds only the update logic; `LegacyConsole.cs` is gone.
- Before/after screenshots exist for every page in both themes (`docs/evidence/screenshots/console-redesign/before` and `after`).

## 10. Data the agent does not expose (future agent work, with the privacy trade-off)

The console deliberately shows only what the agent reports. Ideas that need agent changes, each with its cost:
- **Per-device activity** (which device downloaded or uploaded what): today the history records fixed kinds, counts and sizes only, no device and no names. Recording a
  device id per event adds a who-did-what trail on the PC. If built: local only, opt-in, no file names by default, bounded retention, clearable.
- **Device type / OS / app version on a device card**: available at pairing from the browser; storing it is mild fingerprinting. Show only what the owner would recognise.
- **Last network location of a device**: not recommended; the relay would have to record addresses.
- **Current Explorer Send-to setting**: the agent can set it but never reports it, so the console offers explicit On/Off and shows the agent's reply. Exposing a boolean in
  status is a safe, small change.
- **Per-transfer device on the Activity page**: same trade-off as per-device activity.

## 11. Owner checklist (Windows 11)

Mica backdrop in both themes; 100/125/150/200 % DPI and a narrow window (nav collapses); Windows high contrast (no crash, text and icons visible); lock/unlock with
"pause while locked" on; tray open/pause/quit; close to tray; second launch brings up the existing window; Ctrl+1..5; Tab focus ring on every page; Narrator reads
the Home headline and a device card; drag a folder from Explorer onto Shared folders; the three folder confirmations, the device removal and Clear history dialogs;
Copy link and Copy as text; scan the QR on Devices with a phone.
