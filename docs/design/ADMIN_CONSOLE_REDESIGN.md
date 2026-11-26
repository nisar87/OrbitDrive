# Operator console redesign

Status: design agreed with the owner 2026-11-25; building in phases. Replaces the single scrolling "Administration" page.

## 1. What was wrong

The first console was one long page in a small window: a block of numbers, a billing form that began with a free-text "reason" box, unlabelled lists with a button on
every row, and no way to find anything. Nothing said what needed attention, accounts were an unsorted list of opaque ids, every action sat in the row, and the
organisation page copied the same style. It worked, but it was built to hold data, not to run a service from.

## 2. Who uses it and what for

One person (the owner), occasionally a trusted second operator. Four jobs, in this order of frequency:

1. **Triage**: "Is anything waiting for me?" Usually a payment to check, sometimes a suspended or abusive account, sometimes a health problem.
2. **Support**: find an account, see its plan, use and devices, fix something (extend, sign out browsers, suspend).
3. **Money**: verify payments, hand out a pass, manage discount codes, set prices and limits.
4. **Oversight**: is the service healthy and what has been done (audit log).

## 3. Principles

- **Answer first.** The first screen says what needs the operator. Everything else is one click away.
- **One place for each thing.** Navigation on the left; the page says where you are and offers one primary action.
- **Tables for lists, drawers for detail, dialogs for consequences.** No button wall in a row. A row opens a detail drawer; risky actions open a dialog that says what
  will happen and asks for a reason.
- **Safe by construction.** Counts only; no file or folder names anywhere. Every change needs a reason and is written to the audit log by the service. Destructive
  actions state their effect and are never the default focus. Reads are free, changes are deliberate.
- **Usable by keyboard and screen reader.** Real tables with headers, labelled controls, focus kept inside dialogs and returned afterwards, visible focus, text that
  scales, no information by colour alone.
- **Quiet visuals, one memorable thing.** The product's existing Windows-style look is kept (same fonts, accent and light/dark tokens). The memorable element is the
  "Needs attention" queue at the top of Overview: each item names the problem and carries its own action.

## 4. Information architecture

```
Console
├─ Overview            needs attention · key numbers · service details (collapsed)
├─ Accounts            table (search, filter, sort) → drawer: Summary · Usage · Devices · Plan and passes · Actions
├─ Organisations       table → drawer: Members · Usage · Activity · Actions
├─ Billing
│   ├─ Orders          waiting / all, verify or reject with a reason
│   ├─ Plans and prices  limits switch, exchange rate, prices and limits (unsaved-changes bar)
│   ├─ Discount codes  table + new code dialog
│   └─ Payment details the text customers see after ordering
├─ Audit log           table: when · who · what · target · reason
└─ Settings            oldest PC version allowed
```

Narrow screens: the left navigation becomes a scrolling row of tabs; the drawer becomes a full-screen panel.

## 5. Wireframes

Overview:

```
┌ Console ───────────────────────────────────────────────────────────────────┐
│ Overview                                   Relay 0.1.35 · up since 09:02  ⟳ │
│ ┌ Needs attention ───────────────────────────────────────────────────────┐ │
│ │ ● 2 orders waiting for a payment check                     [Review]    │ │
│ │ ● Daily relay cost alert is on                              [Open]      │ │
│ │   (when nothing waits: "Nothing is waiting for you.")                  │ │
│ └────────────────────────────────────────────────────────────────────────┘ │
│  Accounts 12        PCs online 3 of 7     Transfers now 1     Relayed today │
│  2 suspended        4 offline             0 failed today      1.4 GB        │
│  ▸ Service details (browsers, downloads, uploads, allowance refusals)       │
└────────────────────────────────────────────────────────────────────────────┘
```

Accounts (master and detail):

```
┌ Accounts ──────────────────────────────────┐ ┌ Account  spRN…c6v1 ───────────┐
│ [search id…]  [All ▾] [Plan ▾]    12 shown │ │ Plus · until 21 Dec  ● active │
│ Account        Plan   PCs Dev  Today  Stat │ │ Summary Usage Devices Plan ⋯  │
│ spRNXp…        Plus    2   3   120 MB  ●   │ │ …                              │
│ BqtvVh…        Free    1   1   0 B     ●   │ │ [Add a pass] [Sign out browsers]│
└────────────────────────────────────────────┘ │ ─ Suspend this account ─       │
                                                └───────────────────────────────┘
```

Action dialog (all risky actions): title naming the action and account, one sentence of consequence, a reason field (required, at least 3 characters), buttons
"Cancel" (default focus) and the action name. Result is announced and added to the audit log.

## 6. Visual system (kept from the product, extended)

- Colour: the product tokens (`--bg --surface --panel --text --muted --line --accent --danger`), plus status colours that work in light and dark: ok (green), waiting
  (amber), problem (red), neutral (grey). Status is always a word plus a colour, never colour alone.
- Type: Segoe UI Variable, as the rest of the product. Sizes: 20 px page titles, 15 px section titles, 14 px body, 12 px helper text. Figures use tabular numerals
  and right alignment. Sentence case everywhere; no all-caps labels.
- Layout: 13 rem navigation rail, content up to 72 rem, 8 px spacing grid, 6 px corner radius (same as buttons), 1 px lines instead of shadows, one drawer shadow.
- Motion: a short slide for the drawer and dialog only; none when the person prefers reduced motion.

## 7. Safety and privacy (unchanged by the redesign)

- The console is offered only to accounts the relay names as operators; every request is re-checked, and changes need a sign-in from the last 15 minutes.
- No file or folder names, paths or contents exist in any console data. Organisation views show counts and settings only (and, for operators, each member's account email).
- **Accounts are named by email (R65, owner decision of 2026-11-26, reversing the earlier "opaque ids only" default).** Every place that lists or names an account shows the email (and the display name, if the sign-in provides one) with a short id: Overview attention list, Accounts table, search and drawer title, Organisations owner and members, Billing orders, Audit log actor and target, and the reason dialogs (which name the email, so the operator knows exactly whom a change affects). An account that has not signed in since this was added shows "Email not known yet (shows after their next sign-in)". The email is account identity only: it comes from the verified sign-in token, is stored in the billing record, is returned only by `/v1/admin/*` (operator list and fresh sign-in re-checked), never appears in a URL or a log, and is erased from the billing record when the account is deleted. The audit log keeps the email as it was at the time, so entries stay readable after deletion. Ordinary accounts and organisation admins never receive another account's email (an organisation still shows the label each member chose). File and folder names, paths and what a member moved are still never held or shown.
- Reasons are required in the interface and by the service; the audit log is read-only.
- The operator code is a separate download; ordinary accounts never receive it (an existing test checks this).

## 8. Build phases

1. Operator console (this work): shell, kit, Overview, Accounts, Organisations, Billing, Audit log, Settings.
2. Customer Organisation page on the same kit.
3. Desktop app design pass (WPF): same principles, reviewed separately.

## 9. Test plan

Unit tests for the table helpers (filter, sort, paging), formatters and the attention rules. End-to-end specs updated to the new navigation with the same
assertions (reason required, suspension takes effect, audit entry written, no private strings on screen, operator code not served to ordinary accounts), plus accessibility
checks in light and dark. A visual review of every page at desktop and phone width.
