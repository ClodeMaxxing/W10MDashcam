$out = Join-Path $PSScriptRoot 'phone-log.txt'
try {
  $req = [System.Net.WebRequest]::Create('http://127.0.0.1:8020/'); $req.Timeout = 8000
  $resp = $req.GetResponse(); $sr = New-Object IO.StreamReader($resp.GetResponseStream())
  $sr.ReadToEnd() | Out-File $out -Encoding utf8; $resp.Close()
} catch {
  try { (New-Object System.Net.WebClient).DownloadString("http://$((Get-Content (Join-Path $PSScriptRoot 'phone-ip.txt') -ErrorAction SilentlyContinue | Select-Object -First 1)):5001/") | Out-File $out -Encoding utf8 }
  catch { "Phone log fetch failed (USB and Wi-Fi): $_" | Out-File $out -Encoding utf8 }
}
