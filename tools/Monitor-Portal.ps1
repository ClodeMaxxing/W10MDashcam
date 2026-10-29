# Logs the phone's whole-system load once a minute to tools\portal-monitor.csv (CPU %, GPU engine %, battery mWh).
# Needs a Device Portal pairing first: run   Monitor-Portal.ps1 -Pair ABC123   with the PIN shown on the phone
# (Settings > Update & security > For developers > Pair). Leave it running while the phone streams; stop with Ctrl+C.
param([string]$Pair, [int]$Seconds = 60)
$ip  = (Get-Content "$PSScriptRoot\phone-ip.txt" -TotalCount 1).Trim()
$dir = Join-Path $env:LOCALAPPDATA 'W10MSecurityCam'; New-Item -ItemType Directory -Force $dir | Out-Null
$jar = Join-Path $dir 'portal.jar'                     # session cookie; kept outside the repository
$csv = Join-Path $PSScriptRoot 'portal-monitor.csv'
function Csrf { ((Select-String -Path $jar -Pattern 'CSRF-Token').Line -split "`t")[-1] }
if ($Pair) {
  Remove-Item $jar -ErrorAction SilentlyContinue
  curl.exe -sk -c $jar -o NUL "https://$ip/authenticate.htm"
  curl.exe -sk -b $jar -c $jar -X POST -H "X-CSRF-Token: $(Csrf)" -d "x=1" "https://$ip/api/authorize/pair?pin=$Pair&persistent=1" | Out-Null
}
if (-not (Test-Path $csv)) { 'time,cpu_pct,gpu_engines_pct,battery_mwh,charging,free_mb' | Out-File $csv -Encoding ascii }
while ($true) {
  try {
    $p = curl.exe -sk -m 20 -b $jar "https://$ip/api/resourcemanager/systemperf" | ConvertFrom-Json
    $b = curl.exe -sk -m 20 -b $jar "https://$ip/api/power/battery" | ConvertFrom-Json
    $gpu = ($p.GPUData.AvailableAdapters[0].EnginesUtilization | ForEach-Object { [math]::Round($_) }) -join '/'
    ('{0},{1},{2},{3},{4},{5}' -f (Get-Date -Format s), $p.CpuLoad, $gpu, $b.RemainingCapacity, $b.Charging, [int]($p.AvailablePages * $p.PageSize / 1MB)) | Add-Content $csv -Encoding ascii
  } catch { "$(Get-Date -Format s),error,$($_.Exception.Message)" | Add-Content $csv }
  Start-Sleep $Seconds
}
