# Email Triage for iPhone and iPad

A SwiftUI app for iPhone and iPad that triages your Outlook inbox by talking to Email
Triage on your PC over the local network. It needs no Microsoft account set-up and no Entra app
registration. How the PC side works and how it's secured is in the main
[README](../README.md#iphone-and-ipad-companion).

## What you need

There are two ways to get the app onto a device:

- **TestFlight (recommended).** GitHub builds the app on every change and TestFlight
  installs it on your iPhone and iPad by itself. No Mac or cable needed after the
  one-time set-up. Needs the paid **Apple Developer Program** ($99/yr). See
  [Updates through TestFlight](#updates-through-testflight).
- **From Xcode.** Needs a Mac with **Xcode 16 or later** and an **Apple ID**. A free
  Apple ID works, but Apple makes apps installed that way stop opening after **7 days**.
  Plug the device in and press Run again to renew it.

Either way, the iPhone or iPad needs **iOS / iPadOS 17** or later.

Updating the desktop app never needs a new build on the device. The app only talks to
the PC's companion API, so it gets new behavior as soon as the PC updates. A new build
is only needed when something under `ios/` changes.

## Install it from Xcode

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

## Updates through TestFlight

Every push to `main` that touches `ios/` runs the `iOS` workflow. It builds the app,
signs it, uploads it to App Store Connect and numbers the build after the workflow
run. TestFlight then installs it on each device with *Automatic Updates* on, usually
10–30 minutes after the merge. Builds for yourself as an internal tester skip App Review.

### One-time set-up

Everything is done in a browser. Only step 4 uses a terminal, and any machine with
`openssl` will do (Linux, macOS, or Git Bash on Windows).

1. **Join** the [Apple Developer Program](https://developer.apple.com/programs/enroll/).
   Approval can take a day or two.
2. **Register the app ID.** In [Certificates, Identifiers &
   Profiles](https://developer.apple.com/account/resources/identifiers/list), add
   an *App ID* of type *App* with the explicit bundle ID
   `io.github.ntschetterny.emailtriage`. Leave every capability off.
3. **Create the app.** In [App Store Connect](https://appstoreconnect.apple.com/apps),
   go to **+** › *New App*, choose iOS, pick that bundle ID, and enter any SKU. The
   name has to be unique across the App Store, so if *Email Triage* is taken, add a
   word. Only you see it in TestFlight.
4. **Create a distribution certificate.** Make a key and a signing request:

   ```sh
   openssl genrsa -out dist.key 2048
   openssl req -new -key dist.key -out dist.csr -subj "/CN=Email Triage/emailAddress=you@example.com"
   ```

   Under *Certificates*, add an **Apple Distribution** certificate and upload `dist.csr`.
   Download `distribution.cer`, then bundle it with the key:

   ```sh
   openssl x509 -inform DER -in distribution.cer -out dist.pem
   openssl pkcs12 -export -legacy -inkey dist.key -in dist.pem -out dist.p12 -passout pass:CHOOSE-A-PASSWORD
   ```

   If your `openssl` is older than 3.0, leave out `-legacy`.
5. **Create the profile.** Under *Profiles*, add an **App Store Connect** distribution
   profile for the app ID and the certificate. Name it, for example,
   *Email Triage App Store*, and download it.
6. **Create an API key.** In App Store Connect, go to *Users and Access* ›
   *Integrations* › *App Store Connect API* › *Team Keys*, and add a key with the
   **App Manager** role. Download the `.p8` file, which Apple offers only once, and
   note its *Key ID* and the *Issuer ID* shown above the list.
7. **Add the GitHub secrets.** Your Team ID is under *Membership details* on
   developer.apple.com.

   ```sh
   gh secret set APPLE_TEAM_ID --body "ABCDE12345"
   gh secret set ASC_KEY_ID --body "KEYID12345"
   gh secret set ASC_ISSUER_ID --body "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
   gh secret set ASC_KEY_P8 < AuthKey_KEYID12345.p8
   base64 -w0 dist.p12 | gh secret set IOS_DIST_CERT_P12
   gh secret set IOS_DIST_CERT_PASSWORD --body "CHOOSE-A-PASSWORD"
   base64 -w0 Email_Triage_App_Store.mobileprovision | gh secret set IOS_APPSTORE_PROFILE
   ```

   On macOS, use `base64 -i FILE` in place of `base64 -w0 FILE`. Then delete the local
   copies of `dist.key`, `dist.p12` and the `.p8` file, or keep them somewhere safe.
8. **Start the first build.** Under *Actions* › *iOS*, choose *Run workflow* on `main`.
9. **Add yourself as a tester.** When the build appears in App Store Connect ›
   *TestFlight*, make an *Internal Testing* group, add yourself, and turn on automatic
   distribution so every new build goes to the group.
10. **Install TestFlight** from the App Store on each iPhone and iPad. Accept the
    invitation, install Email Triage, and in TestFlight's page for the app, turn on
    **Automatic Updates**.

If the app was installed from Xcode before, TestFlight replaces it. If it was signed
with a free Apple ID, which is a different team, iOS keeps the old pairing out of
reach, so pair once more.

### Keeping it going

- Until all seven secrets are set, the workflow only builds for the simulator and
  notes why it skipped the upload.
- The certificate and profile last a year. When they expire, repeat steps 4, 5 and 7.
- TestFlight builds expire after 90 days. Any change under `ios/`, or running the
  workflow by hand, starts a new 90 days.
- Raise `MARKETING_VERSION` in the Xcode project when you want the visible version
  to change. The build number takes care of itself.

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
| `⌘⇧L` | Send feedback |

Hold ⌘ to see them listed. The app also works in Split View and Slide Over.

## Using it

- **Swipe right** on a conversation to archive it.
- **Swipe left** to snooze it, flag it, or move it to a folder.
- **Tap** a conversation to read the whole thread as the desktop shows it. The bar at
  the bottom has archive, snooze, move, flag and reply. Hold Reply for *reply to
  sender*.
- **Pull down** to refresh. The list also refreshes whenever you come back to the app.
- **Long-press** a conversation to mark it read or unread.
- **Attachments** on the newest message sit above the conversation. Tap one to open it
  in Quick Look, where you can also share or save it.
- **Move** puts the keyboard straight in the folder search. Type a few letters and
  press Return (or Go) to move to the top match.
- **Feedback**: *Send feedback...* in the filter menu, or the bubble at the top of a
  conversation. Say what should change and the PC files it on GitHub, as the
  desktop's Lavish button does. The sheet shows each note's progress.

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
| `Views/` | Inbox (a split view on iPad), thread and its attachments, snooze, move, reply, feedback, pairing and QR scanner screens |

The API it talks to is `src/EmailTriage.Companion`. The `iOS` GitHub workflow builds
the app for the simulator on every change under `ios/`, and on `main` uploads it to
TestFlight.
