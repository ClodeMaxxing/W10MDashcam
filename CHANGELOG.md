# Changelog

## W10MDashcam 0.9

Feature parity with W10MSecurityCam and W10MWebCam: what one app of the family did better now exists in all three where
it applies. Recording, crash detection, GPS and live view are unchanged.

**What you notice**
- **The battery tile turns red when the battery is draining although the phone says "Charging"** (a PC USB port or a
  small car adapter with 4K), judged every second from the power reading: Charging and the 5-minute average below
  −0.3 W. Before, the level had to drop 2 % over 20 minutes while the *Charger insufficient* notice was already up. The
  notice now also appears in `/status` (`chargerNotice`), and the web page's battery bar turns red too.
- **Temp badge in three levels:** *Paused* (amber) the moment charging pauses, *Hot* (red) after 20 s, *Protect* (red)
  while heat protection is on. Before, a one-second pause flashed the badge red.
- **A chime and a vibration** when heat protection sets in (the same as a power loss), and a short one when charging
  resumes.
- **View only without the login:** the page, `/camera/state`, `/status`, the recordings list, the log and the stats can
  be read on the home network without logging in; changes, the snapshot, the recordings themselves (play, download,
  captions) and `/login` need the login (the browser asks). The page shows a *View only* notice with a *Log in* button.
  The settings page is now served at `/` as well (the log is at `/log`); a switched-off web, log or stats page
  answers 403 (was 503 / 404).
- The phone's **addresses follow network changes** (a new DHCP lease, another Wi-Fi network).
- **Return to normal mode** button on the phone while the app is in safe mode; before, only the web page could do that.

**Under the hood**
- `/status` also carries W10MWebCam's field names (`uptime`, `battery`, `charging`, `onExternalPower`, `latencyMs`,
  `wifiBars`, `cpuPhone`, `cpuApp`, `heat`, `heatBadge`, `heatLevel`, `protect`, `alert`, `blackout`, `previewing`,
  `torchOn`, `webOn`, `logOn`, `statsOn`, ...); the older names stay. `charging` means current flows in (Charging), the
  new `onExternalPower` means a charger is connected (Charging or Idle); the low-battery text looks at the latter.
- Three previous sessions' logs are kept (`log-previous.txt`, `-2`, `-3`), each headed CRASHED, closed normally or
  ended abruptly; a clean close writes "App suspended" so the difference can be told.
- `tools\Soak-Monitor.ps1` is the same script in all three repositories (reads `/status` without a login, collects the
  phone log every 5 minutes while the log page is on); `tools\Make-Release.ps1` packs the release zip and SHA256SUMS.
- The battery and charger rules moved to `MainPage.Power.cs` (with the chime and vibration helpers), the same file as
  in W10MSecurityCam.

## W10MDashcam 0.8.1

Small follow-up to 0.8; recording, live view and crash detection are unchanged.

- Changing the login through `/config` (USB) now also needs the current password (`current`), as on the web page.
  `tools\set-camera-config.ps1` adds it from the login it signs in with.
- Changes (`/camera/set`, `/recordings/...`, `/config`) are read from the request body only; fields in the URL are
  ignored.
- Deleting saved clips from the web page (with *Delete saved clips from the web page* on) and *Delete all* with saved
  clips now only touch the app's own recordings, never other `.mp4` files someone put in the Saved folder.
- The web page's Save clip tip and message now say that the next file is kept too when *Save clip also keeps the next
  file* is on (the default).
- The live-view-on-open setting is saved under a new name (`LiveOnOpen`, was `AutoStart`); the old value is moved once.
- Removed an unused per-frame counter; a JSON reply can no longer fail on an unexpected shape.

## W10MDashcam 0.8

Saved clips are protected from the network by default, logins are harder to guess, and recording comes first.

**What you notice**
- **New phone switch *Delete saved clips from the web page*** (*Network and login*, off by default, needs the web
  page on). While it is off, nothing over the network can delete, unlock or replace saved clips, or change their
  space, even with the login: Delete with a saved clip, Delete all with saved clips included, Unlock, and the
  `savedlimit` / `replacesaved` fields (web page and `/config`) are refused with a note. Saved clips can then only be
  removed on the phone. Loop recordings can still be deleted, and Keep still works.
- **Changing the login on the web page needs the current password** (new *Current password* box), so a signed request
  caught on the network can't be sent again with a new password. The phone and `/config` over USB are unchanged.
