namespace W10MDashcam.Streaming
{
    /// <summary>
    /// The settings web page (http://PHONE-IP:5001/camera): the phone screen, mirrored. The same status card with the
    /// recording row as the button, the recording card, the Live view bar, the service groups, the Preview / Screensaver /
    /// Flashlight buttons and the same drawers (recording options and storage included, with the recordings list). The two
    /// differences: a Log in button (the page is view only without the login) and the web page switch, which only the phone
    /// can turn off. Plain HTML + a little script; the state comes from /camera/state every 3 s.
    /// </summary>
    internal static class CameraPage
    {
        public const string Html = @"<!doctype html>
<html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>W10MDashcam</title>
<style>
body{font-family:'Segoe UI',system-ui,sans-serif;background:#050805;color:#d6e8d9;margin:0;padding:12px;max-width:420px}
a{color:#9cf0b0}
.card{background:#071109;border:1px solid #1f3a26;border-radius:8px;margin:0 0 6px;overflow:hidden}
.ttl{display:flex;justify-content:space-between;align-items:baseline;padding:8px 10px 6px;background:#050a06;border-bottom:1px solid #1f3a26}
.ttl b{font-size:20px;font-weight:600;color:#e4f5e7}.ttl i{font-style:normal;color:#39ff14;font-size:11px;font-weight:bold}
.srow{position:relative;display:flex;gap:9px;align-items:center;height:46px;padding:0 10px 0 16px;margin:8px;background:#0f2416;border:1px solid #1f4a2a;border-radius:8px;overflow:hidden;cursor:pointer;user-select:none}
.srow:before{content:'';position:absolute;left:0;top:9px;bottom:9px;width:4px;border-radius:0 2px 2px 0;background:#16301e}
.srow.on:before{background:#39ff14}
.srow b{font-weight:600;letter-spacing:1px;font-size:15px;color:#cfe8d4;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.srow.on b{color:#fff}.srow .rt{font-style:normal;font-size:11px;color:#7fae86;white-space:nowrap}.srow.hot b{color:#ff6b6b}
.srow .fmt{margin-left:auto;border:1px solid #2a4a32;border-radius:9px;padding:1px 7px;font-size:11px;color:#8fb596;font-family:Consolas,'Cascadia Mono',monospace;background:#0b1a10;flex:none}
.srow.cam .fmt{border-color:#39ff14;color:#cfffd2}
.srow.busy{opacity:.5;pointer-events:none}
.rl{width:12px;height:12px;border-radius:50%;background:#3a1414;border:1px solid #5a2020;flex:none}
.on .rl{background:#ff3030;border-color:#ff8080;animation:pulse 2.4s ease-in-out infinite}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.25}}
.body{padding:6px 8px 8px}
.g3{display:grid;grid-template-columns:repeat(3,1fr);gap:4px}.g3+.g3{margin-top:4px}
.tile{background:#0b1a10;border:1px solid #16301e;border-radius:6px;padding:5px 7px;min-width:0;overflow:hidden}
.tile.k{border-color:#1f3a26}.tile.k span{font-size:14px;color:#fff}
.tile b{display:block;font-size:9px;letter-spacing:1px;text-transform:uppercase;color:#6f8f77;font-weight:600}
.tile span{font-family:Consolas,'Cascadia Mono',monospace;font-size:12px;color:#d6e8d9;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;display:block}
.tile.dim span{color:#7fae86}.tile.warn span{color:#ff8a8a}
.meter{height:6px;background:#06100a;border:1px solid #1f3a26;border-radius:3px;overflow:hidden;margin-top:4px}
.meter>div{height:100%;background:#39ff14;border-radius:2px}
.meter.mid>div{background:#e8d040}.meter.low>div{background:#ff5555}
.bars{display:flex;align-items:flex-end;gap:3px;height:14px;margin-top:4px}.bars i{display:block;width:6px;background:#16301e;border-radius:1px}
.bars i.on{background:#39ff14}.bars.weak i.on{background:#e8d040}
.badge{display:inline-flex;align-items:center;gap:5px;margin-top:4px;padding:1px 8px 1px 6px;border-radius:9px;font-size:11px;font-weight:600;border:1px solid #39ff14;color:#cfffd2;background:#0b1a10}
.badge::before{content:'';width:6px;height:6px;border-radius:50%;background:#39ff14}
.badge.mid{color:#e8d040;border-color:#7d6e2e}.badge.mid::before{background:#e8d040}
.badge.bad{color:#ff6b6b;border-color:#7d2e2e}.badge.bad::before{background:#ff5555;animation:pulse 1s ease-in-out infinite}
.notice{display:flex;align-items:center;gap:8px;margin:6px 0 0;padding:6px 9px;border:1px solid #7d6e2e;border-left:3px solid #e8d040;border-radius:6px;background:#1a1808;color:#e8d8a0;font-size:12px}
.notice .ni{flex:none;width:16px;height:16px;line-height:16px;text-align:center;border-radius:50%;background:#e8d040;color:#1a1808;font-weight:bold;font-size:11px}
.notice.bad{border-color:#7d2e2e;border-left-color:#ff5555;background:#1a0808;color:#ffb0b0}.notice.bad .ni{background:#ff5555;color:#1a0808}
.notice code{font-family:Consolas,'Cascadia Mono',monospace;color:#fff3b0}
.notice button{margin:0 0 0 auto;padding:4px 10px;font-size:12px;width:auto}
.rec{background:#140707;border:1px solid #3a1f1f;border-radius:8px;padding:8px 10px;margin:0 0 6px}
.rec .inc{background:#b00020;border:1px solid #ff6b6b;border-radius:4px;padding:8px 10px;margin:0 0 8px;color:#fff;font-weight:bold;font-size:13px}
.rec .pl{background:#7a3a00;border:1px solid #ffb040;border-radius:4px;padding:8px 10px;margin:0 0 8px;color:#fff;font-weight:bold;font-size:13px}
.rec .inc button,.rec .pl button{background:#fff;color:#7a3a00;border:0;font-weight:bold;margin-top:6px}.rec .inc button{color:#b00020}
.rec button.save{background:#5a1010;color:#ffe0e0;border:1px solid #a03030;font-weight:bold;margin:0 0 4px}
.rec .info{font-size:11px;color:#c9b0b0}
.grp{background:#071109;border:1px solid #1f3a26;border-radius:8px;padding:6px 8px 3px;margin:0 0 6px}
.grp h4{margin:0 0 4px 2px;font-size:10px;font-weight:600;letter-spacing:1px;color:#7fae86}
.svc{display:flex;align-items:center;gap:7px;border:1px solid #2a4a32;border-radius:12px;padding:3px 9px;margin:0 0 4px;font-family:Consolas,'Cascadia Mono',monospace;font-size:11px;color:#6e9a76;background:#0b1a10}
.svc:before{content:'';width:7px;height:7px;border-radius:50%;background:#555;flex:none}
.svc b{font-weight:400;color:#8fb596;min-width:34px}.svc code{font-family:inherit;color:inherit;user-select:all;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.svc span{margin-left:auto;color:#7f9a86;font-family:'Segoe UI',sans-serif;flex:none}
.svc.on{border-color:#39ff14;color:#cfffd2}.svc.on:before{background:#39ff14}
.svc.q{border-color:#7d6e2e;color:#e8d8a0}.svc.q:before{background:#e0b030}
.bar{position:relative;display:flex;justify-content:space-between;align-items:center;height:46px;padding:0 10px 0 16px;background:#071109;border:1px solid #1f3a26;border-radius:8px;margin:0 0 6px;cursor:pointer;user-select:none;overflow:hidden}
.bar:before{content:'';position:absolute;left:0;top:9px;bottom:9px;width:4px;border-radius:0 2px 2px 0;background:#16301e}
.bar .n{font-size:14px;font-weight:500;color:#d8f5dc}.bar .s{font-size:11px;letter-spacing:.5px;color:#7fae86;font-family:Consolas,'Cascadia Mono',monospace}
.bar.on:before{background:#39ff14}.bar.on .s{color:#39ff14}
.bar.dis,.sw.dis{opacity:.45}.pk.dis{opacity:.5}
.dr{background:#071109;border:1px solid #1f3a26;border-radius:8px;margin:0 0 6px}
.dh{display:flex;justify-content:space-between;align-items:center;padding:10px;background:#0a160d;color:#a8f0b0;font-weight:600;letter-spacing:1px;font-size:13px;cursor:pointer;user-select:none;border-radius:7px 7px 0 0}
.dh.c{border-radius:7px}
.dh i{font-style:normal;color:#a8f0b0}
.db{padding:6px 8px 4px;border-top:1px solid #1f3a26}
.sw{position:relative;display:flex;justify-content:space-between;align-items:center;gap:8px;min-height:40px;border:1px solid #142a1a;border-radius:6px;background:#0a160d;padding:7px 9px 7px 15px;margin:0 0 4px;cursor:pointer;user-select:none;overflow:hidden}
.sw:before{content:'';position:absolute;left:0;top:7px;bottom:7px;width:4px;border-radius:0 2px 2px 0;background:#16301e}
.sw .n{font-size:12px;color:#d8f5dc;line-height:1.3}.sw .s{font-size:11px;color:#7fae86;font-family:Consolas,'Cascadia Mono',monospace;flex:none}
.sw.on:before{background:#39ff14}.sw.on .s{color:#39ff14}
.fld{border:1px solid #142a1a;border-radius:6px;background:#0a160d;padding:6px 9px 7px;margin:0 0 4px}
.fld b{display:block;font-size:9px;letter-spacing:1px;text-transform:uppercase;color:#6f8f77;font-weight:600}
.fld code{display:block;font-family:Consolas,'Cascadia Mono',monospace;font-size:12px;color:#cfffd2;margin-top:3px;word-break:break-all;user-select:all;white-space:pre-wrap}
.fld .nt{font-size:11px;color:#b9cfbd;margin-top:3px;white-space:pre-line}
.pk{position:relative;cursor:pointer;user-select:none}.pk.open{border-color:#39ff14;z-index:30}
.pk .pv{display:flex;justify-content:space-between;align-items:center;font-family:Consolas,'Cascadia Mono',monospace;font-size:14px;color:#cfffd2;margin-top:3px}
.pk .pv i{font-style:normal;color:#3fbf5f}
.pk .list{display:none;position:absolute;left:-1px;right:-1px;top:100%;margin-top:3px;background:#071109;border:1px solid #39ff14;border-radius:6px;z-index:20;max-height:320px;overflow-y:auto;box-shadow:0 8px 20px rgba(0,0,0,.7)}
.pk.open .list{display:block}
.pk .list div{position:relative;padding:9px 12px 9px 16px;font-family:Consolas,'Cascadia Mono',monospace;font-size:13px;color:#9cc3a2;border-bottom:1px solid #0e2014}
.pk .list div:last-child{border-bottom:0}.pk .list div:hover{background:#0e2014;color:#cfffd2}
.pk .list div.a{background:#163a1e;color:#cfffd2}.pk .list div.a:before{content:'';position:absolute;left:0;top:9px;bottom:9px;width:3px;background:#39ff14;border-radius:0 2px 2px 0}
.sl .t{display:flex;justify-content:space-between;align-items:center}
.sl .val{display:flex;align-items:baseline;gap:6px;margin-top:3px}
.sl .val em{font-style:normal;font-family:Consolas,'Cascadia Mono',monospace;font-size:14px;color:#fff}
.sl .val small{font-size:10px;color:#7fae86;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.seg{display:inline-flex;border:1px solid #1f3a26;border-radius:10px;overflow:hidden;font-size:10px;cursor:pointer}
.seg span{padding:2px 9px;color:#7fae86}.seg span.a{background:#1f3a26;color:#cfffd2}
input[type=range]{width:100%;accent-color:#39ff14;margin:8px 0 0}input[type=range]:disabled{opacity:.35}
.sl.off .val em{color:#7fae86}
input[type=text],input[type=password],input:not([type]){background:#06100a;color:#d6e8d9;border:1px solid #1f3a26;border-radius:6px;padding:7px 9px;font-size:13px;width:100%;box-sizing:border-box;margin:4px 0 0}
button{background:#0e2a14;color:#e6ffe8;border:1px solid #2e7d46;border-radius:6px;padding:8px 12px;font-size:13px;cursor:pointer;width:100%;margin:4px 0 0}
button:hover:not(:disabled){background:#18452b}button:disabled{opacity:.35}
button.danger{background:#3a1010;color:#ffd0d0;border-color:#a03030}
.two{display:flex;gap:6px}.two button{flex:1}
.tip{display:inline-block;width:14px;height:14px;line-height:13px;text-align:center;border-radius:50%;border:1px solid #3fbf5f;color:#3fbf5f;font-size:9px;font-style:normal;font-weight:bold;cursor:help;margin-left:6px;vertical-align:middle;flex:none}
#tipbox{position:fixed;display:none;max-width:260px;background:#0b1a10;border:1px solid #2e7d46;color:#d6e8d9;padding:8px 10px;border-radius:6px;font-size:12px;line-height:1.45;z-index:50;box-shadow:0 6px 16px rgba(0,0,0,.7);pointer-events:none}
.bad{color:#ff6b6b;font-size:12px;font-weight:bold;margin:2px 0 4px}
#msg{color:#e9a;font-size:12px;min-height:16px;margin:4px 2px}
.lg{font-family:Consolas,'Cascadia Mono',monospace;font-size:11px;color:#b9cfbd;white-space:pre-wrap;word-break:break-word;max-height:300px;overflow:auto}
table.files{width:100%;border-collapse:collapse;font-size:12px;margin-top:4px}table.files td{padding:5px 4px;border-top:1px solid #142a1a;vertical-align:top}
table.files td.s{text-align:right;color:#7fae86;white-space:nowrap}table.files td.a a{margin-left:8px;font-size:11px}table.files a.x{color:#ff8a8a}table.files a.k{color:#e8d040}
.tag{font-size:10px;color:#e8d040}.tag.r{color:#ff8a8a}
</style></head><body>

<div class='card'>
 <div class='ttl'><b>W10MDashcam</b><i id='ver'></i></div>
 <div class='srow' id='srow'><span class='rl'></span><b id='rstate'>START RECORDING</b><i class='rt' id='rtime'></i><span class='fmt' id='fmt'>-</span></div>
 <div class='body'>
  <div class='g3' id='tiles'></div>
  <div id='warn'></div>
 </div>
</div>
<div class='notice' id='ro' style='display:none'><span class='ni'>i</span><span id='rotext'>View only.</span><button id='loginb'>Log in</button></div>
<button id='eng' style='display:none;margin:0 0 6px' class='danger'>Return to normal mode (low-latency engine)</button>

<div class='rec'>
 <div class='inc' id='rinc' style='display:none'><span id='rinct'></span><button id='rres'>RESUME RECORDING</button></div>
 <div class='pl' id='rpls' style='display:none'><span id='rplt'></span></div>
 <button class='save' id='rsave' data-tip='Keeps the file being recorded, the one before it and (when Save clip also keeps the next file is on) the next one in the Saved folder, where loop recording never deletes them.'>SAVE CLIP</button>
 <div class='info' id='rinfo'></div>
</div>

<div class='bar' id='bpv'><span class='n'>Preview</span><span class='s' id='bpvs'>OFF</span></div>
<div class='bar' id='bss'><span class='n'>Screensaver</span><span class='s' id='bsss'>ON</span></div>
<div class='bar' id='btt'><span class='n'>Flashlight</span><span class='s' id='btts'>OFF</span></div>

<div class='dr'><div class='dh' data-f='recopt'>RECORDING OPTIONS<i>&#x25B8;</i></div><div class='db' id='recopt'>
 <div class='sw' data-k='recordonopen' id='roo'><span class='n'>Record when the app opens</span><span class='s'>ON</span></div>
 <div class='fld pk' id='rpl' data-k='powerloss'><b>Stop when the power is lost<i class='tip' data-tip='When the charger is disconnected (the car is switched off), a countdown shows on the phone. When it ends, recording and live view stop so the battery is not run flat; they start again when the power returns.'>i</i></b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='fld pk' id='rseg' data-k='segment'><b>New file every</b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='fld pk' id='rimp' data-k='impact'><b>Crash detection<i class='tip' data-tip='On a jolt above the chosen level the current file, the one before and the next one are saved. Taps on the phone screen within 2 s do not count.'>i</i></b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='fld'><b>G-sensor</b><div class='nt' id='rg'></div></div>
 <div class='sw' data-k='keepnext' id='rkn'><span class='n'>Save clip also keeps the next file</span><span class='s'>ON</span></div>
 <div class='sw' data-k='gps' id='rgo'><span class='n'>GPS speed and position</span><span class='s'>ON</span></div>
 <div class='fld'><b>GPS</b><div class='nt' id='rgps'></div></div>
 <div class='fld pk' id='runits' data-k='units'><b>Speed in</b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='sw' data-k='subtitles' id='rsub'><span class='n'>Time, speed and position in recordings<i class='tip' data-tip='A subtitle track inside each recording: date and time, speed, position and IMPACT markers. VLC and most players show it over the video (Subtitle menu).'>i</i></span><span class='s'>ON</span></div>
</div></div>

<div class='dr'><div class='dh' data-f='storage'>STORAGE<i>&#x25B8;</i></div><div class='db' id='storage'>
 <div class='fld'><div class='nt' id='rstor'></div></div>
 <div class='fld pk' id='rwhere' data-k='location'><b>Save recordings to<i class='tip' data-tip='Where new recordings go. Automatic: the SD card when there is one, the phone storage when it is missing or full. A folder of your own is chosen on the phone (Storage > Choose folder).'>i</i></b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='fld pk' id='rlim' data-k='storage'><b>Space for loop recording<i class='tip' data-tip='Loop recording deletes its oldest files to stay within its space. Saved clips are never deleted by it. Automatic: a small reserve stays free and loop recording gets the rest.'>i</i></b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='fld pk' id='rsl' data-k='savedlimit'><b>Space for saved clips<i class='tip' data-tip='Saved clips get about a fifth of the storage (1 to 8 GB) on Automatic.'>i</i></b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='sw' data-k='replacesaved' id='rrs'><span class='n'>Replace the oldest saved clips when full</span><span class='s'>OFF</span></div>
 <div class='two'><button id='rref'>Refresh</button><button id='rdel' class='danger'>Delete all...</button></div>
 <div class='sw' id='rdels'><span class='n'>Delete all: saved clips too<i class='tip' data-tip='Formatting the SD card is only possible on the phone (Storage > Format SD).'>i</i></span><span class='s'>OFF</span></div>
 <div class='fld'><b>Recordings here<i class='tip' data-tip='Tap a name to play it. Captions: the time, speed and position as an .srt file (keep it next to the video, with the same name). Keep moves a recording to Saved; Unlock gives it back to loop recording.'>i</i></b><table class='files' id='rlist'></table></div>
</div></div>

<div class='dr'><div class='dh' data-f='video'>VIDEO<i>&#x25B8;</i></div><div class='db' id='video'>
 <div class='bad' id='ta3' style='display:none'>Heat protection: 1080p / 6 Mbps</div>
 <div class='fld pk' id='fsel' data-k='format'><b>Resolution</b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='fld pk' id='bsel' data-k='bitrate'><b>Bitrate</b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='sw' data-k='highbw' id='hb'><span class='n'>High bandwidth (over 24 Mbps)<i class='tip' data-tip='Unlocks bitrates up to 60 Mbps for the live view. Needs 5 GHz Wi-Fi or USB; 2.4 GHz drops frames.'>i</i></span><span class='s'>OFF</span></div>
 <div class='fld pk' id='ksel' data-k='keyframe'><b>Keyframe every</b><div class='pv'><span>-</span><i>&#x25BE;</i></div><div class='list'></div></div>
 <div class='sw' data-k='audio' id='au'><span class='n'>Microphone audio (AAC)</span><span class='s'>OFF</span></div>
</div></div>

<div class='dr'><div class='dh' data-f='camera'>CAMERA<i>&#x25B8;</i></div><div class='db' id='camera'>
 <div class='sw' id='all'><span class='n'>Automatic focus, exposure and white balance</span><span class='s'>ON</span></div>
 <div class='sw' id='so'><span class='n' id='sol'>Image stabilization<i class='tip' data-tip='Off on a fixed mount saves processing and heat. Changing it restarts the camera once.'>i</i></span><span class='s'>ON</span></div>
 <div class='sw' id='fo'><span class='n'>Focus once and lock<i class='tip' data-tip='Off: autofocus once and lock the lens there. On (locked, or held by the power-saving hold): press to go back to continuous autofocus.'>i</i></span><span class='s'>OFF</span></div>
 <div class='fld sl' id='fsl' style='margin-top:4px'><div class='t'><b>Focus</b><span class='seg' id='fseg'><span>Auto</span><span>Manual</span></span></div><div class='val'><em id='fv'>-</em><small id='fn'></small></div><input type='range' id='fs' min='0' max='100' step='1'></div>
 <div class='fld sl' id='esl'><div class='t'><b>Exposure</b><span class='seg' id='eseg'><span>Auto</span><span>Manual</span></span></div><div class='val'><em id='ev'>-</em><small id='en'></small></div><input type='range' id='es' min='0' max='100' step='0.5'></div>
 <div class='fld sl' id='wsl'><div class='t'><b>White balance</b><span class='seg' id='wseg'><span>Auto</span><span>Manual</span></span></div><div class='val'><em id='wv'>-</em><small id='wn'></small></div><input type='range' id='ws' min='2000' max='8000' step='100'></div>
</div></div>

<div class='dr'><div class='dh' data-f='streaming'>STREAMING<span id='sths' style='margin-left:auto;margin-right:10px;font-weight:400;color:#7fae86'>OFF</span><i>&#x25B8;</i></div><div class='db' id='streaming'>
<div class='g3'><div class='tile k dim'><b>FPS</b><span id='tfps'>-</span></div><div class='tile k dim'><b>MBPS</b><span id='tmbps'>-</span></div><div class='tile k dim'><b>VIEWERS</b><span id='tview'>-</span></div></div>
<div class='bar' id='blv'><span class='n'>Live view (RTSP)</span><span class='s' id='blvs'>OFF</span></div>

<div class='grp'><h4>RTSP LIVE VIEW</h4>
 <div class='svc' id='rw'><b>Wi-Fi</b><code id='rwa'></code><span id='rwn'></span></div>
 <div class='svc' id='ru'><b>USB</b><code id='rua'></code><span id='run'></span></div>
</div>
<div class='grp'><h4>WEB UI</h4>
 <div class='svc' id='ww'><b>Wi-Fi</b><code id='wwa'></code><span id='wwn'></span></div>
 <div class='svc' id='wu'><b>USB</b><code id='wua'></code><span id='wun'></span></div>
</div>
<div class='sw' data-k='liveonopen' id='ast'><span class='n'>Live stream when the app opens</span><span class='s'>OFF</span></div>
</div></div>

<div class='dr'><div class='dh' data-f='power'>POWER<i>&#x25B8;</i></div><div class='db' id='power'>
 <div class='sw' data-k='heatprotect' id='hp'><span class='n'>Heat protection<i class='tip' data-tip='When charging pauses on a good charger the phone is hot: 1080p / 6 Mbps, screensaver on, flashlight locked, until it charges normally again. Off: nothing is changed, and on a weak charger the battery drains and the phone dies.'>i</i></span><span class='s'>ON</span></div>
 <div class='bad' id='ta2' style='display:none'></div>
 <div class='sw' data-k='keeppreview' id='kp'><span class='n'>Preview on always<i class='tip' data-tip='Off by default. On: the preview runs even behind the screensaver, so a tap shows the picture at once. Costs CPU and heat; heat protection switches it off.'>i</i></span><span class='s'>OFF</span></div>
</div></div>

<div class='dr'><div class='dh' data-f='net'>NETWORK AND LOGIN<i>&#x25B8;</i></div><div class='db' id='net'>
 <div class='sw on dis' id='web'><span class='n'>Web page (settings and recordings)<i class='tip' data-tip='This page. It can only be switched off on the phone (the page would cut itself off).'>i</i></span><span class='s'>ON</span></div>
 <div class='fld'><b>Web page</b><code id='webaddr'></code></div>
 <div class='sw' data-k='log' id='lgo'><span class='n'>Log and stats pages (<a href='/log' target='_blank'>/log</a> &middot; <a href='/stats' target='_blank'>/stats</a>)<i class='tip' data-tip='The app log and graphs of the last 12 hours.'>i</i></span><span class='s'>OFF</span></div>
 <div class='fld'><b>Live view (RTSP, for VLC or an NVR)</b><code id='rtspaddr'></code></div>
 <div class='sw' data-k='udp' id='udp'><span class='n'>Allow RTP over UDP<i class='tip' data-tip='Off: viewers get the stream over TCP, which is more reliable on Wi-Fi.'>i</i></span><span class='s'>OFF</span></div>
 <div class='sw' data-k='snapshot' id='snap'><span class='n'>Snapshots at /snapshot.jpg</span><span class='s'>OFF</span></div>
 <div class='fld'><b>Login (stream and web page)</b><div id='lmsg' style='font-size:12px;margin-top:3px'>One login protects the stream and this page.<i class='tip' data-tip='Use the new login in your NVR / VLC: rtsp://USER:PASSWORD@phone-address:8554/live. The browser asks you to sign in again after a change.'>i</i></div>
  <input id='lc' type='password' placeholder='Current password'><input id='lu' placeholder='User name (admin)'><input id='lp' type='password' placeholder='New password'><input id='lp2' type='password' placeholder='Repeat password'>
  <button id='lb'>Save login</button></div>
</div></div>

<div class='dr'><div class='dh' data-f='viewer'>VIEWER SETUP<i>&#x25B8;</i></div><div class='db' id='viewer'>
 <div style='font-size:11px;color:#7fae86;margin:0 2px 6px;line-height:1.4'>The live view is standard RTSP (H.264). Wi-Fi addresses work from any device on the same network; USB addresses (127.0.0.1) only on the PC the phone is plugged into.</div>
 <div class='fld'><b>Stream addresses</b><code id='vaddr'></code></div>
 <div class='fld'><b>NVR (Blue Iris, Frigate, Synology, ...)</b><code id='vnvr'></code></div>
 <div class='fld'><b>VLC</b><code id='vvlc'></code></div>
 <div class='fld'><b>ffplay (quick test)</b><code id='vffp'></code></div>
 <div class='fld'><b>Snapshot (still picture)</b><code id='vsnap'></code></div>
</div></div>

<div class='dr'><div class='dh' data-f='details'>DETAILS AND LOG<i>&#x25B8;</i></div><div class='db' id='details'>
 <div class='fld'><b>Now</b><code id='now'></code></div>
 <div class='fld'><b>Log (last lines)</b><div class='lg' id='logtail'>-</div></div>
</div></div>
<div id='msg'></div><div id='tipbox'></div>
<script>
var st=null,touched=0,t=null;
function $(i){return document.getElementById(i)}
function esc(t){return String(t==null?'':t).replace(/[&<>""']/g,function(c){return '&#'+c.charCodeAt(0)+';'})}
function e2s(ms){return 100*Math.log(ms/st.expMin)/Math.log(st.expMax/st.expMin)}
function s2e(v){return st.expMin*Math.pow(st.expMax/st.expMin,v/100)}
function mb(b){return b>=1073741824?(b/1073741824).toFixed(1)+' GB':b>0?Math.max(1,Math.round(b/1048576))+' MB':'0 MB'}
function hms(s){s=Math.floor(s||0);var m=Math.floor(s/60);s=s%60;return m+':'+(s<10?'0':'')+s}
function fmin(m){return m>=120?(Math.round(m/6)/10)+' h':Math.round(m)+' min'}
function sw(id,on,dis,txt){var e=$(id);e.className='sw'+(on?' on':'')+(dis?' dis':'');e.lastElementChild.textContent=txt||(on?'ON':'OFF')}
function bar(id,on,dis,txt){var e=$(id);e.className='bar'+(on?' on':'')+(dis?' dis':'');e.lastElementChild.textContent=txt}
function svc(id,cls,addr,note){$(id).className='svc'+(cls?' '+cls:'');$(id+'a').textContent=addr;$(id+'n').textContent=note||''}
function seg(id,auto){var s=$(id).children;s[0].className=auto?'a':'';s[1].className=auto?'':'a'}
function meter(p,cls){p=Math.max(0,Math.min(100,p||0));return ""<div class='meter ""+(cls||'')+""'><div style='width:""+p+""%'></div></div>""}
function tile(l,v,w,g,tip){return ""<div class='tile""+(w?' warn':'')+""'""+(tip?"" data-tip='""+esc(tip)+""'"":'')+'><b>'+esc(l)+'</b>'+(v===''?'':'<span>'+esc(v)+'</span>')+(g||'')+'</div>'}
function badge(t,c){return ""<div class='badge ""+(c||'')+""'>""+esc(t)+'</div>'}
// a picker tile: the current value on the tile, the themed list below it while open; a tap on a row calls onsel(value)
function pick(id,vals,txt,cur,dis,onsel){
 var e=$(id),l=e.querySelector('.list'),key=vals.join('|')+'#'+cur;
 e.querySelector('.pv span').textContent=txt(cur);
 e.className='fld pk'+(e.className.indexOf('open')>=0?' open':'')+(dis?' dis':'');
 if(l.dataset.k===key)return;l.dataset.k=key;l.innerHTML='';
 vals.forEach(function(v){var o=document.createElement('div');o.textContent=txt(v);if(String(v)===String(cur))o.className='a';
  o.onclick=function(ev){ev.stopPropagation();e.className='fld pk';onsel(v)};l.appendChild(o)})
}
document.addEventListener('click',function(ev){var ps=document.querySelectorAll('.pk');for(var i=0;i<ps.length;i++){var p=ps[i];if(p.contains(ev.target)){if(ev.target.className==='tip')return;if(p.className.indexOf('dis')>=0){locked();return}p.className=p.className.indexOf('open')>=0?'fld pk':'fld pk open'}else p.className=p.className.replace(' open','')}});
var segs=[1,3,5,10],lims=[-1,4,8,16,32,64,0],slims=[-1,2,4,8,16,32,0],imps={off:'off',low:'low (2.5 g)',medium:'medium (1.6 g)',high:'high (1 g)'},wheres={auto:'automatic (SD card if present)',phone:'phone storage',sd:'SD card',custom:'chosen folder'};
function render(){
 if(!st||Date.now()-touched<2500)return;
 var w=st.canWrite,s=st.streaming,p=st.protect,r=st.recording||{},rec=r.state=='recording',live=!!st.live;
 if(st.version)$('ver').textContent='v'+st.version;
 var fm=/^(\d+)x(\d+)@(\d+)/.exec(st.format||''),res=fm?(fm[2]>=2160?'4K':fm[2]+'p')+fm[3]+(st.safeMode?' SAFE':''):'-';
 $('srow').className='srow'+(rec?' on':'')+(s?' cam':'')+(p?' hot':'')+(w?'':' busy');
 $('rstate').textContent=p?'HEAT PROTECTION':rec?'RECORDING':r.halted?'RECORDING STOPPED':!r.enabled?'START RECORDING':String(r.state||'').toUpperCase();$('rtime').textContent=rec?hms(r.seconds):'';$('fmt').textContent=res;
 $('tfps').textContent=live&&s?Math.round(st.fps):'-';$('tmbps').textContent=live&&s?(+st.mbps).toFixed(1):'-';$('tview').textContent=live&&s?st.viewers:'-';
 var kt=document.querySelectorAll('.tile.k');for(var i=0;i<kt.length;i++)kt[i].className='tile k'+(live&&s?'':' dim');
 var cpu=/phone (\d+) %, app (\d+)/.exec(st.cpuText||''),cpuP=cpu?+cpu[1]:0,cpuA=cpu?+cpu[2]:0,ml=st.memoryLimitMB||1536,mp=100*st.memoryMB/ml;
 var bp=st.battery==null?0:st.battery,bcls=st.batteryFalling||bp<20?'low':bp<40?'mid':'';
 var wb=st.wifiBars==null?0:st.wifiBars,bars=""<div class='bars""+(wb<2?' weak':'')+""'>"";
 for(var k=1;k<=4;k++)bars+=""<i class='""+(k<=wb?'on':'')+""' style='height:""+(2+k*3)+""px'></i>"";bars+='</div>';
 var nw=st.netWatts,pw=null,pl=st.charging?'Into battery':'Battery draw';
 if(!st.charging&&st.drawWatts!=null)pw=Math.abs(st.drawWatts);else if(nw!=null)pw=st.charging?nw:Math.abs(nw);
 var pv=pw==null?'measuring':(st.charging&&pw>=0?'+':'')+pw.toFixed(1)+' W';
 var pbar=pw==null?'':meter(100*Math.abs(pw)/15,st.charging?(pw<0?'low':pw<1?'mid':''):(pw>8?'low':'mid'));
 var gg=r.gps||{},spd=!gg.on?'GPS off':gg.speed!=null?gg.speed+' '+gg.unitText:'N/A',spp=gg.speed!=null?100*gg.speed/(gg.unitText=='mph'?80:130):0;
 var sused=(r.usedMB||0)+(r.savedUsedMB||0),sall=r.freeMB!=null?r.freeMB+sused:0;
 var sval=r.halted?'STOPPED':r.footageMin!=null?'~'+fmin(r.footageMin):r.freeMB!=null?mb(r.freeMB*1048576)+' free':'n/a';
 var swarn=r.halted||(r.footageMin!=null&&r.footageMin<30)||!!r.warning;
 $('tiles').innerHTML=tile('Battery',st.battery==null?'n/a':st.battery+'% '+st.batteryStatus.toLowerCase(),st.batteryFalling,meter(bp,bcls))
  +tile('Wi-Fi',st.wifiBars==null?'n/a':wb+' of 4',st.wifiBars!=null&&wb<2,bars)
  +tile('Temp','',false,badge(st.heatBadge||st.heat||'n/a',st.heatLevel==2?'bad':st.heatLevel==1?'mid':''),st.heatDetail||'Windows gives apps no temperature. Hot means charging has paused on a good charger for 20 s.')
  +tile('CPU phone',cpuP+'%',false,meter(cpuP,cpuP>70?'mid':''))
  +tile('CPU app',cpuA+'%',false,meter(cpuA,cpuA>50?'mid':''))
  +tile('Memory',st.memoryMB+' MB',mp>80,meter(mp,mp>80?'low':mp>60?'mid':''),'App memory, of '+ml+' MB allowed.')
  +tile('Speed',spd,false,meter(spp,gg.speed!=null?'':'mid'),'GPS speed, also written into the recordings.')
  +tile('Storage',sval,swarn,meter(sall>0?100*sused/sall:0,r.halted?'low':swarn?'mid':''),'How much footage loop recording keeps at this quality. The bar: space the recordings use. Details under Storage.')
  +tile(pl,pv,st.batteryFalling,pbar,(st.powerText||'')+' Bar scale: 0-15 W.');
 var nh='';
 if(st.heatWarn)nh+=""<div class='notice bad'><span class='ni'>!</span><span>""+esc(st.heatDetail||'Temperature unstable.')+'</span></div>';
 if(st.defaultLogin)nh+=""<div class='notice'><span class='ni'>!</span><span>Default login <code>admin / admin</code> in use. Change it under Network and login.</span></div>"";
 if(r.powerStopped)nh+=""<div class='notice bad'><span class='ni'>!</span><span>Stopped: no power. Recording and live view start again when the power returns.</span></div>"";
 if(st.chargerNotice)nh+=""<div class='notice'><span class='ni'>!</span><span>""+esc(st.chargerNotice)+'</span></div>';
 if(r.place=='Phone storage')nh+=""<div class='notice'><span class='ni'>!</span><span>Recording to the phone's internal storage""+(r.freeMB!=null?' ('+mb(r.freeMB*1048576)+' free'+(r.footageMin!=null?', keeps ~'+fmin(r.footageMin):'')+')':'')+'. An SD card is highly recommended: constant recording wears out the internal storage.</span></div>';
 $('warn').innerHTML=nh;
 $('ro').style.display=w?'none':'flex';$('rotext').textContent='View only. Changes need the stream login'+(st.defaultLogin?' (default admin / admin)':'')+'.';
 $('eng').style.display=st.safeMode?'block':'none';$('eng').disabled=!w;
 // the recording card
 $('rinc').style.display=r.halted?'block':'none';$('rinct').textContent=r.halted?'RECORDING IS NOT WORKING: '+(r.incident||'')+' - fix it, then resume':'';$('rres').disabled=!w;
 $('rpls').style.display=r.powerLossLeft!=null?'block':'none';$('rplt').textContent=r.powerLossLeft!=null?'Power lost: recording stops in '+r.powerLossLeft+' s unless the power comes back (Keep recording on the phone skips it).':'';
 var i=r.error&&r.state=='error'?r.error:((r.place||'')+(r.location?' · '+r.location.split(' (')[0]:'')+(r.freeMB!=null?' · '+mb(r.freeMB*1048576)+' free':'')+(r.footageMin!=null?' · keeps ~'+fmin(r.footageMin):''));
 if(r.dropped)i+=' · '+r.dropped+' frames dropped';if(rec&&r.writeMBps)i+=' · writing '+r.writeMBps.toFixed(1)+' MB/s';
 if(r.warning)i='WARNING: '+r.warning+' - '+i;
 $('rinfo').textContent=(r.enabled||r.halted)?i:'Recording is stopped';$('rinfo').style.color=(r.warning||r.halted)?'#ff8a8a':'';
 $('rsave').disabled=!w||!rec;
 // service rows, as on the phone
 var ip=st.wifiIp||'',usb='127.0.0.1:'+st.usbPort,seen=st.usbSeen,cable=st.usbCable,un=seen?'':cable?'cable?':'no USB',uc=seen?'on':cable?'q':'';
 var vt=function(n){return n>0?n+(n==1?' viewer':' viewers'):''};
 svc('rw',live&&s&&ip?'on':'',ip?'rtsp://'+ip+':'+st.rtspPort+st.streamPath:'',!ip?'no Wi-Fi':!live?'live view off':!s?'starting':vt(st.rtspViewersWifi));
 svc('ru',live&&s?uc:'','rtsp://'+usb+st.streamPath,!live?'live view off':!s?'starting':st.rtspViewersUsb>0?vt(st.rtspViewersUsb):un);
 svc('ww',ip?'on':'',ip?'http://'+ip+':'+location.port+'/camera':'',ip?'':'no Wi-Fi');
 svc('wu',uc,'http://'+usb+'/camera',un);
 $('rtspaddr').textContent=ip?'rtsp://'+(st.loginSet?'USER:PASSWORD@':'')+ip+':'+st.rtspPort+st.streamPath:'(no Wi-Fi)';
 $('webaddr').textContent=(ip?'http://'+ip+':'+location.port+'/camera\n':'')+'http://'+usb+'/camera (USB)';
 // the bars
 bar('blv',live,!w,!live?'OFF':!s?'ON · STARTING':st.viewers>0?'ON · '+vt(st.viewers).toUpperCase():'ON');$('sths').textContent=$('blvs').textContent;
 var pvOn=st.previewing||st.preview||st.keepPreview;
 bar('bpv',pvOn,!w,pvOn?'ON':'OFF');
 bar('bss',st.powerSave,!w||p,!st.powerSave?'OFF':p?'ON · HEAT PROTECTION':s?'ON':'ON · WHILE RECORDING');
 bar('btt',st.torchOn,!w||p||!st.torchSupported||!s,st.torchOn?'ON · '+st.torchLeftSec+' s':p?'OFF · HEAT PROTECTION':!st.torchSupported?'OFF · NOT ON THIS CAMERA':!s?'OFF · CAMERA FIRST':'OFF');
 // recording options and storage
 sw('roo',r.onOpen,!w);
 var pls=[30,60,120,300,0];if(r.powerLossSec!=null&&pls.indexOf(r.powerLossSec)<0)pls.push(r.powerLossSec);
 pick('rpl',pls,function(v){return v==0?'keep recording':'stop after '+(v<120?v+' s':v/60+' min')},r.powerLossSec,!w,function(v){send('powerloss='+(v==0?'off':v))});
 pick('rseg',segs,function(v){return v+' min'},r.segmentMin,!w,function(v){send('segment='+v)});
 pick('rimp',['off','low','medium','high'],function(v){return imps[v]},r.impact,!w,function(v){send('impact='+v)});
 $('rg').textContent=r.impactAvailable?('G-sensor now '+(+r.gNow).toFixed(2)+' g, highest in 10 s '+(+r.gPeak).toFixed(2)+' g'+(r.lastImpact?' - last impact '+r.lastImpact:'')):'No G-sensor on this phone';
 sw('rkn',r.keepNext,!w);var gp=r.gps||{};sw('rgo',!!gp.on,!w);
 $('rgps').innerHTML=!gp.on?'GPS is off':gp.fix?('GPS: '+(gp.speed!=null?'<b>'+gp.speed+' '+esc(gp.unitText)+'</b> - ':'')+(+gp.lat).toFixed(5)+', '+(+gp.lon).toFixed(5)+' (within '+gp.accuracy+' m) <a target=_blank href=https://www.openstreetmap.org/?mlat='+gp.lat+'&mlon='+gp.lon+'#map=16/'+gp.lat+'/'+gp.lon+'>map</a>'):('GPS: '+esc(gp.status||''));
 pick('runits',['auto','kmh','mph'],function(v){return v=='auto'?'automatic ('+(gp.unitText||'')+')':v=='kmh'?'km/h':'mph'},gp.units||'auto',!w,function(v){send('units='+v)});
 sw('rsub',!!gp.subtitles,!w);
 $('rstor').textContent=r.location?('Loop: '+r.files+' file'+(r.files==1?'':'s')+', '+mb(r.usedMB*1048576)+' of '+(r.loopLimitGB==null?'all free space':r.loopLimitGB+' GB')+(r.limitGB<0?' (auto)':'')
  +'\nSaved clips: '+r.saved+', '+mb((r.savedUsedMB||0)*1048576)+' of '+(r.savedLimitNowGB==null?'no limit':r.savedLimitNowGB+' GB')+(r.savedLimitGB<0?' (auto)':'')
  +(r.freeMB!=null?'\n'+mb(r.freeMB*1048576)+' free, '+mb(r.reserveMB*1048576)+' always kept free':'')
  +(r.footageMin!=null?'\nThe loop keeps about '+fmin(r.footageMin)+' of footage at this quality':'')):'The place is decided when recording starts.';
 pick('rwhere',['auto','phone','sd'].concat(r.customFolder?['custom']:[]),function(v){return wheres[v]},r.mode,!w,function(v){send('location='+v);setTimeout(recList,2500)});
 pick('rlim',lims,function(v){return v<0?'automatic':v?v+' GB':'all free space'},r.limitGB,!w,function(v){send('storage='+(v==0?'all':v==-1?'auto':v))});
 pick('rsl',slims,function(v){return v<0?'automatic':v?v+' GB':'no limit'},r.savedLimitGB,!w,function(v){send('savedlimit='+(v==0?'none':v==-1?'auto':v))});
 sw('rrs',r.replaceSaved,!w);$('rdel').disabled=!w;$('rref').disabled=false;
 // video
 $('ta3').style.display=p?'block':'none';
 pick('fsel',st.formats,function(v){return v.replace('x',' x ').replace('@',' @ ')+' fps'},st.format,!w||p,function(v){video('format',v)});
 pick('bsel',st.bitrates,function(v){return v=='auto'?'auto':v+' Mbps'},st.bitrate,!w||p,function(v){video('bitrate',v)});
 pick('ksel',['0.5','1','2','3','4'],function(v){return v+' s'},String(st.keyframeSec),!w||p,function(v){send('keyframe='+v)});
 sw('hb',st.highBw,!w||p);sw('au',st.audioOn,!w);
 // camera
 sw('all',st.focusAuto&&st.exposureAuto&&st.wbAuto,!w);
 sw('so',!st.saveOis,!w||p||!st.oisSupported,!st.oisSupported?'NOT ON THIS CAMERA':!st.saveOis?'ON':'OFF');
 var fon=st.focusHeld||st.focusLocked;sw('fo',fon,!w,st.focusHeld?'ON · HELD':fon?'ON · LOCKED':'OFF');
 seg('fseg',st.focusAuto);seg('eseg',st.exposureAuto);seg('wseg',st.wbAuto);
 $('fsl').className='fld sl'+(st.focusAuto?' off':'');$('esl').className='fld sl'+(st.exposureAuto?' off':'');$('wsl').className='fld sl'+(st.wbAuto?' off':'');
 $('fs').value=st.focus;$('es').value=Math.max(0,Math.min(100,e2s(st.exposureMs)));$('ws').min=st.wbMin;$('ws').max=st.wbMax;$('ws').value=st.wb;
 $('fs').disabled=!w||st.focusAuto;$('es').disabled=!w||st.exposureAuto;$('ws').disabled=!w||st.wbAuto;
 labels();
 // power
 sw('hp',st.heatProtect,!w);
 var hot=!p&&!st.heatProtect&&st.heatLevel>=2;
 $('ta2').textContent=p?'Heat protection: power saving forced on':'HOT and heat protection is off: nothing is done about it - the battery drains on a weak charger';$('ta2').style.display=p||hot?'block':'none';
 sw('kp',st.keepPreview,!w||p);
 // network
 sw('lgo',st.logOn,!w);sw('ast',st.liveOnOpen,!w);sw('udp',st.udp,!w);sw('snap',st.snapshot,!w);
 if(!$('lu').value&&document.activeElement!==$('lu'))$('lu').value=st.user||'admin';$('lb').disabled=!w;
 $('now').textContent=(st.mode||'')+' - '+(st.now||'');
 // viewer setup
 var lg=st.loginSet?'USER:PASSWORD@':'',wr=ip?'rtsp://'+lg+ip+':'+st.rtspPort+st.streamPath:'(no Wi-Fi)',ur='rtsp://'+lg+usb+st.streamPath,tr=st.udp?'':' -rtsp_transport tcp';
 $('vaddr').textContent='Wi-Fi  '+wr+'\nUSB    '+ur;
 $('vnvr').textContent='Switch Live view on, then add a camera of type RTSP / generic / FFmpeg with the address above.\nTransport: TCP'+(st.udp?' (UDP is allowed too)':'')+'. Login: the stream login'+(st.defaultLogin?' (admin / admin until changed)':'')+'.';
 $('vvlc').textContent='Media > Open Network Stream, paste the address.\nOr: vlc '+(st.udp?'':'--rtsp-tcp ')+(ip?wr:ur);
 $('vffp').textContent='ffplay'+tr+' -fflags nobuffer -flags low_delay '+(ip?wr:ur);
 $('vsnap').textContent=st.snapshot?('http://'+(ip?ip+':'+location.port:usb)+'/snapshot.jpg  (login needed)'):'Switch Snapshots on under Network and login; then http://'+(ip?ip+':'+location.port:usb)+'/snapshot.jpg';
}
function labels(){
 var iso=/ISO (\d+)/.exec(st.now||''),isoT=iso?' · ISO '+iso[1]:'';
 $('fv').textContent=st.focusAuto?'auto':Math.round($('fs').value);
 $('fn').textContent=st.focusHeld?'held, lens parked':st.focusAuto?'continuous':st.focusLocked?'locked · 0 near, 100 far':'0 near, 100 far';
 var ms=s2e($('es').value),mt=ms.toFixed(ms<10?2:1);
 $('ev').textContent=st.exposureAuto?'auto':mt+' ms';
 $('en').textContent=(st.exposureAuto?(+st.exposureMs).toFixed(st.exposureMs<10?2:1)+' ms (1/'+Math.round(1000/Math.max(0.001,st.exposureMs))+' s)':'1/'+Math.round(1000/ms)+' s')+isoT+(st.exposureAuto?' now':'');
 $('wv').textContent=st.wbAuto?'auto':$('ws').value+' K';
 $('wn').textContent=st.wbAuto?st.wb+' K now':'';
}
// view only (no login yet): any tap on a control goes to the login page instead of doing nothing
function locked(){if(st&&st.canWrite)return false;$('msg').textContent='View only - log in to change settings';location.href='/login';return true}
function send(body){if(locked())return;
 touched=Date.now();
 fetch('/camera/set',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded','X-Requested-With':'W10MDashcam'},body:body})
 .then(function(r){if(r.status==401){location.href='/login';throw new Error('Login needed')}return r.ok?r.json():r.text().then(function(x){throw new Error(x)})})
 .then(function(j){st=j;touched=0;render();if(j.result)$('msg').textContent=j.result})
 .catch(function(e){$('msg').textContent=e.message})
}
function load(){
 fetch('/camera/state').then(function(r){return r.json()}).then(function(j){
  if(st&&st.version&&j.version&&j.version!==st.version){location.reload();return}
  st=j;render()}).catch(function(){});
 if($('details').style.display!=='none')fetch('/log').then(function(r){return r.ok?r.text():''}).then(function(x){if(x){var l=x.trim().split('\n');$('logtail').textContent=l.slice(-25).join('\n')}}).catch(function(){});
}
function later(body){clearTimeout(t);t=setTimeout(function(){send(body)},400)}
function video(k,v){send(k+'='+v);$('msg').textContent='Applying - the camera restarts for a moment...'}
$('loginb').onclick=function(){location.href='/login'};
$('srow').onclick=function(){if(!st||st.canWrite){send('record='+(st&&st.recording&&st.recording.enabled?'off':'on'));setTimeout(recList,3000)}};
$('blv').onclick=function(){send('live='+(st&&st.live?'off':'on'))};
$('bpv').onclick=function(){var on=st&&(st.previewing||st.preview||st.keepPreview);send('preview='+(on?'off':'on'))};
$('bss').onclick=function(){send('powersave='+(st&&st.powerSave?'off':'on'))};
$('btt').onclick=function(){send('torch='+(st&&st.torchOn?'off':'on'))};
$('eng').onclick=function(){send('engine=normal')};
$('rres').onclick=function(){send('resume=1')};
$('so').onclick=function(e){if(e.target.className==='tip')return;send('saveois='+(st&&st.saveOis?'off':'on'))};
$('fo').onclick=function(e){if(e.target.className==='tip')return;if(st&&(st.focusHeld||st.focusLocked)){send('focus=auto')}else{$('msg').textContent='Focusing...';send('focus=once')}};
$('all').onclick=function(){var a=st.focusAuto&&st.exposureAuto&&st.wbAuto;send('camera='+(a?'manual':'auto'))};
$('fseg').onclick=function(){send('focus='+(st.focusAuto?'manual':'auto'))};
$('eseg').onclick=function(){send('exposure='+(st.exposureAuto?'manual':'auto'))};
$('wseg').onclick=function(){send('wb='+(st.wbAuto?'manual':'auto'))};
$('fs').oninput=function(){touched=Date.now();labels();later('focus='+this.value)};
$('es').oninput=function(){touched=Date.now();labels();later('exposure='+s2e(this.value).toFixed(3))};
$('ws').oninput=function(){touched=Date.now();labels();later('wb='+this.value)};
$('rdels').onclick=function(e){if(e.target.className==='tip')return;var on=this.className.indexOf(' on')>=0;this.className='sw'+(on?'':' on');this.lastElementChild.textContent=on?'OFF':'ON'};
// every switch row: data-k is the config key; the row's current state decides on/off
var rows=document.querySelectorAll('.sw[data-k]');
for(var i=0;i<rows.length;i++)rows[i].onclick=function(e){
 if(e.target.className==='tip'||e.target.tagName==='A')return;
 var on=(' '+this.className+' ').indexOf(' on ')>=0,k=this.getAttribute('data-k');
 if(k==='heatprotect'&&on&&!confirm('Turn heat protection off? A hot phone then keeps full video and the screen, and on a weak charger the battery drains until the phone dies.'))return;
 if(k==='audio'&&!on)$('msg').textContent='Audio on - the camera restarts; the phone may ask for microphone access.';
 send(k+'='+(on?'off':'on'))};
$('lb').onclick=function(){
 var u=$('lu').value.trim(),p=$('lp').value;
 if(p!==$('lp2').value){$('msg').textContent='The two passwords differ';return}
 if(p.length<8){$('msg').textContent='The password needs at least 8 characters';return}
 if(!$('lc').value){$('msg').textContent='Enter the current password too';return}
 var c=$('lc').value;$('lc').value=$('lp').value=$('lp2').value='';
 send('current='+encodeURIComponent(c)+'&user='+encodeURIComponent(u||'admin')+'&password='+encodeURIComponent(p));
 $('msg').textContent='Changing the login - if it worked, sign in again with the new login'};
// ---- recordings
function recList(){
 fetch('/recordings').then(function(r){return r.json()}).then(function(j){
  var h='';
  j.files.forEach(function(f){
   var q='/recordings/file?name='+encodeURIComponent(f.name)+(f.saved?'&saved=1':'');
   h+='<tr><td class=n><a style=margin:0 href='+q+' target=_blank>'+esc(f.name.replace('.mp4','').replace(/^\d{4}-(\d\d-\d\d)_(\d\d)-(\d\d)-(\d\d)/,'$1 $2:$3:$4'))+'</a>'
    +(f.saved||f.note?'<br><span class=tag>'+esc(f.note||'saved')+'</span>':'')+(f.recording?'<br><span class=""tag r"">recording</span>':'')+'</td>'
    +'<td class=s>'+mb(f.size)+'</td><td class=a><a href='+q+'&dl=1>Download</a><a class=c href='+q.replace('/file?','/srt?')+' title=""Captions (.srt): save next to the video with the same name"">Captions</a>'
    +(st&&st.canWrite?'<a class=k data-a='+(f.saved?'unlock':'lock')+' data-n='+encodeURIComponent(f.name)+'>'+(f.saved?'Unlock':'Keep')+'</a>':'')
    +(f.recording||!(st&&st.canWrite)?'':'<a class=x data-n='+encodeURIComponent(f.name)+' data-s='+(f.saved?1:0)+'>Delete</a>')+'</td></tr>';
  });
  $('rlist').innerHTML=h||'<tr><td style=text-align:left>No recordings yet</td></tr>';
 }).catch(function(){});
}
function recPost(path,body,msg){
 if(locked())return;
 fetch(path,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded','X-Requested-With':'W10MDashcam'},body:body||''})
 .then(function(r){return r.text().then(function(t){return {ok:r.ok,t:t}})}).then(function(x){$('msg').textContent=x.ok?(msg||x.t):x.t;setTimeout(function(){load();recList()},800)});
}
$('rsave').onclick=function(){recPost('/recordings/save','','Saving: the current file ends now (the one before it is kept too, and the next one when Save clip also keeps the next file is on); a new file starts with no gap')};
$('rref').onclick=recList;
$('rdel').onclick=function(){
 var s=$('rdels').className.indexOf(' on')>=0;
 var t=prompt('Delete ALL loop recordings'+(s?' AND all saved clips':' (saved clips are kept)')+' on the phone? This cannot be undone. Type DELETE to confirm.');
 if(t===null)return;
 if(t!=='DELETE'){$('msg').textContent='Nothing deleted (type DELETE in capitals)';return}
 $('msg').textContent='Deleting...';
 recPost('/recordings/deleteall','confirm=DELETE&saved='+(s?1:0),'')};
$('rlist').onclick=function(e){var a=e.target;if(!a.getAttribute||!a.getAttribute('data-n'))return;
 var act=a.getAttribute('data-a');
 if(act){var nm=decodeURIComponent(a.getAttribute('data-n'));
  if(act=='unlock'&&!confirm('Unlock '+nm+'? It goes back to loop recording and will be deleted when it is the oldest.'))return;
  recPost('/recordings/'+act,'name='+encodeURIComponent(nm),(act=='lock'?'Keeping ':'Unlocked ')+nm);return}
 var n=decodeURIComponent(a.getAttribute('data-n'));if(!confirm('Delete '+n+'?'))return;
 recPost('/recordings/delete','name='+encodeURIComponent(n)+'&saved='+a.getAttribute('data-s'),'Deleted '+n)};
function listShown(){return $('rlist').offsetParent!==null}
// drawers: the header band opens and closes the body; remembered in this browser (closed by default, as on the phone)
(function(){
 var saved={};try{saved=JSON.parse(localStorage.getItem('w10m-fold2')||'{}')}catch(e){}
 var hs=document.querySelectorAll('.dh');
 for(var i=0;i<hs.length;i++)(function(h){
  var name=h.getAttribute('data-f'),body=$(name);
  function show(open){body.style.display=open?'':'none';h.className='dh'+(open?'':' c');h.lastElementChild.textContent=open?'▾':'▸';if(open&&name==='details')load();if(open&&name==='storage')recList()}
  show(saved[name]===true);
  h.onclick=function(){var open=body.style.display==='none';show(open);saved[name]=open;try{localStorage.setItem('w10m-fold2',JSON.stringify(saved))}catch(e){}}
 })(hs[i]);
})();
// hover (or tap) anything with data-tip to see its note
(function(){
 var box=$('tipbox'),cur=null;
 function find(e){var n=e.target;while(n&&n!==document){if(n.getAttribute&&n.getAttribute('data-tip'))return n;n=n.parentNode}return null}
 function show(n){var t=n.getAttribute('data-tip');if(!t){hide();return}cur=n;box.textContent=t;box.style.display='block';
  var r=n.getBoundingClientRect(),w=box.offsetWidth,h=box.offsetHeight;
  var x=Math.max(8,Math.min(window.innerWidth-w-8,r.left+r.width/2-w/2)),y=r.bottom+6;
  if(y+h>window.innerHeight-8)y=Math.max(8,r.top-h-6);box.style.left=x+'px';box.style.top=y+'px'}
 function hide(){cur=null;box.style.display='none'}
 document.addEventListener('mouseover',function(e){var n=find(e);if(n)show(n);else if(cur)hide()});
 document.addEventListener('click',function(e){var n=find(e);if(n&&(' '+n.className+' ').indexOf(' tip ')>=0){show(n);e.preventDefault();e.stopPropagation()}else if(!n)hide()},true);
 window.addEventListener('scroll',hide,true);
})();
load();setInterval(load,3000);setInterval(function(){if(listShown())recList()},10000);
</script></body></html>";
    }
}
