param(
  [string]$Body = ''   # e.g. "user=admin&password=secret" or "udp=on"; empty = ask interactively
)
# Changes W10MDashcam settings over the USB cable (the phone only accepts settings over USB).
# Fields: user, password (empty = no login), basic=on|off, udp=on|off, snapshot=on|off
$url = 'http://127.0.0.1:8020/config'
$wc = New-Object System.Net.WebClient
try {
  if (-not $Body) {
    Write-Host 'Current settings:'
    Write-Host $wc.DownloadString($url)
    Write-Host ''
    $u = Read-Host 'New user name (Enter = admin)'
    if (-not $u) { $u = 'admin' }
    $p = Read-Host 'New password (Enter = no login, stream open to the network)'
    $Body = 'user=' + [Uri]::EscapeDataString($u) + '&password=' + [Uri]::EscapeDataString($p)
  }
  $wc.Headers.Add('Content-Type', 'application/x-www-form-urlencoded')
  $r = $wc.UploadString($url, 'POST', $Body)
  Write-Host $r
  $r | Out-File (Join-Path $PSScriptRoot 'config-result.txt') -Encoding utf8
} catch {
  $m = "Could not reach the phone over USB: $_"
  Write-Host $m
  $m | Out-File (Join-Path $PSScriptRoot 'config-result.txt') -Encoding utf8
}
