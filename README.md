# Email Triage

A keyboard-driven triage client for **classic Outlook desktop on Windows**. It talks
to your local Outlook store over COM/MAPI - the same `.ost`/`.pst` Outlook already
has open. No cloud API, no app registration, no mail leaving the machine.

Outlook stays the source of truth for mail. This app adds the layer Outlook lacks:
fast filing, a real action list, and snooze.

## What it does

| | |
|---|---|
| **Triage** | Walk the inbox, decide action / no action, file it, move on - without the mouse. |
| **Move** | `k` opens a fuzzy folder search. No match? Create the folder and move in one keystroke. |
| **Snooze** | `g` parks a mail and puts it back in your inbox at a time you pick. |
| **Action list** | Flagged mail gets notes, blockers, and tasks assigned to other people. |
| **Reply** | `r` reply-all, `Shift+R` reply-to-sender, sent from inside the app. |

## Requirements

- Windows 10 or 11
- **Classic Outlook desktop** (2016 / 2019 / 2021 / Microsoft 365), signed in
  - The *new* Outlook will not work. It removed the COM interface this depends on.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) - preinstalled on
  current Windows 11. Without it everything still works except the message preview pane.

## Build and run

```powershell
git clone <this repo>
cd Email_App
dotnet build -c Release
dotnet run --project src\EmailTriage.App
```

Single-file executable:

```powershell
dotnet publish src\EmailTriage.App -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o publish
.\publish\EmailTriage.exe
```

Building from macOS or Linux (compiles only - it cannot run there) needs
`-p:EnableWindowsTargeting=true`.

## Keys

`?` shows this in the app.

### Moving around
| Key | |
|---|---|
| `j` / `↓` | Next message |
| `p` / `↑` | Previous message |
| `Tab` | Switch between Triage and Action items |
| `/` | Filter the list |
| `F5` | Refresh |

### Triage
| Key | |
|---|---|
| `a` | Needs action - flags it and adds it to the action list |
| `n` | No action needed |
| `k` | **Move to folder** - type to search, `Ctrl+Enter` creates and moves |
| `g` | **Come back to this** - presets, or type `tomorrow 9am` / `fri` / `3d` |
| `e` | Archive |
| `u` | Toggle read / unread |
| `Ctrl+Z` | Undo the last move or snooze |

### Replying
| Key | |
|---|---|
| `r` | Reply to everyone |
| `Shift+R` | Reply to the sender only |
| `Ctrl+Enter` | Send |
| `Esc` | Discard |

> These two are the way round you asked for. Note it is the opposite of Gmail and
> Outlook, where the *unshifted* key replies to one person. If it fights your muscle
> memory, swap them in the config file below - no rebuild needed.

### Action items
| Key | |
|---|---|
| `t` | Edit notes |
| `b` | Add a blocker |
| `Shift+A` | Assign someone a task |
| `x` | Mark done |
| `Shift+P` | Cycle priority |
| `o` | Open the original in Outlook |

Every binding lives in `%APPDATA%\EmailTriage\keybindings.json`, written on first run.

## How the pieces work

### Filing (`k`)
The palette searches every mail folder across every open store, scoring matches the
way `fzf` does - so `acinv` finds `Clients\Acme\Invoices`. It also learns: folders you
file into often rise to the top, with the weighting halving every 60 days so old
habits fade. When nothing matches, `Ctrl+Enter` creates the folder you typed
(`Clients\Acme\Q3` creates `Q3` under an existing `Clients\Acme`) and moves the mail
there in the same keystroke.

### Snooze (`g`)
Outlook has no snooze for received mail, so the app implements it: the message moves
to a `Snoozed` folder and a return time is recorded locally. A background loop checks
every 30 seconds and moves it back, marked unread so it reads as new.

**The app must be running for a snooze to fire.** Anything that came due while it was
closed is swept back the moment you next open it. That is the honest trade for not
installing a background service.

### Action items
Flagging a mail does two things: it applies an Outlook category (so the flag is
visible in Outlook itself, not trapped in this app) and creates a local record for the
notes, blockers, and assignments.

Assignments are **local by default**. Nothing is sent when you assign someone. When
you want to actually tell them, the app opens a pre-filled draft in Outlook for you to
review and send yourself.

### Identity
Outlook `EntryID`s change whenever an item moves between stores, which is what breaks
naive Outlook tools. Everything persisted here is keyed by the RFC 5322 `Message-ID`
instead, with the `EntryID` cached only as a fast path. If the cache goes stale - you
moved the mail by hand - the app re-finds it by `Message-ID`.

## Design notes

```
EmailTriage.Core      models, services, SQLite    net8.0          (no Windows deps, fully tested)
EmailTriage.Outlook   COM/MAPI implementation     net8.0-windows
EmailTriage.App       WPF UI                      net8.0-windows
```

**All COM runs on one STA thread.** Outlook's object model is apartment-bound; calling
it from thread-pool threads is the cause of the intermittent `RPC_E_WRONGTHREAD`
failures that make Outlook automation flaky. `StaDispatcher` confines every call to a
single dedicated thread.

**Outlook is reached by late binding**, not a PIA or type library. That means the
solution builds against any Outlook version (and on any OS), and COM references never
have to be version-matched.

**Mail bodies are rendered under a strict CSP** that forbids scripts entirely and, by
default, blocks remote images - in an inbox those are mostly tracking pixels that tell
the sender when you opened their mail. Set `BlockRemoteImages: false` in
`%APPDATA%\EmailTriage\settings.json` to allow them.

**The message list uses a MAPI table**, not per-item property reads, which is roughly
an order of magnitude faster on a large mailbox. The trade is that the list shows no
body-preview line, because a snippet cannot be read cheaply that way.

Local state lives in `%LOCALAPPDATA%\EmailTriage\triage.db` (SQLite). Deleting it
loses your notes and pending snoozes, not your mail.

## Tests

```bash
dotnet test
```

64 tests over the fuzzy matcher, the snooze date parser, folder ranking, the snooze
scheduler (including catch-up after downtime and stale-EntryID recovery), and the
SQLite repositories. The COM layer is not unit-tested - it needs a real Outlook - which
is exactly why it sits behind `IMailStore` and everything else is tested against a fake.

## Known limits

- Classic Outlook only. New Outlook exposes no COM interface.
- Snoozes fire only while the app is running; overdue ones are swept on next launch.
- No body-preview text in the list (see above).
- Single inbox - the default account's. Folder search spans all stores.
