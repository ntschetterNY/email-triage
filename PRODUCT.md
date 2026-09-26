# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows-desktop

Native Windows WPF application (.NET 8), not one of web/ios/android/adaptive. Design work targets desktop Windows conventions; the message preview pane is embedded WebView2 HTML under a strict CSP.

## Users

Primary: knowledge workers on **classic Outlook desktop for Windows** whose inbox is their task queue and who want keyboard-driven speed Outlook doesn't offer. The author (a construction-industry PM/engineer) is the founding user, but the product is intended for **wider distribution** — strangers who didn't build it will install and learn it. First-run experience, discoverability of shortcuts, and edge-case behavior are product concerns, not polish.

Users are stuck on classic Outlook (usually by corporate IT policy), often in Microsoft 365 shops. They cannot switch to Superhuman/Gmail-family tools; this app meets them where they are.

## Product Purpose

A keyboard-driven triage layer over the local Outlook store (COM/MAPI). Outlook remains the source of truth for mail; the app adds what Outlook lacks: fast filing with fuzzy folder search, snooze, a real action list, and calendar answering — all without the mouse and without any cloud API or mail leaving the machine.

**Success = inbox-zero speed.** The job it must win at is triaging a full inbox in minutes, hands never leaving the keyboard — Superhuman-class speed on classic Outlook. The action-list/snooze layer serves that job; when trade-offs arise, triage speed wins.

## Positioning

The only Superhuman-style triage client that works on **classic Outlook desktop with zero IT involvement**: no cloud API, no app registration, no admin consent, no mail leaving the machine. It talks to the `.ost`/`.pst` Outlook already has open. Competitors need Graph API access or a different mail provider; this needs only that Outlook is running.

## Operating Context

- Runs beside classic Outlook (2016/2019/2021/M365), signed in, on Windows 10/11. The **new** Outlook is explicitly unsupported (no COM interface).
- Users triage in bursts; keyboard layout follows Superhuman's shortcuts where an equivalent exists, so Superhuman muscle memory transfers.
- Flags surface as Outlook categories so state is visible in Outlook itself, never trapped in the app.
- Assignments are local by default; telling someone opens a pre-filled Outlook draft the user sends themselves.
- Snoozes fire only while the app runs; overdue ones are swept on next launch (deliberate trade for no background service).

## Capabilities and Constraints

- Stack: `EmailTriage.Core` (net8.0, no Windows deps, fully tested) / `EmailTriage.Outlook` (COM/MAPI, late-bound, single STA thread) / `EmailTriage.App` (WPF + WebView2). Local state in SQLite (`%LOCALAPPDATA%\EmailTriage\triage.db`).
- Persistence is keyed by RFC 5322 `Message-ID`, never `EntryID` (which changes across stores).
- Message list reads a MAPI table for speed; consequence: **no body-preview snippet in the list**. Durable trade, not an oversight.
- Remote images blocked by default (tracking pixels); scripts always forbidden in the preview CSP.
- Single inbox (default account), default calendar only; folder search spans all stores.
- Keybindings user-editable in `%APPDATA%\EmailTriage\keybindings.json`, with versioned migration.
- Tabs: Triage, Action items, Calendar. Fuzzy folder palette (`v`), snooze with natural-language dates (`h`), compose/reply with @-mentions, meeting join countdown in the top bar.

## Brand Commitments

- **Name undecided.** "Email Triage" is a working title; "orca" is the repo's parent folder, not a confirmed name. No binding palette, logo, or visual identity exists yet. Future naming/identity work is open.
- Confirmed interaction commitment: keyboard-first, Superhuman-compatible shortcut layout where equivalents exist.
- Voice in existing copy/README: plain, direct, honest about trade-offs ("That is the honest trade for not installing a background service"). Treat as the product's voice unless changed.

## Evidence on Hand

- Working implementation of everything in README.md; that file is the authoritative feature record.
- Test suite over Core logic (fuzzy matcher, date parsers, snooze scheduler, calendar math, repositories); COM layer untested by design, isolated behind `IMailStore`.
- No testimonials, customers, benchmarks, or pricing exist. **Do not fabricate any.**

## Product Principles

1. **Speed is the product.** Every interaction is judged by whether it keeps a triage burst moving; anything that forces the mouse or a wait is a defect.
2. **Outlook stays the source of truth.** State the app creates must be visible or recoverable in Outlook itself; the app must survive the user acting in Outlook directly.
3. **Nothing leaves the machine.** No cloud API, no telemetry-by-default, no mail exfiltration — this is the adoption story for locked-down corporate users.
4. **Honest trade-offs, stated plainly.** When a limitation is the price of a design choice (no preview snippets, snooze needs the app running), say so rather than hide it.
5. **Built for strangers.** Wider distribution is the goal: shortcuts must be discoverable (`?` help), first-run must work on a machine that isn't the author's, and failure modes (Outlook closed, new Outlook, missing WebView2) must explain themselves.

## Accessibility & Inclusion

No product-specific standard established yet (open decision for distribution). The keyboard-first design is inherently strong for motor accessibility; screen-reader behavior of the WPF list and WebView2 preview has not been evaluated.