- **New passwords need at least 8 characters** (8-64), on the phone, on the web page and through `/config`. A shorter
  password set before keeps working until you change it.
- **Wrong logins are slowed down:** after 5 wrong logins from one address within a minute, that address is refused for
  30 seconds (the web page answers `429 Too Many Requests`, the live view 401). A right login clears the count.
- The login challenge is renewed every 5 minutes. Browsers, VLC and NVRs take the new one by themselves; nobody is
  asked for the password again.
- **`/status` no longer contains the GPS position:** latitude, longitude, accuracy and heading are left out there
  (speed, fix and units stay). The web page still shows them.
- When the web page refuses an action it now shows the phone's reason instead of its own success text.
- An RTSP connection that has not started playing is closed when no complete request arrives for 30 seconds, so a
  silent or half-open connection no longer holds one of the 8 places. The last of the 8 only takes the PC over USB.
- The web port (5001) answers at most 16 connections at once; more get `503` "Busy, try again". A request has to
  arrive complete within 10 seconds, and a request with a body over 64 KB is refused. A reply or a recording download
  that the client takes nothing of for 30 seconds (a paused player, a dead connection) is cut off, so it no longer
  holds the file and the connection; this applies to the RTSP ports' web replies too.
- The stats page only samples while the log and stats pages are switched on.
- The pages can no longer be shown inside a frame on another site. An error answer (500) only says "Internal error,
  see the app log"; the details go to the app log.
- The web page and the log pages no longer switch themselves on for installs from before those switches existed (this
  only affected development builds before 0.7.6).

**Fixes**
- **Recording first:** live view was handed each frame before the recorder, so packetizing for every viewer could
  delay the recording (the encoder sink keeps only 3 frames before it skips to the next keyframe), and an error there
  skipped it. The recorder now gets each frame first.
- **Loop recording:** a file that could not be deleted (for example while it was being downloaded) was dropped from
  the list and its space counted as freed, leaving an orphaned file and a wrong free-space figure. It now stays listed
  and counted, the next oldest is deleted instead, and it is tried again at the next clean-up.
- **Heat protection:** when the stream was busy starting or stopping, the restart that lowers the video to 1080p at
  6 Mbps was skipped, so it could stay at 4K while heat protection was on. It is now retried by the once-a-second heat
  check.
- Switching focus to manual starts from the last sensible lens position: the 950's autofocus sometimes reports focus 0
  (the near end stop) while the picture is sharp, which made the manual picture jump out of focus (fix from W10MWebCam).
- Recorder failures on the camera thread (a frame not recorded) are logged: the first 5 with the error, then one line
  per 1000. Before, they left no trace.
- When the encoder sink drops a video backlog to wait for the next keyframe, the queued audio frames are kept.
- A comma inside a stats field (a format or bitrate label) no longer shifts the later columns of the stats row.
- The recordings list on the web page escapes `"` and `>` in names too.
- `.srt` captions: a damaged file can no longer make the export loop for billions of samples.
- The jobs that run every 2, 10 or 15 seconds were skipped whenever a one-second tick came late; they now count the
  ticks.
- If the Windows password vault fails while storing the default login, the app still starts and the log says so;
  nobody gets in until a login is saved (before, a missing password let everyone in).
- Failed web requests are logged, at most one line per 30 seconds.

**Under the hood**
- Login: random nonces, kept for 5 minutes; `qop="auth"` is offered and its nonce count must go up, while clients
  without qop (VLC, many NVRs) keep working. Each login answer must be for the address requested (for RTSP also an
  address under it, since VLC and many NVRs sign every request with the stream's base address). The login is checked
  once per request, recording downloads included.
- RTSP: no list query or lock for every frame, and one audio payload per frame for all viewers.
- The fps and Mbps counters are read and written safely between the encoder thread and the screen (64-bit values on
  ARM).
- The battery is read once a second and shared by every check that needs it; the resolution label is worked out only
  when the format changes; while the screen is black the recording card's texts are not redrawn (the checks behind
  them still run).
- The encoder sink hands each frame to the app without an extra copy, and reports handler errors again after a stream
  restart.
- The log: a line is only queued when it is logged; the file and the screen are updated once a second (also on suspend
  and after a crash).
- Recording file names must end exactly at `.mp4`.
- The old `CamAuto` and `Bitrate` settings keys are converted once and removed. Unused code removed.

