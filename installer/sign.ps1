# Signs a file with the code-signing certificate in SIGNING_PFX (base64) and SIGNING_PASSWORD, timestamped. Used by CI
# for Lakeflow.Agent.exe and the MSI: agents install only updates signed by the same publisher as themselves.
param([Parameter(Mandatory)] [string] $File)
$ErrorActionPreference = 'Stop'
$pfx = Join-Path $env:RUNNER_TEMP 'signing.pfx'
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGNING_PFX))
try {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe | Where-Object FullName -match 'x64' | Select-Object -Last 1
    & $signtool.FullName sign /f $pfx /p $env:SIGNING_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $File
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
} finally {
    Remove-Item $pfx
}
