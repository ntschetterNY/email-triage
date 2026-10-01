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
| **Folders** | Name folders your way (`Elara - Field Reports - Rimkus`) and they're kept nested (`Elara › Field Reports › Rimkus`). Settings (`Ctrl+,`) sets the scheme and tidies existing folders to match. `Shift+V` opens any folder in Outlook. |
| **Snooze** | `h` parks a mail and puts it back in your inbox at a time you pick. |
| **Action list** | Flagged mail gets notes, blockers, and tasks assigned to other people. Set a follow-up day as you send, and on that day the mail turns up in the board's Follow up column, ready for Claude to draft the nudge. |
| **Write** | `Enter` reply-all, `r` reply-to-sender, `c` a new message, all sent from inside the app. |
| **Calendar** | Invitations show when they are and whether you're free; `y` answers them. `s` puts a mail on your calendar. The Calendar tab shows your day, work week, week or month (keys `1`-`5`), and the top bar counts down to your next meeting. |
| **AI** (optional) | `Ctrl+G` has Claude draft the reply from the conversation - or from notes you type first - offering times you're free when it's about meeting. `Ctrl+/` asks your inbox a question in plain language. Runs through your own Claude Code sign-in; see below for what leaves the machine. |
| **Feedback** | The **Lavish** button (top right, `Ctrl+Shift+L`) lets you click any part of the app and say what should change. Each note becomes a GitHub issue, and the Lavish panel follows it through branch, pull request, merge and release. |

## Requirements

- Windows 10 or 11
- **Classic Outlook desktop** (2016 / 2019 / 2021 / Microsoft 365), signed in
  - The *new* Outlook will not work. It removed the COM interface this depends on.
- Nothing else to install. The .NET runtime is inside `EmailTriage.exe`, so it runs on a
  machine where you can't install software. No admin rights are needed at any point.