**Tools**
- `tools\Crash-Dumps.ps1`, with `tools\Enable-Crash-Dumps.cmd` and `tools\Get-Crash-Dump.cmd` (from W10MWebCam):
  switch Windows crash-dump collection on for the installed W10MDashcam through Device Portal (it is per installed
  version, so switch it on again after an update), list the dumps and download them to `tools\dump\`. A Device Portal
  pairing made by a sister app's tools is reused.
- `tools\Sync-Core.ps1` compares the source files the three apps share (`-Check`, the default) and copies one app's
  copy over the others (`-Fix -From APP`). It needs all three repositories on the PC.
- `tools\Build-And-Deploy.cmd` finds Visual Studio 2019's MSBuild with vswhere (the 2019 Community path if vswhere is
  missing) and logs which one it used. It and `tools\Install-Only.cmd` now report the right exit code after the
  pairing retry.
- `tools\test-rtsp.ps1` takes the renewed login challenge during long runs.

## W10MDashcam 0.7.7

Security fixes; nothing else changes.

- A single crafted RTSP request with a negative `Content-Length` made the app handle the same request again forever
  until it ran out of memory, which stopped recording. It needed no login and reached the RTSP ports, which are always
  on (8554, and 8020 over USB). Such a request now closes the connection, and a client that never reads its replies
  is disconnected after 64.
- The login is needed over the USB cable too. Any program or web page on the PC the phone is plugged into can reach
  the USB port, so "no login over USB" let a web page delete every recording, saved clips included, or reset the
  login. The web page, `/status`, the recordings, downloads and `/config` now ask for the login on every port.
- Changes (`/config`, `/camera/set`, `/recordings/...`) also need the `X-Requested-With: W10MDashcam` header and the
  phone's address in the `Host` header, over USB too.
- `tools\set-camera-config.ps1` and `tools\get-phone-log-usb.ps1` take `-User` / `-Password` (or the `W10M_USER` /
  `W10M_PASSWORD` environment variables) and ask otherwise.

## W10MDashcam 0.7.6 (first release)

A dashcam app forked from W10MSecurityCam 1.1 (build 1.9.29).

**Recording**
- Records the camera's encoded stream (no second encode) to fragmented MP4 files: H.264, optional AAC sound, and a
  subtitle track. 4K30 at 24 Mbps by default. A new file every 1, 3, 5 or 10 minutes, cut at keyframes with no gap.
- Loop recording deletes the oldest of the app's own files only; other files in the same folder are never touched.
- Save clip (phone button, web page, or the phone's camera button) keeps the current file, the one before it and, by
  default, the next one, with a chime and a buzz. Keep / Unlock per recording on the web page. Clips still in use by
  Windows (indexer, downloads) are moved to Saved as soon as they are free, also after a restart.
- Crash detection (G-sensor, low 2.5 g / medium 1.6 g / high 1 g) saves the clip and the next file; screen taps don't count.
- GPS speed, position and heading, the date and time and IMPACT markers in a subtitle track in every recording;
  .srt download per recording on the web page.
- Record when the app opens (remembered); recording and live view start and stop separately.
- Stop when the power is lost: when the charger goes, a 60-second countdown shows on the screen (Keep recording skips
  it); then recording and live view stop, and they start again when the power returns. The delay (30 s to 5 min) or
  Never is set under Recording options or with `powerloss=`.

**Storage**
- Save to: Automatic (SD card, or the phone's storage when the card is missing or full), phone storage, SD card, or a
  chosen folder.
- Automatic space: loop space, saved-clip space and a reserve worked out from the storage, so small and large cards
  work without settings. Footage estimate ("keeps ~22 min") on the status card and in the Storage section.
- Recording stops by itself with a red on-screen warning, a buzz and a tone (the black screen lifts) when the storage
  cannot keep up (90+ dropped frames in a minute), is full with nothing to delete, or keeps failing. Resume recording
  on the phone or the web page.
- Delete all (loop recordings, optionally saved clips); Format SD moves recording to the phone's storage and opens the
  phone's Storage settings.
- Amber notice while recording to the phone's internal storage.
- Network and login shows the web page address (`http://PHONE-IP:5001/camera`) above the live-view addresses.
- Heat warnings: one full message at the top (including the usual cause: a charger that can't keep up, such as a PC
  USB port, drains and heats the battery); the sections only show a short "Heat protection" label.
- Recordings play and download over the USB cable too (`http://127.0.0.1:8020`); before, only the page and the list worked there.
- A dashcam first: a fresh install only records. The web page, the log and stats pages and the live view are off until
  switched on (Network and login); an update keeps an existing install's pages on.
