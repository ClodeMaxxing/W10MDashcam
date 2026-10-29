# Changelog

Release 1.0 is the first version considered complete for this fork. Build numbers 1.0-1.9 before it were the
development steps; they are kept in the history table for reference.

## Release 1.0 (build 1.9.1.0)
- MIT licence added.
- The login is always on, default admin / admin, for the stream and the web page. It can be changed on the settings page (Login section) or on the phone; an empty password resets it.
- `/status`, the log and snapshots now need the login from the network too (USB is exempt).
- Web changes need an `X-Requested-With` header, so another website cannot make your browser change settings.
- Startup self-check: two failed launches in a row reset saved settings (login kept).
- Status block (battery, charging, Wi-Fi, memory, uptime, Battery Saver state) and an indirect heat hint; low-battery and weak-charger warnings.
- One-click power save on the phone, matching the web button; blackout/preview rules in one place.
- Phone screen and web page split into Status / Video / Camera / Power.
- Web page shows the right bitrate and resolution right after a change.

## Development history
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
| 1.9.1 | Always-on login (admin / admin default), password change on the web page, login required everywhere |
| 1.9 | Self-check, status, heat hint, phone power-save button, layout sections |