- The message preview pane uses the WebView2 runtime that Windows 11 and Microsoft 365 already
  include. Without it everything still works except the preview; see
  [No WebView2 runtime](#no-webview2-runtime) if you can't install it either.
- [Claude Code](https://claude.com/claude-code), signed in - **only for the AI commands**
  (`Ctrl+G`, `Ctrl+/`). Everything else works without it.

## Install

1. Download `EmailTriage-win-x64.zip` from the
   [latest release](https://github.com/ntschetterNY/email-triage/releases/latest).
2. Unzip it somewhere you can write to, e.g. `%LOCALAPPDATA%\Programs\EmailTriage`.
   Not `Program Files` - the app can't update itself there.
3. Run `EmailTriage.exe`. Keep the DLLs next to it; they are part of the app.

The zip is about 65 MB because it carries its own copy of .NET. That is the trade for
needing nothing installed.

### No WebView2 runtime

Nearly every Windows 10/11 machine already has it (Edge, Windows 11 and Microsoft 365 all
ship it), and the app uses that copy. If yours doesn't and IT won't install the
[Evergreen runtime](https://developer.microsoft.com/microsoft-edge/webview2/), drop a
[Fixed Version](https://developer.microsoft.com/microsoft-edge/webview2/#download) copy
(x64; it's a `.cab` you expand with `expand -F:* file.cab .`) into a folder named
`WebView2Runtime` next to `EmailTriage.exe`, so that
`WebView2Runtime\msedgewebview2.exe` exists. The app uses it automatically whenever the
system runtime is missing. It isn't in the release zip because it adds ~250 MB and, unlike
the system copy, isn't kept patched by Windows Update.

### Updates

Each time it opens, the app checks for a newer release. If there is one, it downloads it
(about 65 MB), swaps the files and restarts itself. That takes a few seconds on an office
connection and needs no admin rights. If it's offline, or GitHub can't be reached within 5
seconds, it just opens the version you have. Set `CheckForUpdates: false` in `%APPDATA%\EmailTriage\settings.json` to turn this
off. When an update fails, the reason goes to `%LOCALAPPDATA%\EmailTriage\error.log`.

Every push to `main` that changes code is tested, built and published as release
`1.0.<run>` by `.github/workflows/release.yml`. A copy you `dotnet publish` yourself updates
the same way: it is version 1.0.0, so the first time it opens it replaces itself with the
latest release. To run a local publish as-is (say, to try a change before it is merged),
start it with `EmailTriage.exe --skip-update`, or publish it with `-p:NoUpdateRepo=true` so it
never checks. `dotnet build` and `dotnet run` builds never update.

## Build and run

```powershell
git clone https://github.com/ntschetterNY/email-triage.git
cd email-triage
dotnet build -c Release
dotnet run --project src\EmailTriage.App
```

Self-contained executable, the same thing the release workflow ships (the runtime
identifier, single-file and self-contained settings live in `EmailTriage.App.csproj` and
apply only to `publish`):

```powershell
dotnet publish src\EmailTriage.App -c Release -o publish
.\publish\EmailTriage.exe
```

`dotnet build` and `dotnet run` stay framework-dependent, so a developer machine needs the
.NET 8 SDK; end users don't.

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
| `Shift+↓` / `Shift+↑` | Select several - `e`, `v`, `h`, `a` and `n` then act on all of them; `Esc` clears |
| `Home` / `Ctrl+↑`, `End` / `Ctrl+↓` | First / last message |
| `Tab` / `Shift+Tab` | Next / previous tab: Triage, Action items, Calendar |
| `/` | Filter the list. While typing, `↑`/`↓` pick a result, `→` at the end of the query expands its conversation and `←` at the start folds it back |
| `Ctrl+/` | **Ask your inbox** - AI search in plain language, `Esc` shows everything again |
| `F5` | Refresh |
| `Ctrl+,` | Settings (also the **⚙ Settings** button in the top bar) |
| `Ctrl+Shift+L` | **Lavish** - comment on any part of the app (also the **Lavish** button, top right); `Esc` finishes |

### Triage
| Key | |
|---|---|
| `a` | **Needs action** - asks what has to happen, who has the ball, when it's due and when to chase, then flags it. Everything is optional: `Enter` saves, `Shift+Enter` just flags, `Esc` cancels |
| `n` | No action needed |
| `v` | **Move to folder** - type to search, `Ctrl+Enter` creates and moves |
| `Shift+V` | **Open a folder in Outlook** - type to search, `Enter` shows it in Outlook's window (any tab) |
| `h` | **Remind me** (snooze) - presets, or type `tomorrow 9am` / `fri` / `3d` |
| `e` | Archive |
| `u` | Toggle read / unread |
| `Ctrl+O` | Open an attachment |
| `Ctrl+P` | **Print** - the whole conversation, every message open (also the **Print** button in the reading pane) |
| `z` / `Ctrl+Z` | Undo the last move or snooze |

### Replying
| Key | |
|---|---|
| `c` | New message, from any tab (or the **New email** button). `Ctrl+Shift+S` jumps to its subject |
| `Enter` | Reply to everyone |
| `r` | Reply to the sender only |
| `f` | Forward |
| `Ctrl+G` | **AI draft** - Claude writes the reply from the conversation, or expands notes you typed first. Nothing sends itself |
| `Ctrl+Enter` | Send |
| `Ctrl+Shift+Enter` | Send & mark done - archives the conversation and finishes its card on the board |
| `Ctrl+Shift+L` | Send later |
| `Ctrl+Shift+F` | **Follow-up** - who owes what, by when; see below |
| `Ctrl+Shift+T` | Follow-up: toggle tracking it as a task |
| `Ctrl+Shift+O` / `C` / `B` / `M` | Jump to To / Cc / Bcc / the message |
| `Esc` / `Ctrl+Shift+,` | Discard |
| `@` | Mention someone in the message - pick with `↑↓` `Enter`/`Tab`; they are added to To if not already on it |


### Action items
| Key | |
|---|---|
| `t` | Edit notes |
| `b` | Add a blocker |
| `Shift+A` | Assign someone a task |
| `x` | Mark done - the card leaves the board; `z` puts it back. In the done log, reopens it |
| `Shift+D` | Done log - everything you have finished, newest first |
| `Shift+R` | **Review stale cards** - the ones nobody has touched for 30 days, one at a time: `x` done, `#` drop, `Enter` keep, `d` give it a date |
| `Shift+P` | Cycle priority |
| `o` | Open the original in Outlook |
| `#` / `Delete` | Delete the card |

### Calendar
| Key | |
|---|---|
| `y` | Answer an invitation: `Enter` accepts, `↓` for maybe or decline. Type first to send a note with it. On a cancellation, takes it off your calendar |
| `s` | Put the mail (or the card, on the board) on your calendar: `Enter` blocks the time for you, `Ctrl+Enter` invites the people on the thread |
| `Shift+S` | Reply with a meeting to everyone on the thread. In the palette, `Ctrl+T` turns Teams on or off, `Ctrl+D` makes it all day, `Ctrl+R` cycles one time / weekly / fortnightly / monthly / daily / weekdays, `Ctrl+B` cycles how it shows (busy, tentative, free, out of office, working elsewhere). `Enter` opens it in Outlook to send |
| `Ctrl+J` | Join the meeting on now or about to start, from any tab |
| `Enter` | On the Calendar tab: join the meeting (Teams, Zoom, Meet, Webex), or open it in Outlook if it has no link |
| `o` | On the Calendar tab: open the meeting in Outlook |
| `s` / `n` | On the Calendar tab: new entry - type a title and a time together (`Site walk tomorrow 2pm 1h`). Also the `+ New` button |
| `1` `2` `3` `4` `5` | On the Calendar tab: Day, Work week, Week, Month, Agenda |
| `←` `→` / `Home` | On the Calendar tab: previous / next day, week or month / back to today |
| `j` / `k` | On the Calendar tab: next / previous meeting. In the month, `Enter` opens the day |

Every binding lives in `%APPDATA%\EmailTriage\keybindings.json`, written on first run.
A file from before the Superhuman layout is upgraded on the next start: its copies of
the old defaults are replaced, any keys you changed yourself are kept, and the original
is saved beside it as `keybindings.v1.json`.

## How the pieces work

### Filing (`v`)
The palette searches every mail folder across every open store, scoring matches the
way `fzf` does - so `acinv` finds `Clients\Acme\Invoices`. It also learns: folders you
file into often rise to the top, with the weighting halving every 60 days so old
habits fade. When the text names a folder outright - its letters and digits appear in
that folder's own name, so `1940 Jer` matches `1940 Jerome` but a fuzzy `acmeinv` does
not - the palette lists that folder's subfolders indented beneath it, so you can see
what is already there before filing or creating another. When nothing matches,
`Ctrl+Enter` creates the folder you typed
(`Clients\Acme\Q3` creates `Q3` under an existing `Clients\Acme`) and moves the mail
there in the same keystroke.

### Folder structure (Settings, `Ctrl+,`)
Many people name folders with the whole hierarchy in the name -
`Elara - Field Reports - Rimkus`. The Settings page takes that naming scheme and keeps
the folders nested instead, as `Elara\Field Reports\Rimkus`:

- **How your folders are named** - each part in braces, e.g.
  `{Project} - {Type} - {Company}`. Whatever sits between the parts (` - `) is what splits
  a name. A name may stop early (`Elara - Field Reports`); a name with one part is an
  ordinary folder and is left alone.
- **How they nest** - one level per `\`, e.g. `{Project}\{Type}\{Company}`. Reorder the
  parts, drop one, or add a fixed folder: `Projects\{Project}\{Type}`.
- **Build the tree under** - a folder from the top of the mailbox, such as `Inbox`. Empty
  nests each folder where it already is, and puts new ones beside the Inbox.

A **Try a name** box shows where any name would go before anything is saved.

With the scheme set, typing `Elara - Field Reports - Rimkus` in the move palette finds
`Elara\Field Reports\Rimkus`, and `Ctrl+Enter` creates the missing levels and files the
mail there. (Untick the option in Settings to create flat folders instead.)

**Organize existing folders** lists every folder named in the scheme that isn't nested
yet and where it would go. Untick any you want left alone, then **Organize**. Each folder
moves with its mail and subfolders. Where the nested folder already exists, the contents
are merged into it and the emptied original goes to Deleted Items. Anything that won't
move stays in the original, and the list says so. Folders under Deleted Items, Sent Items,
Drafts, Outbox and Junk are never touched.

The scheme is saved in `settings.json` as `FolderNamePattern`, `FolderLayout`,
`FolderHome` and `NestNewFolders`.

### Snooze (`h`)
Outlook has no snooze for received mail, so the app implements it: the message moves
to a `Snoozed` folder and a return time is recorded locally. A background loop checks
every 30 seconds and moves it back, marked unread so it reads as new.

**The app must be running for a snooze to fire.** Anything that came due while it was
closed is swept back the moment you next open it. That is the honest trade for not
installing a background service.

### Print (`Ctrl+P`)
The **Print** button above the reading pane (or `Ctrl+P`) prints the selected conversation:
the subject on top, then every message, newest first, with From, Sent, To, Cc and
attachment names, on white paper. Windows' print dialog picks the printer, copies and
orientation; choose **Microsoft Print to PDF** there to still get a PDF. Conversations longer than `ThreadMessageLimit`
note the older messages left out. Remote images follow `BlockRemoteImages`, as on screen.

### Action items
Flagging a mail does two things: it applies an Outlook category (so the flag is
visible in Outlook itself, not trapped in this app) and creates a local record for the
notes, blockers, and assignments.

`a` asks first, while the mail is still in front of you: **What** needs to happen
(it starts as the subject, minus the RE:s), **Who** has the ball, when it is **Due**
and when to **Follow up**. `Tab` moves through them and `Enter` saves from any of
them, so `a` `Enter` is the plain flag it always was; `Shift+Enter` skips the
questions outright. Name someone and the card lands in **Waiting** on them, with the
follow-up day (a couple of days before it is due unless you type one - `FollowUpBeforeDueDays`
in settings) putting it in the **Follow up** column when the day comes. A first name
is enough: people on the thread come first, then anyone you have waited on before,
then your contacts. `Ctrl+B` makes the wait a blocker rather than a hand-off,
`Ctrl+P` cycles the priority, `Ctrl+N` jumps to the notes box, and `Ctrl+M` opens the
usual Outlook draft to tell them once it is saved. Flag a mail that is already on
the board and the popup edits its card. Several selected conversations get the same
answers. Set `AskDetailsOnFlag` to `false` in settings to have `a` flag at once.

Give the card a date and the mail leaves your inbox until it needs you again: it is
snoozed (see above) until the follow-up day, or the due date if that comes first, and
the popup's last line says when it will be back. A reply sent with a follow-up does
the same with its conversation, unless you sent it with mark done. One `z` puts both
the mail and the card back. Set `SnoozeUntilActionDate` to `false` to keep dated
mail in the inbox.

Assignments are **local by default**. Nothing is sent when you assign someone. When
you want to actually tell them, the app opens a pre-filled draft in Outlook for you to
review and send yourself.

### Follow-ups from the reply box (`Ctrl+Shift+F`)
`Ctrl+Shift+F` (or the **+ Follow up** button) opens a row under the message:
**Follow up** (a date: `fri`, `3d`, `14 oct`), **Who** and **What**. `Tab` moves
through them. Leave the date empty, or press `Esc`, and nothing happens.

- **Who** is whoever the message goes to first, and follows the To line as you
  edit it until you type a name of your own. A first name is enough: it
  matches people on the message first, then your contacts.
- **What** is optional ("send the revised drawings").
- *Add a follow-up line to the email* (on by default) puts
  `Follow-up: Sam Lee - send the revised drawings by Friday 3 Oct` under your text.
- *Track it on the board* (on by default, `Ctrl+Shift+T`) puts the mail on the
  board in **Waiting**, with a hand-off to that person dated that day. For a
  reply it is the mail you answered; for a brand-new message it is your sent
  copy, which the card picks up once Outlook has filed it in Sent Items.

A finished card (`x`) leaves the board the moment you press the key, so the columns
only ever hold what still needs a hand. Its open blockers and hand-offs close with
it. A strip under the board counts what you finished today; `Shift+D` opens the
full log, newest first, where `x` reopens one and `o` opens its mail. `z` on the
board puts the last finished card back exactly as it was.

A card nobody has touched for `StaleAfterDays` (30) - no stage move, note, wait or
chase - gets a `stale · 34d` chip, sinks to the bottom of its column, and counts in
the tab title. `Shift+R` walks them longest-idle first, each with its email beneath:
`x` it got done somewhere else, `#` it never mattered, `Enter` it is still live
(which restarts its clock), `d` give it a real due date. `Esc` stops. A five-minute
Friday habit instead of a column you scroll past every day.

On the day, the card moves to the board's **Follow up** column (the tab reads
`Action items · 2 to follow up`). Go through them there: `Shift+C` opens a reply
in the same conversation with the person on it and Claude's nudge drafted - edit
it and send, and the card goes back to Waiting. If they've already come back to
you, `w` clears the wait instead. Nothing sends by itself, and a draft you
discard leaves the card where it is. A blocker or `Shift+A` hand-off given a
date lands in Follow up on that day too, and chases the usual way.

### Housekeeping
Nothing you have finished is shown for ever, so it is not kept for ever either. On
launch, once everything is on screen, the app removes finished cards older than
`DoneRetentionDays` (90) from the done log, snoozes that came back and scheduled
sends that went more than `SnoozeHistoryDays` / `ScheduledSendHistoryDays` (30) ago,
and compacts the database when enough went. Anything open, pending or on the board
is never touched. Set a window to `0` in `settings.json` to keep that kind for good.

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
`EveningHour` in settings. On the Calendar tab, `s` (or `n`, or `+ New`) adds an entry
of your own: type the title and the time in one go, either way round - `Site walk
tomorrow 2pm 1h`, `fri 10-11am budget review`, or `Focus 2h` to pick a free slot. Tentative entries don't stop a slot being offered: they
are holds you can book over, so the slot says `over a HOLD` and names it, and an
invitation card says "Free apart from a HOLD" rather than calling it a clash. `Enter` blocks the time as an appointment with the email
attached, and `z` removes it. `Ctrl+Enter` makes it a meeting with everyone on the
thread instead. That opens in Outlook for you to check and send, because an invitation
goes to other people.

`Shift+S` replies with a meeting, as Outlook's Reply with Meeting does: everyone on the
thread is invited, and the mail's text is quoted into the invitation. Pick a time the
same way as `s`. The line under the subject shows the switches, and each has its own key:
`Ctrl+T` for a Teams meeting (on by default; `TeamsByDefault` in settings), `Ctrl+D`
for all day (the list then offers days rather than times, and it shows you as free, as
Outlook does), `Ctrl+R` to make it a series, and `Ctrl+B` for how the time shows on
your calendar. A series has no end date; set one in Outlook before sending if it needs
one. `Enter` opens the invitation in Outlook to check and send.

Outlook has no way for another program to add a Teams meeting, so the app presses the
Teams Meeting button on the invitation for you. That needs the button to be there and
named in English. If it can't find it, the status line says so and you press it
yourself. If your Outlook already adds Teams to every new meeting, set `TeamsByDefault`
to false: there's no need for the app to press the button as well.

The top bar shows the meeting on now or next, with a countdown. It turns amber five
minutes before a meeting. Click it for the meeting's card: a Join button, who is coming,
and the invitation text.

On your own meetings and appointments the card has **Edit** (or `E`): change the title,
date and times, all day, show as, location, required and optional attendees, and the
notes, and tick **Add a Teams meeting** for one that has no link. `Ctrl+Enter` saves; `Esc`
goes back to the card. A meeting with attendees goes out to them as an update straight
from the app (the button says **Send update**, or **Send invitation** when you first add
people to an appointment). Names are looked up by Outlook, as in its own To line. For a
recurring meeting the edit changes that day only. Adding Teams still needs Outlook's
Teams Meeting button, so its window opens for a few seconds while the app presses it,
waits for the link, then sends or saves and closes it; if the button or the link does not
turn up, the meeting is left open there for you to finish. Notes are saved as plain text,
so only rewrite them when you mean to: an untouched body is never written back.
Someone else's meeting can't be edited - its changes come from the organizer.

The Calendar tab has five views. `1` Day, `2` Work week and `3` Week are a time grid:
overlapping meetings sit side by side, time outside your working hours is shaded, and a
red line marks now. `4` Month shows six weeks, a few meetings a day, and a click on a day
opens it in the Day view. `5` Agenda lists the next `CalendarDaysAhead` days (14 by
default). The arrow keys step a day, week or month, and `Home` comes back to today.
An unanswered invitation has a dashed amber outline, a tentative one is striped, and a
declined one is struck through. The pane on the right shows the selected meeting's
attendees, their answers and the invitation text. Click a meeting that's already
selected, or double-click one, to expand it into a card with the whole subject, its
Join button, attendees and invitation text. The tab opens on the view you used
last, or `CalendarView` (`WorkWeek`) the first time. Settings also cover `DefaultEventMinutes` (30),
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

**Drafts know when you're free.** Every draft is given your free time for the
next `AvailabilityWorkingDays` working days (10), starting tomorrow: gaps between
`WorkdayStartHour` and `WorkdayEndHour` (07:00-16:00), right up to your meetings -
no gap is kept either side. Busy and out-of-office time counts as taken. Tentative
entries (shown as tentative, or answered "maybe") are holds: that time can be booked
over, so it is offered, but it is marked `HOLD` and Claude prefers clear time first. When the email is about meeting - someone asks "when works?", or
your notes say "offer a few times for an hour's walkthrough" - Claude offers
`ProposedSlotCount` (3) times of the right length, on different days where it can.
If they already proposed times, it says which of those suit you instead. Lunch
(`LunchStartHour` to `LunchEndHour`, 12-1; set them equal to turn this off) is only
offered when nothing else fits. For any other email, it ignores your free time. If Outlook
can't give up the calendar, the draft goes ahead without it, Claude is told not to
suggest times, and the status line says so.

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
no AI involved. Pressing `Shift+C` on the card is what brings Claude in: a hand-off
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
Anthropic through your own Claude account, under that account's data terms. A draft
also carries your free time windows for the next two working weeks - times only,
never what your meetings are or who is in them. If
that trade isn't acceptable in your shop, don't install Claude Code - every
other feature is unaffected.

Settings: `AiModel` (default `claude-opus-5`; `sonnet` answers faster) sets the
model for everything, and each job can override it - `AiDraftModel`,
`AiFollowUpModel`, `AiSearchModel`, `AiStyleModel` - so "Sonnet for replies,
Opus for search" is two lines in settings.json. Follow-up chases start on
`sonnet`; pick another model for them on the Settings page (`Ctrl+,`). Also `FollowUpAfterDays`,
`ClaudeCliPath` (set it if `claude` isn't on PATH), and `AiTimeoutSeconds`.

**Your login, not API credits.** Claude Code prefers an `ANTHROPIC_API_KEY` (or
`ANTHROPIC_AUTH_TOKEN`, an `apiKeyHelper`, Bedrock/Vertex) over your Claude login
whenever one is set, which would quietly bill every draft to API credits. So the app
removes those from the CLI's environment and skips your Claude Code user settings,
and every call goes through the account `claude` is logged into. Set
`AiAllowApiKey: true` if you do want a key used. Each call also runs without Claude
Code's tools, MCP servers, skills and agent prompt - about 450 tokens of overhead
instead of about 30,000.

**Usage in the top bar.** `AI today 4 · 38k tok · ~$0.31` counts today's calls; the
dot is green on your Claude login and amber on an API key. Hover for which account
it runs through, a per-command breakdown, the last 7 and 30 days, and the last
failure. Cost is Claude Code's list-price estimate: on an API key it is billed, on a
subscription it only measures how much of your plan's usage went. Every call is
logged as a JSON line in `%LOCALAPPDATA%\EmailTriage\ai-usage.jsonl` (30 days kept).

### Lavish feedback (`Ctrl+Shift+L`)
The **Lavish** button at the top right turns the window into something to comment on,
in the style of [lavish-axi](https://github.com/kunchenguid/lavish-axi): a brass outline
follows the mouse, a click on any element - a button, a label, a row, the reading pane -
opens a note card under it, and `Ctrl+Enter` sends. `Esc` drops the note, a second `Esc`
(or the button again) leaves comment mode. Nothing in the app reacts to keys or clicks
while it is on, so a stray `e` cannot archive the mail you are writing about.

It works over whatever is open. The button stays on top of the in-window pop-ups
(a reply, the move or snooze palette, the flag popup, help), so you can comment on
them mid-task. Settings and a meeting card each have a **Lavish** button of their own,
and `Ctrl+Shift+L` works in them too. Leaving comment mode puts the cursor back where it was,
in the reply you were typing, for example.

Each note is filed as an issue on the app's GitHub repo, labelled `lavish`, with the
element, where it sits, the tab, the app version and a tracking checklist. Issues there
are public, so **mail never goes into one**: text the app itself shows ("⚙ Settings") is
included, but anything bound to data - a subject, a sender, a folder name - is only sent
if you tick *Include what it says*, and the reading pane is never described.

- **Filing.** With a GitHub token - `GH_TOKEN`, `GITHUB_TOKEN`, or a signed-in
  [`gh`](https://cli.github.com) CLI - the issue is filed straight away under your name.
  Without one, GitHub's new-issue form opens in your browser already filled in; press
  *Submit* there. The app stores no GitHub secret of its own.
- **Tracking.** The panel down the right lists every note you have sent (kept in
  `%LOCALAPPDATA%\EmailTriage\lavish.json`) and reads their progress back from GitHub
  when comment mode opens: *Filed › Branch › PR › Merged › Released*. Click one to open
  the issue. Numbered brass pins mark the elements you commented on this session.
- **Moving an issue along.** `.github/workflows/lavish.yml` does it from the work itself.
  Name the branch `lavish-41-short-name` (or use GitHub's *Create a branch* on the
  issue, which makes `41-…`), and write `Fixes #41` in the pull request. The workflow
  labels the issue `lavish: branch`, then `lavish: in review`, closes it as
  `lavish: merged` when the PR merges into `main`, and marks it `lavish: released` once
  the release workflow ships a build containing that merge.
- **Repo.** Notes go to the repo the release was built from. `LavishRepo` in
  `settings.json` points a fork somewhere else.

Lavish only calls GitHub when you send a note or open comment mode with notes to
check, and it only ever sends what the note card shows.

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

## License

[MIT](LICENSE)
