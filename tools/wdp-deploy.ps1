$ErrorActionPreference = "Stop"
$base = "http://192.168.3.159:80"
$pkgDir = "H:\项目\UWP\JM-UWP\app\Moyue\AppPackages\Moyue_0.2.1.0_ARM_Test"
$appx = Join-Path $pkgDir "Moyue_0.2.1.0_ARM.appx"
$certFile = Join-Path $pkgDir "Moyue_0.2.1.0_ARM.cer"
$deps = @(
  (Join-Path $pkgDir "Dependencies\arm\Microsoft.NET.Native.Framework.1.7.appx"),
  (Join-Path $pkgDir "Dependencies\arm\Microsoft.NET.Native.Runtime.1.7.appx")
)
$s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$page = Invoke-WebRequest -Uri "$base/" -WebSession $s -TimeoutSec 10 -UseBasicParsing
$m = [regex]::Match("$($page.Headers['Set-Cookie'])", "CSRF-Token=([^;]+)")
if (-not $m.Success) { Write-Output "NO CSRF"; exit 1 }
$csrf = $m.Groups[1].Value
$boundary = "----WdpDeploy" + [Guid]::NewGuid().ToString("N")
$ms = New-Object System.IO.MemoryStream
$sw = New-Object System.IO.StreamWriter($ms, [Text.Encoding]::UTF8)
function AddPart($name, $path) {
  $fn = Split-Path $path -Leaf
  $bytes = [IO.File]::ReadAllBytes($path)
  $sw.Write("--$boundary`r`n")
  $sw.Write("Content-Disposition: form-data; name=`"$name`"; filename=`"$fn`"`r`n")
  $sw.Write("Content-Type: application/octet-stream`r`n`r`n")
  $sw.Flush()
  $ms.Write($bytes, 0, $bytes.Length)
  $sw.Write("`r`n"); $sw.Flush()
  Write-Output ("  part: {0} ({1} B)" -f $fn, $bytes.Length)
}
AddPart "Moyue_0.2.1.0_ARM.appx" $appx
AddPart "Moyue_0.2.1.0_ARM.cer" $certFile
foreach ($d in $deps) { if (Test-Path $d) { AddPart (Split-Path $d -Leaf) $d } }
$sw.Write("--$boundary--`r`n"); $sw.Flush()
$body = $ms.ToArray(); $ms.Close()
Write-Output "总上传: $($body.Length) B"
$headers = @{ "X-CSRF-Token" = $csrf }
try {
  $r = Invoke-WebRequest -Uri "$base/api/app/packagemanager/package?package=Moyue_0.2.1.0_ARM.appx" -Method Post -Body $body -ContentType "multipart/form-data; boundary=$boundary" -Headers $headers -WebSession $s -TimeoutSec 240 -UseBasicParsing
  $txt = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
  Write-Output "HTTP $([int]$r.StatusCode)  $txt"
} catch {
  $er = $_.Exception.Response
  if ($er) { Write-Output "HTTP $([int]$er.StatusCode)" } else { Write-Output "ERR: $($_.Exception.Message)" }
}
