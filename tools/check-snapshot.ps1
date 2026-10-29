param([string]$PhoneIp = '', [int]$Count = 10, [int]$EverySeconds = 3)
if (-not $PhoneIp) { $PhoneIp = (Get-Content (Join-Path $PSScriptRoot 'phone-ip.txt') -ErrorAction SilentlyContinue | Select-Object -First 1) }
# Fetches /snapshot.jpg several times, saves them as tools\snapshot-N.jpg and logs size and time taken.
$log = Join-Path $PSScriptRoot 'snapshot-check.txt'
"Snapshot check $(Get-Date)" | Out-File $log -Encoding utf8
for ($i = 1; $i -le $Count; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $wc = New-Object System.Net.WebClient
    $bytes = $wc.DownloadData("http://${PhoneIp}:5001/snapshot.jpg")
    $f = Join-Path $PSScriptRoot "snapshot-$i.jpg"
    [IO.File]::WriteAllBytes($f, $bytes)
    "#{0}: {1} bytes in {2} ms" -f $i, $bytes.Length, $sw.ElapsedMilliseconds | Add-Content $log
  } catch {
    "#{0}: FAILED after {1} ms: {2}" -f $i, $sw.ElapsedMilliseconds, $_ | Add-Content $log
  }
  Start-Sleep -Seconds $EverySeconds
}
"Done $(Get-Date)" | Add-Content $log
