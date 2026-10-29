namespace W10MSecurityCam.Streaming
{
    /// <summary>The small camera-settings web page (http://PHONE-IP:5001/camera). Plain HTML + a little script.</summary>
    internal static class CameraPage
    {
        public const string Html = @"<!doctype html>
<html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>W10MSecurityCam</title>
<style>
body{font-family:sans-serif;background:#181818;color:#eee;margin:0;padding:16px;max-width:480px}
h1{font-size:18px;margin:0 0 4px}.sub{color:#999;font-size:12px;margin-bottom:12px;word-break:break-word}
.row{margin:14px 0}.head{display:flex;justify-content:space-between;align-items:center;font-size:14px}
input[type=text],input[type=password],input:not([type]){background:#2a2a2a;color:#eee;border:1px solid #444;padding:6px;font-size:14px;width:46%;margin:2px 0}
input[type=range]{width:100%}input[type=range]:disabled{opacity:.35}
select{background:#2a2a2a;color:#eee;border:1px solid #444;padding:6px;font-size:14px;max-width:48%}
button{background:#2f4f6f;color:#fff;border:0;border-radius:4px;padding:8px 12px;font-size:14px;margin-top:6px}
button:disabled{opacity:.35}
label{font-size:13px;color:#ccc}#msg{color:#e9a;font-size:12px;min-height:16px}
h2{font-size:13px;color:#8ab;text-transform:uppercase;letter-spacing:1px;border-bottom:1px solid #333;padding-bottom:4px;margin:22px 0 4px}
#stat{font-size:14px;line-height:1.6;white-space:pre-line}.bad{color:#f88;font-size:13px;margin-top:4px}
</style></head><body>
<h1>W10MSecurityCam</h1>
<h2>Status</h2>
<div class='row'><div id='stat'>loading...</div><div class='bad' id='warn'></div></div>
<h2>Video</h2>
<div class='row'>
<select id='fmt'></select> <select id='br'></select>
<div style='margin-top:8px'><label><input type='checkbox' id='hb'> High bandwidth mode</label></div>
<div class='sub'>Unlocks bitrates above 24 Mbps. Needs 5 GHz Wi-Fi or a USB-C connection; on 2.4 GHz the stream drops frames.</div>
<div class='sub' id='vmsg'></div>
<button id='ss'>Stop stream</button> <button id='eng' style='display:none'>Return to normal mode</button>
<div class='sub' id='safe' style='color:#e9a'></div></div>
<div class='row'><div class='head'><span>Watch the stream (VLC, NVR)</span></div>
<div class='sub' id='addr' style='color:#ccc;white-space:pre-line;user-select:all'></div></div>
<h2>Camera</h2><div class='sub' id='now'></div>
<div class='row'><label><input type='checkbox' id='all'> Automatic focus, exposure and white balance</label></div>
<div class='row'><div class='head'><span id='fl'>Focus</span><label><input type='checkbox' id='fa'> Auto</label></div>
<input type='range' id='fs' min='0' max='100' step='1'>
<button id='fo'>Focus once and lock</button></div>
<div class='row'><div class='head'><span id='el'>Exposure</span><label><input type='checkbox' id='ea'> Auto</label></div>
<input type='range' id='es' min='0' max='100' step='0.5'></div>
<div class='row'><div class='head'><span id='wl'>White balance</span><label><input type='checkbox' id='wa'> Auto</label></div>
<input type='range' id='ws' min='2000' max='8000' step='100'></div>
<h2>Power</h2>
<div class='row'><button id='ps'>Save power: black out the phone screen</button>
<div class='sub'>Brightness to zero, black screen and preview off while streaming. Tap the phone to wake it for a moment.</div>
<div class='sub' id='pinfo'></div></div>
<h2>Login</h2>
<div class='row'><div class='sub' id='lmsg'>One login protects the stream and this page.</div>
<input id='lu' placeholder='User name'> <input id='lp' type='password' placeholder='New password'> <input id='lp2' type='password' placeholder='Repeat password'>
<button id='lb'>Change login</button>
<div class='sub'>Use the new login in your NVR / VLC: rtsp://USER:PASSWORD@phone-address:8554/live. The browser will ask you to sign in again.</div></div>
<div id='msg'></div>
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
 $('hb').checked=st.highBw;
 $('fo').disabled=$('ps').disabled=!st.canWrite;
 $('ps').textContent=st.blackout?'Phone screen is black - wake it (turn blackout off)':'Save power: black out the phone screen';
 $('fmt').disabled=$('br').disabled=$('hb').disabled=!st.canWrite;
 $('vmsg').textContent=st.streaming?'Streaming':'Not streaming';
 $('ss').textContent=st.streaming?'Stop stream':'Start stream';$('ss').disabled=!st.canWrite;
 $('eng').style.display=st.safeMode?'inline-block':'none';$('eng').disabled=!st.canWrite;
 $('safe').textContent=st.safeMode?'Safe mode: the app is using its fallback engine, limited to 1080p. Return to normal mode to use 4K.':'';
 var h=location.hostname,u=st.loginSet?'USER:PASSWORD@':'';
 var usb='127.0.0.1:'+st.usbPort;
 $('addr').textContent='Wi-Fi:  rtsp://'+u+h+':'+st.rtspPort+st.streamPath
  +'\nUSB cable (on the PC it is plugged into):  rtsp://'+usb+st.streamPath
  +'\nThis page over USB:  http://'+usb+'/camera';
 $('now').textContent=st.mode+' - '+st.now;
 var bt=st.battery==null?'unknown':st.battery+'% ('+st.batteryStatus.toLowerCase()+')';
 var up=Math.round(st.uptime/60),upt=up>=120?Math.round(up/6)/10+' h':up+' min';
 $('stat').textContent=(st.streaming?'Streaming':'Not streaming')+'  -  '+st.fps+' fps, '+st.mbps+' Mbps, '+st.viewers+' viewer(s)'
  +'\nBattery: '+bt+(st.batteryFalling?' - not keeping up':'')
  +'\nWi-Fi: '+(st.wifiBars==null?'n/a':st.wifiBars+' of 4 bars')+'   Memory: '+st.memoryMB+' MB   Up: '+upt
  +'\nTemperature: not readable on this phone. '+st.heat;
 var wl=[];if(st.battery!=null&&st.battery<20&&!st.charging)wl.push('Low battery - connect a wall charger.');
 else if(st.batteryFalling)wl.push('The charger is too weak: the battery is still falling.');
 if(st.heatWarn)wl.push(st.heat);
 if(st.defaultLogin)wl.push('Default login (admin / admin) in use - change it under Login.');
 $('warn').textContent=wl.join(' ');
 if(!$('lu').value&&document.activeElement!==$('lu'))$('lu').value=st.user||'admin';
 $('lb').disabled=!st.canWrite;
 $('pinfo').textContent='Windows Battery Saver: '+st.batterySaver+' (an app cannot switch it on; it can be turned on in the phone Settings > Battery).';
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
 fetch('/camera/set',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded','X-Requested-With':'W10MSecurityCam'},body:body})
 .then(function(r){return r.ok?r.json():r.text().then(function(x){throw new Error(x)})})
 .then(function(j){st=j;touched=0;render();if(j.result)$('msg').textContent=j.result})
 .catch(function(e){$('msg').textContent=e.message})
}
function load(){
 fetch('/camera/state').then(function(r){return r.json()}).then(function(j){st=j;render()}).catch(function(){});
}
function later(body){clearTimeout(t);t=setTimeout(function(){send(body)},400)}
$('lb').onclick=function(){
 var u=$('lu').value.trim(),p=$('lp').value;
 if(p!==$('lp2').value){$('msg').textContent='The two passwords differ';return}
 if(p.length<4){$('msg').textContent='The password needs at least 4 characters';return}
 $('lp').value=$('lp2').value='';
 send('user='+encodeURIComponent(u||'admin')+'&password='+encodeURIComponent(p));
 $('msg').textContent='Login changed - sign in again with the new login'
};
$('hb').onchange=function(){send('highbw='+(this.checked?'on':'off'))};
$('fo').onclick=function(){$('msg').textContent='Focusing...';send('focus=once')};
$('ps').onclick=function(){send('powersave='+(st&&st.blackout?'off':'on'))};
$('ss').onclick=function(){send('stream='+(st&&st.streaming?'stop':'start'))};
$('eng').onclick=function(){send('engine=normal')};
function video(k,v){send(k+'='+v);$('vmsg').textContent='Applying - the stream restarts for a moment...'}
$('fmt').onchange=function(){video('format',this.value)};
$('br').onchange=function(){video('bitrate',this.value)};
$('all').onchange=function(){send('camera='+(this.checked?'auto':'manual'))};
$('fa').onchange=function(){send('focus='+(this.checked?'auto':'manual'))};
$('ea').onchange=function(){send('exposure='+(this.checked?'auto':'manual'))};
$('wa').onchange=function(){send('wb='+(this.checked?'auto':'manual'))};
$('fs').oninput=function(){touched=Date.now();labels();later('focus='+this.value)};
$('es').oninput=function(){touched=Date.now();labels();later('exposure='+s2e(this.value).toFixed(3))};
$('ws').oninput=function(){touched=Date.now();labels();later('wb='+this.value)};
load();setInterval(load,3000);
</script></body></html>";
    }
}
