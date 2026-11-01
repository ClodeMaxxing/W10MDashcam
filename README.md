# W10MDashcam

A dashcam app for the Lumia 950 (Windows 10 Mobile). It records the camera in 4K to short files with loop recording,
keeps the clips that matter (Save clip, the camera button, crash detection), writes the time, GPS speed and position
into every recording, and lets you watch and download recordings from another device on the same Wi-Fi. A live view
over RTSP is still there if you want it.

**Compatibility:** tested only on the **Lumia 950** with Windows 10 Mobile 1709, OS build **10.0.15254.603**. Other
phones are untested. Full guide: [the project wiki](https://github.com/ClodeMaxxing/W10MDashcam/wiki).

It is a fork of [W10MSecurityCam](https://github.com/ClodeMaxxing/W10MSecurityCam), itself a fork of
[W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam) (formerly LumiaCam; see Credits). It has its own package
identity (`W10MDashcam`, CN=W10MDashcam), so it installs beside both. Only one app can use the camera at a time.

## Addresses

| What | Address |
|---|---|
| Settings and recordings page | `http://PHONE-IP:5001/camera` (over USB: `http://127.0.0.1:8020/camera`) |
| Recordings (JSON) | `http://PHONE-IP:5001/recordings` |
| Health | `http://PHONE-IP:5001/status` (JSON; GPS speed and fix, but not the position) |
| Log | `http://PHONE-IP:5001/log` (with *Log and stats pages* on) |
| Stats | `http://PHONE-IP:5001/stats`: graphs of the last 12 hours, sampled every 30 s |
| Live view (RTSP) | `rtsp://PHONE-IP:8554/live` (USB: `rtsp://127.0.0.1:8020/live`) |

**Off on a fresh install:** the app is a dashcam first and only records. Switch on *Web page* (and, if wanted, *Log and
stats pages*) under *Network and login* on the phone; the live view has its own switches. The page, `/status`, the
recordings list, the log and the stats can then be read on the home network without a login (view only); changes,
the recordings themselves and the live view need the login (see Login), over the USB cable too. With *Web page* off
the phone serves nothing over HTTP, not even over USB (a switched-off page answers 403). The settings page is also
served at `http://PHONE-IP:5001`. Give the phone a fixed address (a DHCP reservation in the router) so the page is
always at the same place.

The web port (5001) answers at most 16 connections at once (more get "503 Busy, try again"); a request has to arrive
complete within 10 seconds, and a download the client takes nothing of for 30 seconds is cut off. The RTSP ports take
8 connections, the last one only from the PC over USB; a connection that has not started playing is closed when it
sends no complete request for 30 seconds.

## Recording

- **Starts by itself:** with *Record when the app opens* on (the default), recording starts as soon as the app opens.
  The top of the phone screen has **Start/Stop recording** and **Start/Stop streaming** (live view); they are separate,
  and the camera runs while either is on.
- **Quality:** 4K at 30 fps, 24 Mbps by default, so number plates stay readable. That is about 155 MB a minute, 9.3 GB
  an hour. Microphone audio is optional (Video section).
- **Files:** a new file every 1, 3 (default), 5 or 10 minutes, named by date and time (`2026-10-31_14-05-00.mp4`).
  Files are fragmented MP4: a file cut short by a crash or a full card still plays up to the last 2 seconds.
- **Loop recording:** the oldest files are deleted to stay within the space for loop recording. Only the app's own
  recordings are ever counted or deleted; other files in the same place are never touched.
- **Save clip:** the big button on the phone or the web page, or a full press of the phone's **camera button**. The
  current file ends at once (a new one starts with no gap) and is kept together with the file before it, and by default
  the next one too. A chime and a buzz confirm it. Saved clips go to the `Saved` folder, which loop recording never
  touches. On the web page, **Keep** saves any loop recording and **Unlock** gives a clip back to loop recording.
  Deleting, unlocking or replacing saved clips (and changing their space) from the web page or `/config` only works
  with *Delete saved clips from the web page* switched on (*Network and login* on the phone, off by default); otherwise
  saved clips can only be removed on the phone.
- **Crash detection:** the G-sensor saves the clip (and the next file) on a jolt above the chosen level: low 2.5 g,
  medium 1.6 g (default), high 1 g. Taps on the screen within 2 seconds don't count.
- **Time, speed and position:** every recording has a subtitle track with the date and time, GPS speed (km/h or mph,
  automatic by region), position, heading and IMPACT markers. VLC and most players show it (Subtitle menu). The web page
  also downloads it as an `.srt` file (**Captions**); keep it next to the video with the same name.

## Storage

The **Storage** section (phone and web page) holds where recordings go, their space, and **Delete all**.

- **Save recordings to:** *Automatic* (the SD card when there is one, the phone's storage when the card is missing or
  full; it looks at the card again after 10 minutes), *Phone storage*, *SD card* (recording stops with a warning if the
  card is missing), or a **chosen folder**. Avoid folders inside Videos, Pictures or Music: Windows indexes them, and
  that slowed recording down so much that frames were lost.
- **Automatic space (default):** the app counts the free space plus its own recordings. Saved clips get about a fifth
  of it (1 to 8 GB), a reserve of 5% (256 MB to 1 GB) always stays free, and loop recording gets the rest, so a small
  and a large card both work without any setting. Fixed sizes can be chosen instead. With *Replace the oldest saved
  clips when full* off (the default) a warning appears when saved clips fill their space.
- **How much footage:** the status card's STORAGE tile and the Storage section show how long the loop keeps at the
  current quality, measured from the files written.
- **SD card size:** at 4K / 24 Mbps, 2 hours of loop needs about 19 GB and 4 hours about 37 GB, so a 32 GB card for
  2 hours and 64 GB for 4. 24 Mbps is only 3 MB/s, so any Class 10 / U1 card keeps up; a "high endurance" card lasts
  longer under constant writing.
- **Delete all:** deletes every loop recording (and, if you choose, the saved clips). The phone asks first; the web page
  asks you to type DELETE. Recording carries on in a new file.
- **Format SD card:** apps cannot format a card. *Format SD...* in the Storage section moves recording to the phone's
  storage and opens the phone's Storage settings, where the card can be formatted; coming back to the app uses the card
  again. (Formatting cannot be started from the web page.)
- **Internal storage notice:** while recording to the phone's own storage, an amber notice under the login one says so
  and recommends an SD card: constant recording wears out the phone's internal storage.
- **Charger insufficient:** an amber notice appears when the phone says it is charging but the battery still drains.

### When recording stops by itself

If the storage needs you, recording stops instead of carrying on badly, and a red **RECORDING IS NOT WORKING** banner
shows the reason with a **Resume recording** button. The phone buzzes and plays a tone, and the black screen lifts and
stays lifted until you resume. The web page shows the same. Reasons:

- the storage cannot keep up: 90 or more frames dropped within a minute (fewer than that shows a warning),
- the storage is full and holds nothing the app may delete (with Automatic, the phone's storage is used instead first),
- starting a file failed 6 times in a row (for example the SD card was removed while *SD card* was chosen).

`testincident=1` (see Settings over USB) shows the banner on purpose; `resume=1` clears it.

## Phone screen and web page

Both share one look (black with green accents, version at the top right) and the same sections. Click a header to fold
it; explanations sit behind small (i) icons.

- **Status:** state, resolution, uptime and viewers (with the stream latency while someone watches), fps and Mbps, and
  tiles: battery (red when the battery is draining although the phone says "Charging", judged every second from the
  power reading), Wi-Fi, temperature (Healthy / Paused / Hot / Protect), phone and app CPU, memory, **speed**,
  **storage** (footage the loop keeps) and power. Notices below it: default login, internal storage, charger
  insufficient (with the watts), heat. In safe mode a **Return to normal mode** button appears.
- **Recording:** the REC card with **SAVE CLIP** and a one-line summary, Start/Stop recording, *Record when the app opens*.
- **Recording options:** file length, crash detection (with the live G-sensor reading), keep the next file, GPS, speed
  units, subtitles.
- **Storage:** see above; on the web page also the list of recordings (play, **Download**, **Captions**, **Keep**/**Unlock**,
  **Delete**).
- **Video, Camera, Power, Network and login, Details and log:** as in W10MSecurityCam (resolution and bitrate, high
  bandwidth, microphone; automatic or manual focus, exposure and white balance, flashlight; black screen, power-saving
  camera features; stream addresses, login).

**Black screen:** while the camera runs, the phone screen is black and the preview is off (an OLED screen uses almost
no power for black). Tap it to read the status for 15 seconds; **Show camera picture** (or a tap on the dark preview
area) shows the picture for 60 seconds (the camera restarts briefly to start and stop the preview, which also starts
a new file). **Preview on the phone** (under Camera) is off by default; *Keep the preview running behind the black
screen* (under Power, off by default: it costs CPU and heat) keeps it alive so a tap shows the picture at once; heat
protection forces that off. The same rules run in W10MSecurityCam and W10MWebCam.

**Heat protection:** Windows 10 Mobile gives apps no temperature. If charging pauses on an adequate charger for
20 seconds, the phone is probably hot: the app lowers the video to 1080p at 6 Mbps, forces the power-saving options on,
shows a red warning and chimes and vibrates (as for a power loss). Your settings come back after 30 seconds of
charging again and at least 3 minutes, with a short chime. The
usual cause is a power source that can't keep up (a PC USB port, a small car adapter): the phone then also drains its
battery, which heats it. The warnings say so; use a 2 A or better charger.

## In the car

- The app must be open, with the phone on the charger. Windows takes the camera away when the screen locks or another
  app opens; the black screen keeps it on without burning power.
- Windows Phone cannot start an app when it powers up, so open the app at the start of each drive; it then records by
  itself.
- **When the power goes** (the car is switched off), an orange countdown shows on the screen: after 60 seconds recording
  and live view stop so the battery is not run flat, and they start again when the power returns. *Keep recording*
  skips it once; *Recording options > Stop when the power is lost* sets the delay (30 s to 5 min) or turns it off.
- Heat is the main risk: keep the phone out of direct sun and use a good 12 V adapter. Watch the TEMP tile and the
  POWER tile (watts into the battery).

## Login

One login protects every change on the page, the recordings themselves (play, download, captions), the snapshot,
`/config` and the live view, over Wi-Fi and over the USB cable alike: any program or web page on the PC the phone is
plugged into can reach the USB port. Reading the page, the recordings list, `/status`, the log and the stats needs no
login (view only; the page shows a *View only* notice with a *Log in* button). A fresh
install uses **admin / admin** and warns until you change it: on the web page or on the phone (*Network and login*), or
over USB with `tools\Set-Camera-Login.cmd` (it asks for the current login first). An empty password resets it to
admin / admin; it does not remove the login. Forgot it? Set a new one on the phone (*Network and login*). Digest
authentication is used; traffic is not encrypted, so use it on a network you trust.

Since 0.8 a new password needs at least 8 characters everywhere (a shorter one set before keeps working until you
change it), and changing the login on the web page also needs the current password (*Current password*). After 5
wrong logins from one address within a minute, that address is refused for 30 seconds (the web page answers
`429 Too Many Requests`, the live view 401). The phone renews the login challenge every 5 minutes; browsers, VLC and
NVRs take the new one by themselves without asking for the password.

## Settings over USB

`tools\set-camera-config.ps1 -Body "field=value&..."` sends settings to `/config` over the USB cable (`/config` only
works over USB). It needs the login: give `-User` and `-Password`, or set the environment variables `W10M_USER` and
`W10M_PASSWORD`; otherwise it asks. Changing the login there also needs the current password (`current`); the script
adds it from the login it signs in with. Fields are read from the request body only, never from the URL. The web page
uses the same fields (`/camera/set`). Every change (POST), over USB
too, needs the login, the header `X-Requested-With: W10MDashcam` and the phone's address as `Host` (127.0.0.1 or
localhost over USB, an IP address over Wi-Fi); the script sends them.

| Fields | Values |
|---|---|
| `record`, `live`, `recordonopen`, `liveonopen` | `on` / `off` |
| `location` | `auto`, `phone`, `sd`, `custom` (a folder chosen on the phone first) |
| `segment` | minutes per file (1-30) |
| `storage` | loop space: `auto`, GB, or `all` |
| `savedlimit` | saved clips' space: `auto`, GB, or `none` (only with *Delete saved clips from the web page* on) |
| `replacesaved` | `on` / `off` (only with *Delete saved clips from the web page* on) |
| `keepnext`, `gps`, `subtitles` | `on` / `off` |
| `impact` | `off`, `low`, `medium`, `high` |
| `units` | `auto`, `kmh`, `mph` |
| `powerloss` | seconds without power before recording stops (10-3600), or `off` |
| `log` | `on` / `off`: the log and stats pages (the web page itself is switched on the phone) |
| `saveclip`, `resume`, `testincident` | `1` |
| `testpowerloss` | `1` acts as if the charger were unplugged (for testing), `0` back to normal |
| Camera and stream | `format=WIDTHxHEIGHT@FPS`, `bitrate=MBPS\|auto`, `highbw`, `audio`, `camera`, `focus`, `exposure`, `wb`, `powersave`, `stream`, `engine=normal`, `udp`, `user`, `password` (a new one needs at least 8 characters; on the web page, `/camera/set`, also `current`, the current password) |

`http://127.0.0.1:8020/config` shows the current values (after signing in). Recordings over HTTP: `/recordings/file?name=N[&saved=1][&dl=1]`
(with byte ranges for seeking), `/recordings/srt?name=N[&saved=1]`, and POST `/recordings/save`, `/recordings/delete`
(`name`, `saved`), `/recordings/lock`, `/recordings/unlock`, `/recordings/deleteall` (`confirm=DELETE`, `saved=1` to
include saved clips). `/recordings/unlock` and deleting with `saved=1` are refused (403) unless *Delete saved clips
from the web page* is on.

## Install a release

1. Download `W10MDashcam_<version>_ARM.zip` from the
   [latest release](https://github.com/ClodeMaxxing/W10MDashcam/releases/latest) and unzip it. Ready-built: no
   compiling and nothing else to download. To check the download, compare the value from
   `Get-FileHash .\W10MDashcam_<version>_ARM.zip -Algorithm SHA256` (PowerShell) with the release's `SHA256SUMS.txt`.
2. On the phone: *Settings > Update & security > For developers*: **Developer mode**, **Device discovery** and
   **Device Portal** on.
3. On a PC on the same Wi-Fi, open the Device Portal address the phone shows, pair with the PIN, then
   *Apps > Install app*: choose the app package **and add its three dependencies** (all in the zip):

| File | What it is |
|---|---|
| `W10MDashcam_<version>.0_ARM.appx` | The app (the package version has four parts, for example `0.8.0.0`) |
| `Dependencies\Microsoft.NET.Native.Framework.1.7.appx` | .NET Native framework 1.7 (ARM) |
| `Dependencies\Microsoft.NET.Native.Runtime.1.7.appx` | .NET Native runtime 1.7 (ARM) |
| `Dependencies\Microsoft.VCLibs.ARM.14.00.appx` | Visual C++ runtime 14.00 (ARM), for the native encoder sink |

   Without the dependencies the install fails with a missing-framework error (unless another app already installed
   them). They are Microsoft's standard runtime packages, the ones Visual Studio deploys with any .NET Native UWP app.
4. Open W10MDashcam on the phone and allow the camera, microphone and location. `INSTALL.txt` in the zip and the wiki's
   [Installation](https://github.com/ClodeMaxxing/W10MDashcam/wiki/Installation) page have the same steps.

To update, install the newer package the same way; settings are kept.

## Build and install

Same PC setup as [W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam) (formerly LumiaCam): VS 2019 with UWP,
C++ UWP and ARM tools, SDK 10.0.19041, IP over USB. Bump `Version` in `W10MDashcam/Package.appxmanifest` for every
install, or the phone refuses it.

| Script | Use |
|---|---|
| `tools\Build-And-Deploy.cmd` | Build Release ARM and install on the phone (USB first, then Wi-Fi using `tools\phone-ip.txt`). Finds Visual Studio 2019's MSBuild with vswhere (the 2019 Community path if vswhere is missing) |
| `tools\Install-Only.cmd` | Re-install the last build |
| `tools\Get-Phone-Log.cmd` | Download the phone log (needs the login, like the two below) |
| `tools\Set-Camera-Login.cmd`, `tools\set-camera-config.ps1` | Login and settings over USB (`-User`/`-Password` or `W10M_USER`/`W10M_PASSWORD`, otherwise they ask) |
| `tools\Test-RTSP.cmd`, `tools\test-rtsp.ps1` | Check the live view |
| `tools\Soak-Monitor.ps1`, `tools\Monitor-Portal.ps1` | Long-run status recorder with an HTML report; Device Portal CPU/GPU/battery log |
| `tools\Crash-Dumps.ps1` | Windows crash dumps through Device Portal: `-Pair PIN` once, `-Enable` (again after each app update), `-List`, `-Get` (to `tools\dump\`), `-Disable` |
| `tools\Enable-Crash-Dumps.cmd`, `tools\Get-Crash-Dump.cmd` | Switch crash dumps on and list them / download W10MDashcam's dumps |
| `tools\Sync-Core.ps1` | Compares the source files the three apps share (`-Check`) or copies one app's copy over the others (`-Fix -From APP`); needs all three repositories |

Put the phone's IP address in `tools\phone-ip.txt` (one line, not committed). Developer notes are in
[NOTES.md](NOTES.md), release history in [CHANGELOG.md](CHANGELOG.md).

## Credits

- **[W10MWebCam](https://github.com/ClodeMaxxing/W10MWebCam)** (formerly LumiaCam): the camera engine and the low-latency encoder sink.
- **[W10MSecurityCam](https://github.com/ClodeMaxxing/W10MSecurityCam):** the RTSP server, login, settings page, status and power monitor, heat protection and theme.
- **W10MDashcam:** recording (fragmented MP4 writer, loop recording, saved clips, storage handling), crash detection,
  GPS subtitles, the recordings page and the dashcam screens.

## Licence

MIT, see [LICENSE](LICENSE). It comes with no warranty. It is a hobby dashcam: do not rely on it alone as evidence or for
anything safety-critical, and check your local rules on dashcams and recording sound.
