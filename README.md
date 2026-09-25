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
| **Move** | `v` opens a fuzzy folder search. No match? Create the folder and move in one keystroke. |
| **Snooze** | `h` parks a mail and puts it back in your inbox at a time you pick. |
| **Action list** | Flagged mail gets notes, blockers, and tasks assigned to other people. |
| **Write** | `Enter` reply-all, `r` reply-to-sender, `Ctrl+N` a new message, all sent from inside the app. |
| **Calendar** | Invitations show when they are and whether you're free; `y` answers them. `s` puts a mail on your calendar. A Calendar tab lists what's coming, and the top bar counts down to your next meeting. |
| **AI** (optional) | `Ctrl+G` has Claude draft the reply from the conversation - or from notes you type first. `Ctrl+/` asks your inbox a question in plain language. Runs through your own Claude Code sign-in; see below for what leaves the machine. |

## Requirements

- Windows 10 or 11
- **Classic Outlook desktop** (2016 / 2019 / 2021 / Microsoft 365), signed in
  - The *new* Outlook will not work. It removed the COM interface this depends on.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) - preinstalled on
  current Windows 11. Without it everything still works except the message preview pane.
- [Claude Code](https://claude.com/claude-code), signed in - **only for the AI commands**
  (`Ctrl+G`, `Ctrl+/`). Everything else works without it.

## Install

1. Download `EmailTriage-win-x64.zip` from the
   [latest release](https://github.com/ntschetterNY/email-triage/releases/latest).
2. Unzip it somewhere you can write to, e.g. `%LOCALAPPDATA%\Programs\EmailTriage`.
   Not `Program Files` - the app can't update itself there.
3. Run `EmailTriage.exe`.

### Updates

Each time it opens, the app checks for a newer release. If there is one, it downloads it,
swaps the files and restarts itself. That takes a few seconds and needs no admin rights. If
it's offline, or GitHub can't be reached within 5 seconds, it just opens the version you
have. Set `CheckForUpdates: false` in `%APPDATA%\EmailTriage\settings.json` to turn this
off. When an update fails, the reason goes to `%LOCALAPPDATA%\EmailTriage\error.log`.

Every push to `main` that changes code is tested, built and published as release
`1.0.<run>` by `.github/workflows/release.yml`. Only those builds update themselves. A copy
you build locally never replaces itself.

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

`?` shows this in the app. The layout follows
[Superhuman](https://superhuman.com)'s shortcuts wherever the app has the same command;
the ones Superhuman has no equivalent for (action / no action, the action board) keep
their own letters.

### Moving around
| Key | |
|---|---|
| `j` / `↓` | Next message |
| `k` / `↑` | Previous message |
| `Home` / `Ctrl+↑`, `End` / `Ctrl+↓` | First / last message |
| `Tab` / `Shift+Tab` | Next / previous tab: Triage, Action items, Calendar |
| `/` | Filter the list |
| `Ctrl+/` | **Ask your inbox** - AI search in plain language, `Esc` shows everything again |
| `F5` | Refresh |

### Triage
| Key | |
|---|---|
| `a` | Needs action - flags it and adds it to the action list |
| `n` | No action needed |
| `v` | **Move to folder** - type to search, `Ctrl+Enter` creates and moves |
| `h` | **Remind me** (snooze) - presets, or type `tomorrow 9am` / `fri` / `3d` |
| `e` | Archive |
| `u` | Toggle read / unread |
| `Ctrl+O` | Open an attachment |
| `z` / `Ctrl+Z` | Undo the last move or snooze |

### Replying
| Key | |
|---|---|
| `Ctrl+N` | New message, from any tab (or the **New email** button). `Ctrl+Shift+S` jumps to its subject |
| `Enter` | Reply to everyone |
| `r` | Reply to the sender only |
| `f` | Forward |
| `Ctrl+G` | **AI draft** - Claude writes the reply from the conversation, or expands notes you typed first. Nothing sends itself |
| `Ctrl+Enter` | Send |
| `Ctrl+Shift+Enter` | Send & mark done - archives the conversation |
| `Ctrl+Shift+L` | Send later |
| `Ctrl+Shift+O` / `C` / `B` / `M` | Jump to To / Cc / Bcc / the message |
| `Esc` / `Ctrl+Shift+,` | Discard |
| `@` | Mention someone in the message - pick with `↑↓` `Enter`/`Tab`; they are added to To if not already on it |


### Action items
| Key | |
|---|---|
| `t` | Edit notes |
| `b` | Add a blocker |
| `Shift+A` | Assign someone a task |
| `x` | Mark done |
| `Shift+P` | Cycle priority |
| `o` | Open the original in Outlook |
| `#` / `Delete` | Delete the card |

### Calendar
| Key | |
|---|---|
| `y` | Answer an invitation: `Enter` accepts, `↓` for maybe or decline. Type first to send a note with it. On a cancellation, takes it off your calendar |
| `s` | Put the mail (or the card, on the board) on your calendar: `Enter` blocks the time for you, `Ctrl+Enter` invites the people on the thread |
| `Ctrl+J` | Join the meeting on now or about to start, from any tab |
| `Enter` | On the Calendar tab: join the meeting (Teams, Zoom, Meet, Webex), or open it in Outlook if it has no link |
| `o` | On the Calendar tab: open the meeting in Outlook |

Every binding lives in `%APPDATA%\EmailTriage\keybindings.json`, written on first run.
A file from before the Superhuman layout is upgraded on the next start: its copies of
the old defaults are replaced, any keys you changed yourself are kept, and the original
is saved beside it as `keybindings.v1.json`.

## How the pieces work

### Filing (`v`)
The palette searches every mail folder across every open store, scoring matches the
way `fzf` does - so `acinv` finds `Clients\Acme\Invoices`. It also learns: folders you
file into often rise to the top, with the weighting halving every 60 days so old
habits fade. When nothing matches, `Ctrl+Enter` creates the folder you typed
(`Clients\Acme\Q3` creates `Q3` under an existing `Clients\Acme`) and moves the mail
there in the same keystroke.

### Snooze (`h`)
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

### Calendar
Meeting invitations, cancellations and responses now show in the triage list, tagged
`INVITE`, `CANCELLED`, `ACCEPTED` and so on. Before, the list left them out, so they sat
unseen in the Outlook Inbox. Opening an invitation shows a card with the date and time,
your current answer, and whether it clashes with anything already on your calendar.
Outlook pencils an invitation in as soon as it arrives, so that entry itself doesn't
count as a clash.

`y` answers. The answer goes to the organizer (with your note, if you typed one) and the
invitation is archived. Declining takes the meeting off your calendar, as Outlook does.
An answer is an email, so `z` can't unsend it; it only brings the invitation back. On
the Calendar tab, `y` answers the meeting directly. A recurring meeting is answered for
the whole series.

`s` puts a mail on your calendar. Type a time the way you would for snooze, with an
optional length or range: `tomorrow 2pm 1h`, `fri 10-11:30am`. Or type just a length
(`45m`) to be offered free slots in your working day, which runs from `MorningHour` to
`EveningHour` in settings. `Enter` blocks the time as an appointment with the email
attached, and `z` removes it. `Ctrl+Enter` makes it a meeting with everyone on the
thread instead. That opens in Outlook for you to check and send, because an invitation
goes to other people.

The top bar shows the meeting on now or next, with a countdown. It turns amber five
minutes before a meeting. Click it to see the meeting in the Calendar tab. The Calendar
tab lists the next `CalendarDaysAhead` days (14 by default), with attendees, their
answers and the invitation text. Settings also cover `DefaultEventMinutes` (30),
`BlockReminderMinutes` (5), and `JoinLeadMinutes` (10), which is how close a meeting
must be for `Ctrl+J` to join it rather than the one you're in.

### AI drafting and search (`Ctrl+G`, `Ctrl+/`)

Both commands run the [Claude Code](https://claude.com/claude-code) CLI as a child
process, so they use whatever sign-in you already have - a Claude subscription
login works; no API key is stored or needed. If `claude` isn't installed or
signed in, the status line says so and nothing else changes.

**`Ctrl+G` drafts a reply.** From the list it opens a reply-all and writes a
draft from the whole conversation. In the composer it works from what's already
in the box: type rough notes - `say yes, ask for the revised SOV by Friday` -
and `Ctrl+G` turns them into the full message. The draft only ever lands in the
composer for you to edit; sending stays your keystroke, and `Ctrl+G` again
redoes it.

**`Ctrl+/` asks your inbox a question.** "what am I still waiting on from the
architect?", "anything about the November invoice?" - Claude reads the list
(subjects, senders, dates, and the text of conversations you've already opened)
and filters it to the matches, best first. `Esc` shows everything again. Because
the list itself carries no body text (see below), unopened conversations match
on their subject and sender only.

**Follow-ups chase themselves onto the board.** When a card's blocker or
hand-off has sat unchanged for `FollowUpAfterDays` (5 by default, 0 turns it
off), the card grows a `follow up · waiting 8d` chip and the status line counts
what's due. The flagging is timestamp arithmetic in the local database - free,
no AI involved. Pressing `c` on the card is what brings Claude in: a hand-off
with an email address gets its own chase mail (opened in Outlook for review, as
before, but now written by Claude); anything else gets a follow-up reply
drafted into the composer on the task's own conversation, saying what's owed
and for how long. Drafting a chase restarts that card's clock.

**Drafts come out in your voice.** The first time you ask for a draft, the app
reads your recent sent mail (quoted history stripped), has Claude distil how
you write - greeting, sign-off, length, phrasing - and caches the result at
`%APPDATA%\EmailTriage\writing-style.md`. Every later draft carries that guide.
The file is plain text on purpose: open it and edit it to tune what the drafts
sound like, or delete it to relearn from scratch.

**What leaves the machine, stated plainly:** this app's core promise is that no
mail leaves the machine, and these two commands are the deliberate, opt-in
exception. Nothing is sent anywhere until you press `Ctrl+G` or `Ctrl+/`; when
you do, the conversation being answered (or the list being searched) goes to
Anthropic through your own Claude account, under that account's data terms. If
that trade isn't acceptable in your shop, don't install Claude Code - every
other feature is unaffected.

Settings: `AiModel` (default `claude-opus-5`; `sonnet` answers faster) sets the
model for everything, and each job can override it - `AiDraftModel`,
`AiFollowUpModel`, `AiSearchModel`, `AiStyleModel` - so "Sonnet for replies,
Opus for follow-ups" is two lines in settings.json. Also `FollowUpAfterDays`,
`ClaudeCliPath` (set it if `claude` isn't on PATH), and `AiTimeoutSeconds`.

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

Tests over the fuzzy matcher, the snooze date parser, folder ranking, the snooze
scheduler (including catch-up after downtime and stale-EntryID recovery), the SQLite
repositories, the calendar logic (reading typed times and lengths, clashes, free
slots, and finding join links), and the AI layer: the prompts built for drafting
and search, and the tolerant parsing of what comes back - the model process itself
sits behind `IAiAssistant` and is faked. The COM layer is not unit-tested - it needs a real Outlook - which
is exactly why it sits behind `IMailStore` and everything else is tested against a fake.

## Known limits

- Classic Outlook only. New Outlook exposes no COM interface.
- Snoozes fire only while the app is running; overdue ones are swept on next launch.
- No body-preview text in the list (see above).
- Single inbox - the default account's. Folder search spans all stores.
- Only the default calendar. Shared and secondary calendars aren't read, so they don't
  count toward clashes or free slots.
