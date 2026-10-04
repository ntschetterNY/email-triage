# Email Triage for iPhone and iPad

A SwiftUI app for iPhone and iPad that triages your Outlook inbox by talking to Email
Triage on your PC over the local network. It needs no Microsoft account set-up and no Entra app
registration. How the PC side works and how it's secured is in the main
[README](../README.md#iphone-and-ipad-companion).

## What you need

- A Mac with **Xcode 16 or later**. Apple only lets iOS apps be built on a Mac.
- An **Apple ID**. A free one works, but Apple makes apps installed that way stop
  opening after **7 days**. Plug the device in and press Run again to renew it. A paid
  Apple Developer Program membership ($99/yr) makes it last a year and lets you
  share the app through TestFlight.
- An iPhone or iPad on **iOS / iPadOS 17** or later, on the same Wi-Fi as the PC.

## Install it on your iPhone or iPad

1. Open `ios/EmailTriage.xcodeproj` in Xcode.
2. Select the **EmailTriage** target › **Signing & Capabilities**, tick *Automatically
   manage signing*, and pick your Apple ID under **Team**. If you haven't added it
   yet, use *Add an Account...*.
3. If Xcode says the bundle identifier is taken, change
   `io.github.ntschetterny.emailtriage` to something of your own, such as
   `com.yourname.emailtriage`.
4. Plug the iPhone or iPad in with a cable, unlock it, and choose it as the run
   destination at the top of the Xcode window.
5. The first time, the device asks you to turn on **Developer Mode** (Settings ›
   Privacy & Security › Developer Mode) and restart.
6. Press **Run** (⌘R). If iOS says the developer is untrusted, go to Settings ›
   General › VPN & Device Management, tap your Apple ID and choose **Trust**. To use
   both an iPhone and an iPad, install it on each one.

## Pair it with the PC

1. On the PC, open Email Triage › Settings (`Ctrl+,`) › **iPhone and iPad**. Tick *Let
   my iPhone or iPad triage this inbox over Wi-Fi*, then click **Pair a device...**.
   The same code pairs any number of devices.
2. In the app, tap **Scan pairing code** and point the camera at the PC screen. You can
   also copy the link shown under the code to the device (for example with
   Universal Clipboard), then tap **Paste pairing link** or just tap the link.
3. Allow **Local Network** access when iOS asks. Without it the app can't see the PC.

## On iPad

The inbox sits on the left and the open conversation on the right, as in Mail.
Archiving, snoozing or moving a conversation opens the next one, as on the desktop. With
a keyboard (Magic Keyboard, Smart Keyboard or Bluetooth), the desktop's keys work while
a conversation is open:

| Key | Does |
|---|---|
| `j` / `k` | Next / previous conversation |
| `e` | Archive |
| `h` | Snooze |
| `v` | Move to a folder |
| `a` | Flag or unflag |
| `r` | Reply all |
| `Shift+R` | Reply to sender |

Hold ⌘ to see them listed. The app also works in Split View and Slide Over.

## Using it

- **Swipe right** on a conversation to archive it.
- **Swipe left** to snooze it, flag it, or move it to a folder.
- **Tap** a conversation to read the whole thread as the desktop shows it. The bar at
  the bottom has archive, snooze, move, flag and reply. Hold Reply for *reply to
  sender*.
- **Pull down** to refresh. The list also refreshes whenever you come back to the app.
- **Long-press** a conversation to mark it read or unread.

The filter button at the top right shows unread mail only, and has **Unpair**.

## If it can't connect

- Check that the device and the PC are on the same Wi-Fi. Guest networks often keep
  devices apart. Away from the PC, or when its network is *Public* and can't be
  changed, set up the relay (Settings › iPhone and iPad on the PC) and pair again.
- Look at the status line under Settings › iPhone and iPad on the PC. It shows the
  address and port the PC is listening on, and when a device last connected.
- Windows Firewall has to allow Email Triage on private networks. That prompt needs
  an administrator, so on a managed PC ask IT. If the PC's network is set to
  *Public*, switching it to *Private* may be all that's needed. If it's locked, use
  the relay instead: it needs nothing from the firewall.
- Check that iOS Settings › Privacy & Security › Local Network has Email Triage on.
- If you pressed *Unpair all devices* on the PC, pair the device again.

## Code

| File | What it does |
|---|---|
| `EmailTriageApp.swift` | App entry, and opening `emailtriage://pair` links |
| `Pairing.swift` | Reading the pairing link, and keeping it in the Keychain |
| `CompanionClient.swift` | HTTPS calls to the PC, certificate pinning, trying each PC address and then the relay |
| `Relay.swift` | The relay: a Supabase Realtime channel, every call sealed with AES-GCM under the pairing code's key |
| `InboxModel.swift` | The list and every triage move; a move shows at once and is undone if the PC refuses it |
| `Views/` | Inbox (a split view on iPad), thread, snooze, move, reply, pairing and QR scanner screens |

The API it talks to is `src/EmailTriage.Companion`. The `iOS` GitHub workflow builds
the app for the simulator on every change under `ios/`.
