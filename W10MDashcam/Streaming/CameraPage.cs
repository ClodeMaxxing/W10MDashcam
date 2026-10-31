namespace W10MDashcam.Streaming
{
    /// <summary>The small camera-settings web page (http://PHONE-IP:5001/camera). Plain HTML + a little script.</summary>
    internal static class CameraPage
    {
        public const string Html = @"<!doctype html>
<html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>W10MDashcam</title>
<style>
body{font-family:'Segoe UI',system-ui,sans-serif;background:#000;color:#d6e8d9;margin:0;padding:16px;max-width:480px}
h1{font-size:19px;margin:0 0 4px;display:flex;justify-content:space-between;align-items:baseline;color:#e4f5e7;letter-spacing:.3px}
#ver{font-size:12px;font-weight:bold;color:#39ff14;text-shadow:0 0 6px rgba(57,255,20,.45)}
.sub{color:#7f9a86;font-size:12px;margin-bottom:12px;word-break:break-word}
.row{margin:14px 0}.head{display:flex;justify-content:space-between;align-items:center;font-size:14px}
input[type=text],input[type=password],input:not([type]){background:#0d140f;color:#d6e8d9;border:1px solid #1f3a26;border-radius:4px;padding:6px;font-size:14px;width:46%;margin:2px 0}
input[type=range]{width:100%;accent-color:#3fbf5f}input[type=range]:disabled{opacity:.35}input[type=checkbox]{accent-color:#3fbf5f}
select{background:#0d140f;color:#d6e8d9;border:1px solid #1f3a26;border-radius:4px;padding:6px;font-size:14px;max-width:48%}
button{background:#123320;color:#cfffd8;border:1px solid #2e7d46;border-radius:6px;padding:8px 12px;font-size:14px;margin-top:6px;cursor:pointer}
button:hover:not(:disabled){background:#18452b;box-shadow:0 0 8px rgba(63,191,95,.35)}
button:disabled{opacity:.35}
label{font-size:13px;color:#b9cfbd}#msg{color:#e9a;font-size:12px;min-height:16px}
h2{font-size:13px;color:#9cf0b0;background:#0b1a10;text-transform:uppercase;letter-spacing:1px;border:1px solid #1f3a26;border-left:3px solid #3fbf5f;border-radius:4px;padding:8px 10px;margin:18px 0 6px;display:flex;justify-content:space-between;align-items:center;cursor:pointer;user-select:none}
h2 .ar{font-size:16px;color:#3fbf5f;padding:0 4px}
#stat{font-size:14px}
.card{background:linear-gradient(180deg,#07140b,#030805);border:1px solid #1f3a26;border-radius:8px;padding:10px 12px;margin-top:6px}
.top{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.dot{width:10px;height:10px;border-radius:50%;background:#555;flex:none}
.dot.on{background:#39ff14;box-shadow:0 0 8px #39ff14;animation:pulse 2s ease-in-out infinite}
.dot.hot{background:#ff5555;box-shadow:0 0 8px #ff5555;animation:pulse 1s ease-in-out infinite}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.35}}
.state{font-weight:600;letter-spacing:1px;font-size:13px;text-transform:uppercase;color:#9cf0b0}
.pill{font-size:11px;color:#9cf0b0;border:1px solid #2e7d46;border-radius:10px;padding:1px 8px;background:#0b1a10}
.pill.r{margin-left:auto}
.big{font-family:Consolas,'Cascadia Mono',monospace;font-size:22px;color:#e4f5e7;margin:8px 0 2px}
.big small{font-size:12px;color:#7f9a86;margin:0 10px 0 3px}
.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:6px;margin-top:8px}
.tile{background:#0b1a10;border:1px solid #16301e;border-radius:6px;padding:6px 8px;min-width:0}
.tile b{display:block;font-size:10px;letter-spacing:1px;text-transform:uppercase;color:#6f8f77;font-weight:600}
.tile span{font-family:Consolas,'Cascadia Mono',monospace;font-size:13px;color:#d6e8d9;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;display:block}
.tile.warn span{color:#ff8a8a}

.meter{height:6px;background:#06100a;border:1px solid #1f3a26;border-radius:3px;overflow:hidden;margin-top:4px}
.meter>div{height:100%;background:linear-gradient(90deg,#2fa84f,#39ff14);border-radius:2px}
.meter.mid>div{background:linear-gradient(90deg,#b8a02f,#e8d040)}.meter.low>div{background:linear-gradient(90deg,#a83232,#ff5555)}
.batt{display:flex;align-items:center;gap:2px;margin-top:4px}.batt .meter{flex:1;height:10px;margin:0;border-radius:3px}
.batt .cap{width:3px;height:5px;background:#2e7d46;border-radius:0 2px 2px 0}
.bars{display:flex;align-items:flex-end;gap:3px;height:14px;margin-top:4px}.bars i{display:block;width:6px;background:#16301e;border-radius:1px;margin:0}
.bars i.on{background:#39ff14;box-shadow:0 0 4px rgba(57,255,20,.5)}.bars.weak i.on{background:#e8d040;box-shadow:none}
.tile.warn .meter>div,.tile.warn .bars i.on{background:#ff5555;box-shadow:none}
.tile[data-tip]{cursor:help}
.notice{display:flex;align-items:center;gap:8px;margin-top:8px;padding:7px 10px;border:1px solid #7d6e2e;border-left:3px solid #e8d040;border-radius:6px;background:#1a1808;color:#e8d8a0;font-size:12px}
.notice .ni{flex:none;width:16px;height:16px;line-height:16px;text-align:center;border-radius:50%;background:#e8d040;color:#1a1808;font-weight:bold;font-size:11px}
.notice code{font-family:Consolas,'Cascadia Mono',monospace;color:#fff3b0}
.notice.bad{border-color:#7d2e2e;border-left-color:#ff5555;background:#1a0808;color:#ffb0b0}.notice.bad .ni{background:#ff5555;color:#1a0808}
.badge{display:inline-flex;align-items:center;gap:6px;margin-top:5px;padding:2px 9px 2px 7px;border-radius:11px;font-size:12px;font-weight:600;letter-spacing:.3px;border:1px solid #2e7d46;color:#39ff14;background:#0f2a17}
.badge::before{content:'';width:8px;height:8px;border-radius:50%;background:currentColor;box-shadow:0 0 6px currentColor}
.badge.mid{color:#e8d040;border-color:#7d6e2e;background:#2a2610}.badge.bad{color:#ff5555;border-color:#7d2e2e;background:#2a1010}
.badge.bad::before{animation:pulse 1s ease-in-out infinite}
.tip{display:inline-block;width:15px;height:15px;line-height:15px;text-align:center;border-radius:50%;border:1px solid #2e7d46;color:#3fbf5f;font-size:10px;font-style:normal;font-weight:bold;cursor:help;margin-left:6px;vertical-align:middle}
#tipbox{position:fixed;display:none;max-width:260px;background:#0b1a10;border:1px solid #2e7d46;color:#d6e8d9;padding:8px 10px;border-radius:6px;font-size:12px;line-height:1.45;z-index:50;box-shadow:0 6px 16px rgba(0,0,0,.7);pointer-events:none}
.vrow{display:flex;gap:6px;align-items:center;flex-wrap:wrap}.vrow select{max-width:none;flex:1 1 30%}
button.tog{margin-top:0;padding:6px 10px;font-size:13px;background:#0b1a10;color:#7f9a86;border-color:#1f3a26}
button.tog.on{background:#18452b;color:#39ff14;border-color:#39ff14;box-shadow:0 0 8px rgba(57,255,20,.35)}
.btnrow{display:flex;gap:6px;flex-wrap:wrap;align-items:center}
.modal{position:fixed;left:0;top:0;right:0;bottom:0;background:rgba(0,0,0,.75);display:none;align-items:center;justify-content:center;z-index:40;padding:16px}
.modal.open{display:flex}.mbody{background:#07140b;border:1px solid #2e7d46;border-radius:10px;padding:14px 16px;max-width:440px;width:100%;box-shadow:0 0 24px rgba(57,255,20,.15)}
.mbody h3{margin:0 0 10px;font-size:15px;color:#9cf0b0;letter-spacing:.5px}
.addr{margin:8px 0}.addr b{display:block;font-size:11px;letter-spacing:1px;text-transform:uppercase;color:#6f8f77}
.addr code{display:block;font-family:Consolas,'Cascadia Mono',monospace;font-size:13px;color:#e4f5e7;background:#030805;border:1px solid #16301e;border-radius:4px;padding:6px 8px;margin-top:3px;word-break:break-all;user-select:all}
.bad{color:#ff6b6b;font-size:13px;margin-top:4px}
.dot.rec{background:#ff3030;box-shadow:0 0 8px #ff3030;animation:pulse 1s ease-in-out infinite}
.state.rec{color:#ff8a8a}
button.save{background:#5a1010;color:#ffe0e0;border-color:#a03030;font-weight:bold;width:100%}
button.save:hover:not(:disabled){background:#7a1818;box-shadow:0 0 8px rgba(255,48,48,.4)}
button.danger{background:#3a1010;color:#ffd0d0;border-color:#a03030}button.danger:hover:not(:disabled){background:#5a1414;box-shadow:0 0 8px rgba(255,48,48,.35)}
.files{width:100%;border-collapse:collapse;font-size:12px;margin-top:6px}
.files td{border-top:1px solid #16301e;padding:5px 3px;vertical-align:middle}
.files td.n{font-family:Consolas,'Cascadia Mono',monospace;color:#e4f5e7;white-space:nowrap}
.files td.s{color:#7f9a86;white-space:nowrap;text-align:right}
.files td.a{text-align:right;line-height:1.8;border-top:1px solid #16301e;padding:5px 3px}.files td.a a{display:inline-block}
.files a{color:#9cf0b0;margin-left:6px}.files a.x{color:#ff8a8a;cursor:pointer}.files a.k{color:#e8d040;cursor:pointer}.files a.c{color:#7fc8f0}
.tag{font-size:10px;border-radius:8px;padding:0 6px;margin-left:0;border:1px solid #2e7d46;color:#39ff14;white-space:nowrap;display:inline-block;word-break:normal}
.tag.r{border-color:#a03030;color:#ff8a8a}
</style></head><body>
<h1><span>W10MDashcam</span><span id='ver'></span></h1>
<h2>Status</h2>
<div class='row'><div id='stat'>loading...</div><div id='warn'></div></div>
<h2>Recording</h2>
<div class='row'><div class='card'>
<div class='top'><span class='dot' id='rdot'></span><span class='state' id='rstate'>-</span><span class='pill r' id='rloc'></span></div>
<button class='save' id='rsave' data-tip='Keeps the file being recorded and the one before it in the Saved folder, where loop recording never deletes them.'>SAVE CLIP</button>
<div class='sub' id='rinfo' style='margin:6px 0 0'></div></div>
<div class='btnrow' style='margin-top:8px'><button id='rbtn'>Stop recording</button><label><input type='checkbox' id='roo'> Record when the app opens</label></div></div>
<h2>Recording options</h2>
<div class='row'>
<div class='vrow'><select id='rseg'></select><select id='rimp'></select><i class='tip' data-tip='Crash detection: on a jolt above the chosen level the current file, the one before and the next one are saved. Taps on the phone screen within 2 s do not count.'>i</i></div>
<div class='sub' id='rg' style='margin:4px 0 0'></div>
<div class='vrow' style='margin-top:6px'><select id='rpl'></select><i class='tip' data-tip='When the charger is disconnected (the car is switched off), a countdown shows on the phone. When it ends, recording and live view stop so the battery is not run flat; they start again when the power returns.'>i</i></div>
<div class='vrow' style='margin-top:6px'><label><input type='checkbox' id='rkn'> Save clip also keeps the next file</label></div>
<div class='vrow' style='margin-top:6px'><label><input type='checkbox' id='rgo'> GPS</label><select id='runits'></select><label><input type='checkbox' id='rsub'> Time, speed and position in recordings</label><i class='tip' data-tip='A subtitle track inside each recording: date and time, speed, position and IMPACT markers. VLC and most players show it over the video (Subtitle menu).'>i</i></div>
<div class='sub' id='rgps' style='margin:4px 0 0'></div></div>
<h2>Storage</h2>
<div class='row'>
<div class='sub' id='rstor' style='margin:0 0 8px;white-space:pre-line'></div>
<div class='vrow'><select id='rwhere'></select><i class='tip' data-tip='Where new recordings go. Automatic: the SD card when there is one, the phone storage when it is missing or full. A folder of your own is chosen on the phone (Storage > Choose folder). The list below shows the current place only.'>i</i></div>
<div class='vrow' style='margin-top:6px'><select id='rlim'></select><select id='rsl'></select><i class='tip' data-tip='Loop recording deletes its oldest files to stay within its space. Saved clips are never deleted by it. Automatic: saved clips get about a fifth of the storage (1 to 8 GB), a small reserve stays free, and loop recording gets the rest.'>i</i></div>
<div class='vrow' style='margin-top:6px'><label><input type='checkbox' id='rrs'> Replace the oldest saved clips when full</label></div>
<div class='btnrow' style='margin-top:8px'><button id='rref'>Refresh</button><button id='rdel' class='danger'>Delete all...</button><label><input type='checkbox' id='rdels'> saved clips too</label><i class='tip' data-tip='Formatting the SD card is only possible on the phone (Storage > Format SD): apps cannot format a card, so the phone opens its own Storage settings for it.'>i</i></div>
<div class='vrow' style='margin-top:8px'><span class='sub' style='margin:0'>Recordings here</span><i class='tip' data-tip='Tap a name to play it. Captions: the time, speed and position as an .srt file (keep it next to the video, with the same name). Keep moves a recording to Saved; Unlock gives it back to loop recording.'>i</i></div>
<table class='files' id='rlist'></table></div>
<h2>Video</h2>
<div class='bad' id='ta3' style='display:none;font-weight:bold'>Heat protection: 1080p / 6 Mbps</div>
<div class='row'>
<div class='vrow'><select id='fmt'></select><select id='br'></select>
<button id='hb' class='tog' data-tip='High bandwidth mode unlocks bitrates above 24 Mbps. Needs 5 GHz Wi-Fi or a USB-C connection; on 2.4 GHz the stream drops frames.'>High BW</button></div>
<div class='btnrow' style='margin-top:8px'><button id='ss'>Stop stream</button><button id='wa2'>Stream addresses</button><button id='eng' style='display:none'>Return to normal mode</button></div>
<div class='sub' id='safe' style='color:#e9a'></div></div>
<div class='modal' id='addrbox'><div class='mbody'><h3>Watch the stream (VLC, NVR)</h3><div id='addr'></div>
<div class='sub' style='margin:8px 0 0'>Put the stream login in place of USER:PASSWORD. The USB addresses work on the PC the phone is plugged into.</div>
<button id='addrclose'>Close</button></div></div>
<h2>Camera</h2><div class='sub' id='now'></div>
<div class='row'><label><input type='checkbox' id='all'> Automatic focus, exposure and white balance</label></div>
<div class='row'><div class='head'><span id='fl'>Focus</span><label><input type='checkbox' id='fa'> Auto</label></div>
<input type='range' id='fs' min='0' max='100' step='1'>
<button id='fo'>Focus once and lock</button></div>
<div class='row'><div class='head'><span id='el'>Exposure</span><label><input type='checkbox' id='ea'> Auto</label></div>
<input type='range' id='es' min='0' max='100' step='0.5'></div>
<div class='row'><div class='head'><span id='wl'>White balance</span><label><input type='checkbox' id='wa'> Auto</label></div>
<input type='range' id='ws' min='2000' max='8000' step='100'></div>
<div class='bad' id='ta1' style='display:none;font-weight:bold'>Heat protection: flashlight locked</div>
<div class='row'><div class='head'><span><span id='tl'>Flashlight</span><i class='tip' data-tip='Only while streaming. Switches off by itself after 30 seconds: the LED heats the phone quickly and this phone cannot dim it.'>i</i></span><label><input type='checkbox' id='tt'> On</label></div>
<div class='sub' id='tinfo'></div></div>
<h2>Power</h2>
<div class='bad' id='ta2' style='display:none;font-weight:bold'>Heat protection: power saving forced on</div>
<div class='row'><div class='btnrow'><button id='ps'>Black out the phone screen</button><i class='tip' id='pinfo' data-tip='x'>i</i></div></div>
<div class='row'><label><input type='checkbox' id='sall'> Power-saving camera features</label><i class='tip' data-tip='Less processing and heat: stabilization off for a fixed mount, and focus held once it has settled. Untick to choose each one. Changing stabilization restarts the stream once.'>i</i>
<div id='spanel' style='margin-left:18px'>
<div><label><input type='checkbox' id='so'> <span id='sol'>Stabilization off (fixed mount)</span></label></div>
<div><label><input type='checkbox' id='sf'> Hold focus once it has settled</label></div></div></div>
<h2>Network and login</h2>
<div class='row'><label><input type='checkbox' id='lgo'> Log and stats pages</label> <span id='lglinks'><a href='/log' target='_blank' style='color:#9cf0b0'>log</a> &middot; <a href='/stats' target='_blank' style='color:#9cf0b0'>stats</a></span><i class='tip' data-tip='The app log (/log) and graphs of the last 12 hours (/stats). The web page itself can only be switched off on the phone.'>i</i></div>
<div class='row'><div class='sub' style='margin-bottom:6px'><span id='lmsg'>One login protects the stream and this page.</span><i class='tip' data-tip='Use the new login in your NVR / VLC: rtsp://USER:PASSWORD@phone-address:8554/live. The browser asks you to sign in again after a change.'>i</i></div>
<input id='lc' type='password' placeholder='Current password'> <input id='lu' placeholder='User name'> <input id='lp' type='password' placeholder='New password'> <input id='lp2' type='password' placeholder='Repeat password'>
<button id='lb'>Change login</button></div>
<div id='msg'></div><div id='tipbox'></div>
<script>
var st=null,touched=0,t=null;
function $(i){return document.getElementById(i)}
function e2s(ms){return 100*Math.log(ms/st.expMin)/Math.log(st.expMax/st.expMin)}
function s2e(v){return st.expMin*Math.pow(st.expMax/st.expMin,v/100)}
function fill(sel,vals,txt){
 var key=vals.join('|');if(sel.dataset.k===key)return;sel.dataset.k=key;sel.innerHTML='';
 vals.forEach(function(v){var o=document.createElement('option');o.value=v;o.textContent=txt(v);sel.appendChild(o)})
}
function render(){
 if(!st||Date.now()-touched<2500)return;
 fill($('fmt'),st.formats,function(v){return v.replace('x',' x ').replace('@',' @ ')+' fps'});
 fill($('br'),st.bitrates,function(v){return v=='auto'?'Bitrate: auto':v+' Mbps'});
 $('fmt').value=st.format;$('br').value=st.bitrate;
 $('hb').className='tog'+(st.highBw?' on':'');$('hb').textContent=st.highBw?'High BW on':'High BW';
 $('fo').disabled=$('ps').disabled=!st.canWrite;
 $('ps').textContent=st.blackout?'Phone screen is black - wake it':'Black out the phone screen';
 $('fmt').disabled=$('br').disabled=$('hb').disabled=!st.canWrite;
 $('ss').textContent=st.live?'Stop live view':'Start live view';$('ss').disabled=!st.canWrite;
 $('eng').style.display=st.safeMode?'inline-block':'none';$('eng').disabled=!st.canWrite;
 $('safe').textContent=st.safeMode?'Safe mode: the app is using its fallback engine, limited to 1080p. Return to normal mode to use 4K.':'';
 var h=location.hostname,u=st.loginSet?'USER:PASSWORD@':'';
 var usb='127.0.0.1:'+st.usbPort;
 var ad=function(l,v){return ""<div class='addr'><b>""+l+'</b><code>'+v.replace(/&/g,'&amp;').replace(/</g,'&lt;')+'</code></div>'};
 $('addr').innerHTML=ad('Wi-Fi (RTSP)','rtsp://'+u+h+':'+st.rtspPort+st.streamPath)
  +ad('USB cable (on the PC it is plugged into)','rtsp://'+usb+st.streamPath)
  +ad('This page over USB','http://'+usb+'/camera')
  +ad('Live log (Wi-Fi)','http://'+location.host+'/log')
  +ad('Live log (USB)','http://'+usb+'/log');
 $('now').textContent=st.mode+' - '+st.now;
 var bt=st.battery==null?'unknown':st.battery+'% ('+st.batteryStatus.toLowerCase()+')';
 var up=Math.round(st.uptime/60),upt=up>=120?Math.round(up/6)/10+' h':up+' min';
 if(st.version)$('ver').textContent='v'+st.version;
 var esc=function(t){return String(t).replace(/[&<>""']/g,function(c){return '&#'+c.charCodeAt(0)+';'})};
 var cpu=/phone (\d+) %, app (\d+)/.exec(st.cpuText||''),fm=/^(\d+)x(\d+)@(\d+)/.exec(st.format||'');
 var res=fm?(fm[2]>=2160?'4K':fm[2]+'p')+fm[3]:'';
 var state=st.protect?'Heat protection':st.streaming?'Streaming':'Stopped',dc=st.protect?'dot hot':st.streaming?'dot on':'dot';
 var tile=function(l,v,w,g,tip){return ""<div class='tile""+(w?' warn':'')+""'""+(tip?"" data-tip='""+esc(tip)+""'"":'')+'><b>'+esc(l)+'</b>'+(v===''?'':'<span>'+esc(v)+'</span>')+(g||'')+'</div>'};
 var badge=function(t,c){return ""<div class='badge ""+(c||'')+""'>""+esc(t)+'</div>'};
 var meter=function(p,cls){p=Math.max(0,Math.min(100,p||0));return ""<div class='meter ""+(cls||'')+""'><div style='width:""+p+""%'></div></div>""};
 var bp=st.battery==null?0:st.battery,bcls=bp<20?'low':bp<40?'mid':'';
 var batt=""<div class='batt'>""+meter(bp,bcls)+""<span class='cap'></span></div>"";
 var wb=st.wifiBars==null?0:st.wifiBars,bars=""<div class='bars""+(wb<2?' weak':'')+""'>"";
 for(var k=1;k<=4;k++)bars+=""<i class='""+(k<=wb?'on':'')+""' style='height:""+(2+k*3)+""px'></i>"";bars+='</div>';
 var cpuP=cpu?+cpu[1]:0,cpuA=cpu?+cpu[2]:0,ml=st.memoryLimitMB||1536,mp=100*st.memoryMB/ml;
 var lat=st.latencyMs==null||st.latencyMs<0?null:st.latencyMs;
 var nw=st.netWatts,pw=null,pl=st.charging?'Into battery':'Battery draw';
 if(!st.charging&&st.drawWatts!=null)pw=Math.abs(st.drawWatts);else if(nw!=null)pw=st.charging?nw:Math.abs(nw);
 var pv=pw==null?'measuring':(st.charging&&pw>=0?'+':'')+pw.toFixed(1)+' W';
 var pbar=pw==null?'':meter(100*Math.abs(pw)/15,st.charging?(pw<0?'low':pw<1?'mid':''):(pw>8?'low':'mid'));
 var rc=st.recording||{},gg=rc.gps||{};
 var spd=!gg.on?'GPS off':gg.speed!=null?gg.speed+' '+gg.unitText:'N/A';
 var spp=gg.speed!=null?100*gg.speed/(gg.unitText=='mph'?80:130):0;
 var sused=(rc.usedMB||0)+(rc.savedUsedMB||0),sall=rc.freeMB!=null?rc.freeMB+sused:0;
 var sval=rc.halted?'STOPPED':rc.footageMin!=null?'~'+fmin(rc.footageMin):rc.freeMB!=null?mb(rc.freeMB*1048576)+' free':'n/a';
 var swarn=rc.halted||(rc.footageMin!=null&&rc.footageMin<30)||!!rc.warning;
 $('stat').innerHTML=""<div class='card'><div class='top'><span class='""+dc+""'></span><span class='state'>""+esc(state)+'</span>'
  +(res?""<span class='pill'>""+esc(res)+(st.safeMode?' SAFE':'')+'</span>':'')
  +""<span class='pill'>up ""+esc(upt)+'</span>'
  +""<span class='pill r'>""+esc(st.viewers+' viewer'+(st.viewers==1?'':'s')+(st.viewers>0&&lat!=null?' · '+lat+' ms':''))+'</span></div>'
  +(st.streaming?""<div class='big'>""+esc(Math.round(st.fps))+'<small>fps</small>'+esc(st.mbps.toFixed?st.mbps.toFixed(1):st.mbps)+'<small>Mbps</small></div>':'')
  +""<div class='grid'>""
  +tile('Battery',st.battery==null?'n/a':st.battery+'% '+st.batteryStatus.toLowerCase(),st.batteryFalling,batt)
  +tile('Wi-Fi',st.wifiBars==null?'n/a':wb+' of 4',st.wifiBars!=null&&wb<2,bars)
  +tile('Temp','',false,badge(st.heat||'n/a',st.heatWarn?'bad':''),st.heatDetail||'Healthy while charging normally; Unstable when Windows pauses charging because the phone is hot.')
  +tile('CPU phone',cpuP+'%',false,meter(cpuP,cpuP>70?'mid':''),'Whole phone. The camera, encoder and Wi-Fi do most of their work outside the CPU.')
  +tile('CPU app',cpuA+'%',false,meter(cpuA,cpuA>50?'mid':''))
  +tile('Memory',st.memoryMB+' MB',mp>80,meter(mp,mp>80?'low':mp>60?'mid':''),'App memory, of '+ml+' MB allowed.')
  +tile('Speed',spd,false,meter(spp,gg.speed!=null?'':'mid'),'GPS speed, also written into the recordings. The viewers pill shows the stream latency while someone watches.')
  +tile('Storage',sval,swarn,meter(sall>0?100*sused/sall:0,rc.halted?'low':swarn?'mid':''),'How much footage loop recording keeps at this quality. The bar: space the recordings use. Details under Storage.')
  +tile(pl,pv,st.batteryFalling,pbar,(st.powerText||'')+' Bar scale: 0-15 W.')
  +'</div></div>';
 var nh='';
 if(st.heatWarn)nh+=""<div class='notice bad'><span class='ni'>!</span><span>""+esc(st.heatDetail||'Temperature unstable.')+'</span></div>';
 if(st.defaultLogin)nh+=""<div class='notice'><span class='ni'>!</span><span>Default login <code>admin / admin</code> in use. Change it under Network and login.</span></div>"";
 if(rc.powerLossLeft!=null)nh+=""<div class='notice bad'><span class='ni'>!</span><span>Power lost: recording stops in ""+rc.powerLossLeft+' s unless the power comes back (Keep recording on the phone skips it).</span></div>';
 if(rc.powerStopped)nh+=""<div class='notice bad'><span class='ni'>!</span><span>Stopped: no power. Recording and live view start again when the power returns.</span></div>"";
 if(st.charging&&st.batteryStatus=='Charging'&&st.netWatts!=null&&st.netWatts<-0.3)nh+=""<div class='notice'><span class='ni'>!</span><span>Charger insufficient: the battery is draining while charging (""+st.netWatts.toFixed(1)+' W). Use a 2 A or better charger.</span></div>';
 if(rc.place=='Phone storage')nh+=""<div class='notice'><span class='ni'>!</span><span>Recording to the phone's internal storage""
  +(rc.freeMB!=null?' ('+mb(rc.freeMB*1048576)+' free'+(rc.footageMin!=null?', keeps ~'+fmin(rc.footageMin):'')+')':'')+'. An SD card is highly recommended: constant recording wears out the internal storage.</span></div>';
 $('warn').innerHTML=nh;
 if(!$('lu').value&&document.activeElement!==$('lu'))$('lu').value=st.user||'admin';
 $('lb').disabled=!st.canWrite;
 $('lgo').checked=!!st.logOn;$('lgo').disabled=!st.canWrite;$('lglinks').style.display=st.logOn?'':'none';
 $('tt').checked=st.torchOn;
 $('tt').disabled=!st.canWrite||!st.torchSupported||(!st.streaming&&!st.torchOn);
 $('tinfo').textContent=!st.torchSupported?'This camera has no flashlight control.':!st.streaming&&!st.torchOn?'Start the stream to use the flashlight.':st.torchOn?'On - switches off in '+st.torchLeftSec+' s.':'';
 $('sall').checked=st.saveAll;$('spanel').style.display=st.saveAll?'none':'block';
 $('so').checked=st.saveOis;$('sf').checked=st.saveFocus;
 $('so').disabled=!st.canWrite||!st.oisSupported;
 $('sf').disabled=$('sall').disabled=!st.canWrite;
 $('ta1').style.display=$('ta2').style.display=$('ta3').style.display=st.protect?'block':'none';
 $('fmt').disabled=$('br').disabled=$('hb').disabled=!st.canWrite||st.protect;
 if(st.protect){$('sall').disabled=$('so').disabled=$('sf').disabled=$('ps').disabled=$('tt').disabled=true;$('tinfo').textContent='Locked by heat protection.'}
 $('sol').textContent='Stabilization off (fixed mount)'+(st.oisSupported?'':' - not offered by this camera');
 $('pinfo').setAttribute('data-tip','Brightness to zero, black screen and preview off while streaming. Windows Battery Saver: '+st.batterySaver+' (an app cannot switch it on; use the phone Settings > Battery).');
 $('all').checked=st.focusAuto&&st.exposureAuto&&st.wbAuto;
 $('fa').checked=st.focusAuto;$('ea').checked=st.exposureAuto;$('wa').checked=st.wbAuto;
 $('fs').value=st.focus;$('es').value=Math.max(0,Math.min(100,e2s(st.exposureMs)));
 $('ws').min=st.wbMin;$('ws').max=st.wbMax;$('ws').value=st.wb;
 var w=st.canWrite;
 ['all','fa','ea','wa'].forEach(function(i){$(i).disabled=!w});
 $('fs').disabled=!w||st.focusAuto;$('es').disabled=!w||st.exposureAuto;$('ws').disabled=!w||st.wbAuto;
 labels();
 if(!w)$('msg').textContent='View only.';
}
function labels(){
 $('fl').textContent='Focus: '+(st.focusAuto?'automatic':Math.round($('fs').value)+' (0 near - 100 far)');
 var ms=s2e($('es').value);
 $('el').textContent='Exposure: '+(st.exposureAuto?'automatic':ms.toFixed(ms<10?2:1)+' ms (1/'+Math.round(1000/ms)+' s)');
 $('wl').textContent='White balance: '+(st.wbAuto?'automatic':$('ws').value+' K');
}
function send(body){
 touched=Date.now();
 fetch('/camera/set',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded','X-Requested-With':'W10MDashcam'},body:body})
 .then(function(r){return r.ok?r.json():r.text().then(function(x){throw new Error(x)})})
 .then(function(j){st=j;touched=0;render();if(j.result)$('msg').textContent=j.result})
 .catch(function(e){$('msg').textContent=e.message})
}
function load(){
 fetch('/camera/state').then(function(r){return r.json()}).then(function(j){
  if(st&&st.version&&j.version&&j.version!==st.version){location.reload();return} // the app was updated: get its page
  st=j;render()}).catch(function(){});
}
function later(body){clearTimeout(t);t=setTimeout(function(){send(body)},400)}
$('lb').onclick=function(){
 var u=$('lu').value.trim(),p=$('lp').value;
 if(p!==$('lp2').value){$('msg').textContent='The two passwords differ';return}
 if(p.length<8){$('msg').textContent='The password needs at least 8 characters';return}
 if(!$('lc').value){$('msg').textContent='Enter the current password too';return}
 var c=$('lc').value;
 $('lc').value=$('lp').value=$('lp2').value='';
 send('current='+encodeURIComponent(c)+'&user='+encodeURIComponent(u||'admin')+'&password='+encodeURIComponent(p));
 $('msg').textContent='Changing the login - if it worked, sign in again with the new login'
};
$('tt').onchange=function(){send('torch='+(this.checked?'on':'off'))};
$('sall').onchange=function(){send('savepower='+(this.checked?'on':'off'))};
$('so').onchange=function(){send('saveois='+(this.checked?'on':'off'))};
$('sf').onchange=function(){send('savefocus='+(this.checked?'on':'off'))};
$('hb').onclick=function(){send('highbw='+(st&&st.highBw?'off':'on'))};
$('wa2').onclick=function(){$('addrbox').className='modal open'};
$('addrclose').onclick=function(){$('addrbox').className='modal'};
$('addrbox').onclick=function(e){if(e.target===this)this.className='modal'};
$('fo').onclick=function(){$('msg').textContent='Focusing...';send('focus=once')};
$('ps').onclick=function(){send('powersave='+(st&&st.blackout?'off':'on'))};
$('ss').onclick=function(){send('live='+(st&&st.live?'off':'on'))};
$('eng').onclick=function(){send('engine=normal')};
function video(k,v){send(k+'='+v);$('msg').textContent='Applying - the stream restarts for a moment...'}
$('fmt').onchange=function(){video('format',this.value)};
$('br').onchange=function(){video('bitrate',this.value)};
$('all').onchange=function(){send('camera='+(this.checked?'auto':'manual'))};
$('fa').onchange=function(){send('focus='+(this.checked?'auto':'manual'))};
$('ea').onchange=function(){send('exposure='+(this.checked?'auto':'manual'))};
$('wa').onchange=function(){send('wb='+(this.checked?'auto':'manual'))};
$('fs').oninput=function(){touched=Date.now();labels();later('focus='+this.value)};
$('es').oninput=function(){touched=Date.now();labels();later('exposure='+s2e(this.value).toFixed(3))};
$('ws').oninput=function(){touched=Date.now();labels();later('wb='+this.value)};
// Every section header folds its section away (a down arrow at the right); the choice is remembered in this browser.
(function(){
 var saved={};try{saved=JSON.parse(localStorage.getItem('w10m-fold')||'{}')}catch(e){}
 var hs=Array.prototype.slice.call(document.getElementsByTagName('h2'));
 hs.forEach(function(h){
  var name=h.textContent,body=document.createElement('div'),n=h.nextSibling;
  while(n&&!(n.nodeType===1&&(n.tagName==='H2'||n.id==='msg'||n.tagName==='SCRIPT'))){var nx=n.nextSibling;body.appendChild(n);n=nx}
  h.parentNode.insertBefore(body,n);
  var ar=document.createElement('span');ar.className='ar';h.appendChild(ar);
  h.setAttribute('role','button');
  function show(open){body.style.display=open?'':'none';ar.textContent=open?'\u25BE':'\u25B4';h.setAttribute('aria-expanded',open?'true':'false')}
  show(saved[name]!==false);
  h.onclick=function(){var open=body.style.display==='none';show(open);saved[name]=open;try{localStorage.setItem('w10m-fold',JSON.stringify(saved))}catch(e){}
   if(open&&body.querySelector('#rlist'))recList()}
 })
})();
// Hover (or tap) anything with data-tip to see its note; the box stays inside the window.
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
// ---- dashcam recordings
function mb(b){return b>=1073741824?(b/1073741824).toFixed(1)+' GB':b>0?Math.max(1,Math.round(b/1048576))+' MB':'0 MB'}
function hms(s){s=Math.floor(s||0);var m=Math.floor(s/60);s=s%60;return m+':'+(s<10?'0':'')+s}
function fmin(m){return m>=120?(Math.round(m/6)/10)+' h':Math.round(m)+' min'}
function esc(v){return String(v).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/'/g,'&#39;')}
var segs=[1,3,5,10],lims=[-1,4,8,16,32,64,0],slims=[-1,2,4,8,16,32,0],imps={off:'Crash detection: off',low:'Crash detection: low (2.5 g)',medium:'Crash detection: medium (1.6 g)',high:'Crash detection: high (1 g)'},wheres={auto:'Save to: automatic (SD card if present)',phone:'Save to: phone storage',sd:'Save to: SD card',custom:'Save to: chosen folder'};
function recRender(){
 var r=st&&st.recording;if(!r)return;
 var on=r.state=='recording';
 $('rdot').className='dot'+(on?' rec':'');$('rstate').className='state'+(on?' rec':'');
 $('rstate').textContent=on?'REC '+hms(r.seconds):r.halted?'RECORDING STOPPED':r.state;
 $('rloc').textContent=r.location?r.location.split(' (')[0]:'';
 var i=r.error&&r.state=='error'?r.error:((r.place||'')+(r.freeMB!=null?' \u00b7 '+mb(r.freeMB*1048576)+' free':'')+(r.footageMin!=null?' \u00b7 keeps ~'+fmin(r.footageMin):''));
 if(r.dropped)i+=' \u00b7 '+r.dropped+' frames dropped';
 if(on&&r.writeMBps)i+=' \u00b7 writing '+r.writeMBps.toFixed(1)+' MB/s';
 $('rstor').textContent=r.location?('Loop: '+r.files+' file'+(r.files==1?'':'s')+', '+mb(r.usedMB*1048576)+' of '+(r.loopLimitGB==null?'all free space':r.loopLimitGB+' GB')+(r.limitGB<0?' (auto)':'')
  +'\nSaved clips: '+r.saved+', '+mb((r.savedUsedMB||0)*1048576)+' of '+(r.savedLimitNowGB==null?'no limit':r.savedLimitNowGB+' GB')+(r.savedLimitGB<0?' (auto)':'')
  +(r.freeMB!=null?'\n'+mb(r.freeMB*1048576)+' free, '+mb(r.reserveMB*1048576)+' always kept free':'')
  +(r.footageMin!=null?'\nThe loop keeps about '+fmin(r.footageMin)+' of footage at this quality':'')):'The place is decided when recording starts.';
 $('rdel').disabled=!st.canWrite;
 if(r.warning)i='WARNING: '+r.warning+' \u2013 '+i;
 if(r.halted)i='RECORDING IS NOT WORKING: '+r.incident+' – fix it, then press Start recording';
 $('rinfo').textContent=(r.enabled||r.halted)?i:'Recording is stopped';$('rinfo').style.color=(r.warning||r.halted)?'#ff8a8a':'';
 $('rg').textContent=r.impactAvailable?('G-sensor now '+r.gNow.toFixed(2)+' g, highest in 10 s '+r.gPeak.toFixed(2)+' g'+(r.lastImpact?' \u2013 last impact '+r.lastImpact:'')):'No G-sensor on this phone';
 $('rbtn').textContent=r.enabled?'Stop recording':'Start recording';$('rbtn').disabled=!st.canWrite;
 $('rsave').disabled=!st.canWrite||!on;
 var g=r.gps||{};
 $('rgps').innerHTML=!g.on?'GPS is off':g.fix?('GPS: '+(g.speed!=null?'<b>'+g.speed+' '+esc(g.unitText)+'</b> \u2013 ':'')+g.lat.toFixed(5)+', '+g.lon.toFixed(5)
  +' (within '+g.accuracy+' m) <a style=color:#9cf0b0 target=_blank href=https://www.openstreetmap.org/?mlat='+g.lat+'&mlon='+g.lon+'#map=16/'+g.lat+'/'+g.lon+'>map</a>'):('GPS: '+esc(g.status||''));
 if(Date.now()-touched<2500)return;
 fill($('rseg'),segs,function(v){return 'New file every '+v+' min'});
 fill($('rlim'),lims,function(v){return v<0?'Loop space: auto':v?'Loop space: '+v+' GB':'Loop space: all free'});
 fill($('rwhere'),['auto','phone','sd'].concat(r.customFolder?['custom']:[]),function(v){return wheres[v]});
 fill($('rimp'),['off','low','medium','high'],function(v){return imps[v]});
 fill($('rpl'),[30,60,120,300,0].concat([30,60,120,300,0].indexOf(r.powerLossSec)<0&&r.powerLossSec!=null?[r.powerLossSec]:[]),function(v){return v==0?'Power lost: keep recording':'Power lost: stop after '+(v<120?v+' s':v/60+' min')});
 $('rpl').value=r.powerLossSec;$('rpl').disabled=!st.canWrite;
 fill($('rsl'),slims,function(v){return v<0?'Saved clips: auto':v?'Saved clips: '+v+' GB':'Saved clips: no limit'});
 var gp=r.gps||{};
 fill($('runits'),['auto','kmh','mph'],function(v){return v=='auto'?'Speed: automatic ('+(gp.unitText||'')+')':v=='kmh'?'Speed: km/h':'Speed: mph'});
 $('rgo').checked=!!gp.on;$('rsub').checked=!!gp.subtitles;$('runits').value=gp.units||'auto';
 $('rgo').disabled=$('runits').disabled=$('rsub').disabled=!st.canWrite;
 $('rimp').value=r.impact;$('rsl').value=r.savedLimitGB;$('rkn').checked=r.keepNext;$('rrs').checked=r.replaceSaved;
 $('rimp').disabled=$('rsl').disabled=$('rkn').disabled=$('rrs').disabled=!st.canWrite;
 $('roo').checked=r.onOpen;$('rseg').value=r.segmentMin;$('rlim').value=r.limitGB;$('rwhere').value=r.mode;
 $('roo').disabled=$('rseg').disabled=$('rlim').disabled=$('rwhere').disabled=!st.canWrite;
}
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
  $('rlist').innerHTML=h||'<tr><td class=s style=text-align:left>No recordings yet</td></tr>';
 }).catch(function(){});
}
function recPost(path,body,msg){
 fetch(path,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded','X-Requested-With':'W10MDashcam'},body:body||''})
 .then(function(r){return r.text().then(function(t){return {ok:r.ok,t:t}})}).then(function(x){$('msg').textContent=x.ok?(msg||x.t):x.t;setTimeout(function(){load();recList()},800)});
}
$('rsave').onclick=function(){recPost('/recordings/save','','Saving: the current file ends now (and the one before it is kept too); a new file starts with no gap')};
$('rref').onclick=recList;
$('lgo').onchange=function(){touched=Date.now();send('log='+(this.checked?'on':'off'))};
$('rpl').onchange=function(){touched=Date.now();send('powerloss='+(this.value=='0'?'off':this.value))};
$('rdel').onclick=function(){
 var s=$('rdels').checked;
 var t=prompt('Delete ALL loop recordings'+(s?' AND all saved clips':' (saved clips are kept)')+' on the phone? This cannot be undone. Type DELETE to confirm.');
 if(t===null)return;
 if(t!=='DELETE'){$('msg').textContent='Nothing deleted (type DELETE in capitals)';return}
 $('msg').textContent='Deleting...';
 recPost('/recordings/deleteall','confirm=DELETE&saved='+(s?1:0),'')};
$('rbtn').onclick=function(){send('record='+(st&&st.recording&&st.recording.enabled?'off':'on'));setTimeout(recList,3000)};
$('roo').onchange=function(){touched=Date.now();send('recordonopen='+(this.checked?'on':'off'))};
$('rwhere').onchange=function(){touched=Date.now();send('location='+this.value);setTimeout(recList,2500)};
$('rseg').onchange=function(){touched=Date.now();send('segment='+this.value)};
$('rlim').onchange=function(){touched=Date.now();send('storage='+(this.value=='0'?'all':this.value=='-1'?'auto':this.value))};
$('rimp').onchange=function(){touched=Date.now();send('impact='+this.value)};
$('rgo').onchange=function(){touched=Date.now();send('gps='+(this.checked?'on':'off'))};
$('runits').onchange=function(){touched=Date.now();send('units='+this.value)};
$('rsub').onchange=function(){touched=Date.now();send('subtitles='+(this.checked?'on':'off'))};
$('rkn').onchange=function(){touched=Date.now();send('keepnext='+(this.checked?'on':'off'))};
$('rsl').onchange=function(){touched=Date.now();send('savedlimit='+(this.value=='0'?'none':this.value=='-1'?'auto':this.value))};
$('rrs').onchange=function(){touched=Date.now();send('replacesaved='+(this.checked?'on':'off'))};
$('rlist').onclick=function(e){var a=e.target;if(!a.getAttribute||!a.getAttribute('data-n'))return;
 var act=a.getAttribute('data-a');
 if(act){var nm=decodeURIComponent(a.getAttribute('data-n'));
  if(act=='unlock'&&!confirm('Unlock '+nm+'? It goes back to loop recording and will be deleted when it is the oldest.'))return;
  recPost('/recordings/'+act,'name='+encodeURIComponent(nm),(act=='lock'?'Keeping ':'Unlocked ')+nm);return}
 var n=decodeURIComponent(a.getAttribute('data-n'));if(!confirm('Delete '+n+'?'))return;
 recPost('/recordings/delete','name='+encodeURIComponent(n)+'&saved='+a.getAttribute('data-s'),'Deleted '+n)};
// the list is only fetched while the Storage section is open (it is cheap now, but nobody sees it folded)
function listShown(){return $('rlist').offsetParent!==null}
load();setInterval(load,3000);setInterval(recRender,1000);if(listShown())recList();setInterval(function(){if(listShown())recList()},10000);
</script></body></html>";
    }
}
