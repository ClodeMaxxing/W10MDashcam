param([string]$PhoneIp = '', [string]$Tag = '')
if (-not $PhoneIp) { $PhoneIp = (Get-Content (Join-Path $PSScriptRoot 'phone-ip.txt') -ErrorAction SilentlyContinue | Select-Object -First 1) }
# Checks the phone's web endpoints and writes what came back to tools\http-check<Tag>.txt
$out = Join-Path $PSScriptRoot "http-check$Tag.txt"
"HTTP check $(Get-Date)" | Out-File $out -Encoding utf8
function Probe([string]$url) {
  try {
    $r = [System.Net.WebRequest]::Create($url); $r.Timeout = 10000
    $resp = $r.GetResponse()
    $sr = New-Object IO.StreamReader($resp.GetResponseStream())
    $body = $sr.ReadToEnd(); $code = [int]$resp.StatusCode; $type = $resp.ContentType; $resp.Close()
  } catch [System.Net.WebException] {
    $resp = $_.Exception.Response
    if ($resp) { $code = [int]$resp.StatusCode; $type = $resp.ContentType; $body = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd(); $resp.Close() }
    else { $code = 'no answer'; $body = $_.Exception.Message; $type = '' }
  }
  if ($body.Length -gt 600) { $body = $body.Substring(0, 600) + '...' }
  "`n=== $url -> $code ($type)`n$body" | Add-Content $out
}
Probe "http://${PhoneIp}:5001/status"
Probe "http://${PhoneIp}:5001/config"
Probe "http://${PhoneIp}:5001/snapshot.jpg"
Probe "http://${PhoneIp}:5001/nope"
Probe "http://127.0.0.1:8020/status"
Probe "http://127.0.0.1:8020/config"
"`nDone $(Get-Date)" | Add-Content $out