- Switches for the web page (phone only) and for the log and stats pages (phone and web page).
- Stats page (`/stats`): graphs of the last 12 hours sampled every 30 s by the app itself (frame rate, bitrate, write
  speed, dropped frames, free storage, speed, battery, power, CPU, memory, Wi-Fi, states), the same page as the PC's
  soak report.
- "Charger insufficient" notice when the phone reports charging but the battery still drains.
- *Show camera picture (60 s)* sits right under Start recording / Start streaming.
- The internal-storage notice recommends an SD card to spare the phone's storage from constant writing.

**Screens**
- Phone and web page: Recording, Recording options and Storage sections; explanations behind (i) pop-ups; toggle
  switches instead of checkboxes on the phone.
- Status card: SPEED and STORAGE tiles replace engine and latency; latency shows in the viewers pill while someone
  watches; SAFE on the resolution pill in safe mode.
- Web page: recordings list with play, download, captions, keep/unlock and delete; reloads itself after an app update.
- New icon (a phone sideways in a car mount).

**Under the hood**
- The recordings list and the saved-clip totals come from memory instead of re-reading the folder (folder queries in
  an indexed library folder cost frames).
- Single deletes no longer re-scan the folder; colours are made once instead of every second.
- New config fields: `record`, `live`, `recordonopen`, `liveonopen`, `location`, `segment`, `storage`, `savedlimit`,
  `replacesaved`, `keepnext`, `impact`, `gps`, `units`, `subtitles`, `saveclip`, `resume`, `powerloss`, `testincident`,
  `testpowerloss`.

### Development builds
| Build | Content |
|---|---|
| 0.1 | New identity (W10MDashcam) |
| 0.2-0.3 | Fragmented MP4 recorder, loop recording, Save clip, storage choice, recordings page |
| 0.4 | Camera button, record on open, Save clip feedback |
| 0.5 | Crash detection, locking, GPS subtitles, captions download, storage-full handling, new icon |
| 0.6 | Automatic space, SD fallback, recording-stopped warning; new sections, delete all, format SD |
| 0.7 | Toggle switches, internal-storage notice, stop on power loss, release docs |

## Inherited from W10MSecurityCam

Release 1.0 is the first version considered complete for this fork. Build numbers 1.0-1.9 before it were the
development steps; they are kept in the history table for reference.

### Release 1.1 (build 1.9.29.0)
- **Crash fix:** the app could close about a second after the first 4K frame on a fresh launch (crash dump: fast-fail abort from an exception escaping the native sink's worker thread). The native sink and the frame handler now catch and log such errors ("handler threw an exception - ignored") instead of ending the app.
- **Camera power saving:** one "Power-saving camera features" box (stabilization off for a fixed mount, hold focus once it has settled); untick it to choose each option. HDR and noise-reduction controls were removed because this camera does not offer them.
- **Power monitor:** the status block shows net watts into or out of the battery (5-minute average, restarted whenever the charger is plugged in or out or charging pauses, so a reading never mixes two states), the phone's draw while it is on battery or charging is paused (from the capacity change; the driver's own rate is stale on this phone and is not used), the charger state Windows reports, and CPU use (phone and app). A "Power:" line is logged every minute for comparing settings. `probe=power` dumps the raw battery and CPU readings.
- **Flashlight:** on/off toggle on the phone and the web page, only while streaming, switches off by itself after 30 seconds (this phone cannot dim it). It heats the phone quickly.
- **Heat protection:** if charging pauses on a good charger for 20 s the phone is probably too hot. The app then forces the power-saving options on (screen blackout, preview off, stabilization off, held focus), locks the flashlight and shows a red warning on the phone and the web page. While it is active the video is also lowered to 1080p at 6 Mbps (4K kept the phone hot; 1080p let charging resume within about 30 s). Your own settings, including the video, come back after 30 s of charging again and at least 3 minutes (the flashlight stays off). The temperature line reads Healthy or Unstable on this basis. The frame rate is no longer used: it stayed at 25 fps while Windows warned of overheating.
- **Blackout by default:** while streaming the phone screen is black and the preview is off, whether the camera is automatic or manual and whether settings were changed on the phone or on the web page. Changing settings on the web page never wakes the screen. Touching a camera control on the phone (or pressing Wake) lifts the blackout and shows the picture for 60 s after the last touch; the stream restarts once to start the preview and once more to stop it afterwards (at most once a minute). The preview is never switched on or off mid-stream: doing so stalled the 4K pipeline and could crash the app (this was the cause of crashes on "Save power now"). "Save power now" / Wake only change the overlay and brightness.
- **Simpler phone screen:** the main screen holds Start/Stop, the status card, warnings, Show camera picture (60 s) and Black out the screen now; Video, Camera (with the flashlight), Power, Network and login, and Details and log fold away. Both buttons restart the stream once to start or end the picture. Measured before choosing: keeping the preview running behind the black screen doubled the app's CPU use (12% to 24%) and raised the battery draw, so the preview stays off while streaming.
- **Green theme:** black background with soft green accents on the phone and the web page (section bars, buttons, switches, sliders); the version sits small and bright green at the top right of both.
- **Status card:** a pulsing green dot (red in heat protection), the state, resolution and viewer pills and large fps / Mbps figures on the phone; the web page adds tiles for battery, temperature, CPU, Wi-Fi, memory and uptime. The pulse stops while the phone screen is black.
- **Graphic status:** battery bar, Wi-Fi signal bars and meters for temperature, CPU, memory, engine and encoder latency on the web page; battery, Wi-Fi, engine and latency tiles on the phone. Web notes moved into hover/tap info icons; High BW is a toggle beside the resolution and bitrate; the stream addresses open in a pop-up.
- Status tiles match on both screens: battery, Wi-Fi, temperature (Healthy / Unstable badge), phone and app CPU, memory, engine (Fast / Safe mode badge), encoder latency and power (watts with a 0-15 W bar). The charger warning line is gone (the battery tile turns red instead); the default-login warning is a themed notice. The flashlight moved into the Camera section.
- The stream start/stop chirp is Windows' own recording sound; it follows the phone's Ringer + notifications volume (noted under Power).
- Cleaner status lines (CPU, Temperature); web page sections fold with an arrow at the right (remembered per browser).
- Longer settle time before auto-starting 4K, a delay before the first camera-control apply, the device scan is on demand (`probe=devices`).
- `tools/Soak-Monitor.ps1` records every status value on the PC for long runs and keeps an HTML report with graphs and a summary (availability, restarts, heat time, fps, memory trend).
- `tools/Monitor-Portal.ps1` logs whole-phone CPU, GPU and battery from Device Portal once a minute.

### Release 1.0 (build 1.9.1.0)
- MIT licence added.
- The login is always on, default admin / admin, for the stream and the web page. It can be changed on the settings page (Login section) or on the phone; an empty password resets it.
- `/status`, the log and snapshots now need the login from the network too (USB is exempt).
- Web changes need an `X-Requested-With` header, so another website cannot make your browser change settings.
- Startup self-check: two failed launches in a row reset saved settings (login kept).
- Status block (battery, charging, Wi-Fi, memory, uptime, Battery Saver state) and an indirect heat hint; low-battery and weak-charger warnings.
- One-click power save on the phone, matching the web button; blackout/preview rules in one place.
- Phone screen and web page split into Status / Video / Camera / Power.
- Web page shows the right bitrate and resolution right after a change.

### Development history
| Build | Content |
|---|---|
| 1.0 | Fork of LumiaCam, new identity |
| 1.1 | RTSP server, RTP over TCP |
| 1.2 | Digest login, RTCP SR, `/status`, `/config` over USB, defaults 1080p / 4 Mbps, power warnings |
| 1.3 | Optional RTP over UDP and snapshots, weak-charger warning |
| 1.4 | AAC microphone audio |
| 1.5 | `format=` / `bitrate=` over USB, memory and Wi-Fi in `/status` |
| 1.6 | Focus / exposure / white balance with per-setting Auto, Focus once and lock |
| 1.7 | Web settings page, high bandwidth mode |
| 1.8 | Start/Stop stream, safe-mode visibility, USB address block |
| 1.9.13-1.9.29 | Heat protection lowers video to 1080p/6 Mbps; one display switch; preview only changes at stream (re)start; "Show camera picture (60 s)"; folded phone layout; empty first sample ignored; correct no-charger note |
| 1.9.12 | Native-sink crash guard, camera power saving, power monitor, flashlight, heat protection, folding web sections |
| 1.9.1 | Always-on login (admin / admin default), password change on the web page, login required everywhere |
| 1.9 | Self-check, status, heat hint, phone power-save button, layout sections |
