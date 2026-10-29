# Installs the MSI unattended as the console's command does, checks what it set up, then uninstalls. Run by CI on a
# Windows runner (as Administrator):  pwsh installer/test-install.ps1 out/LakeflowAgent.msi
param([Parameter(Mandatory)] [string] $Msi)
$ErrorActionPreference = 'Stop'
function Check([bool] $ok, [string] $what) { if (-not $ok) { throw "FAILED: $what" } ; Write-Host "ok: $what" }

$token = 'tok-' + [guid]::NewGuid().ToString('N')
$log = Join-Path $env:RUNNER_TEMP 'install.log'
$p = Start-Process msiexec -Wait -PassThru -ArgumentList @('/i', (Resolve-Path $Msi), '/qn', '/l*v', $log,
    'CONTROL_API=https://lakeflow.invalid:8444/', "ENROLLMENT_TOKEN=$token", 'CA_SHA256=00ff')
Check ($p.ExitCode -eq 0) "msiexec install exit code ($($p.ExitCode))"
$leaks = Select-String -Path $log -Pattern $token -SimpleMatch
$leaks | ForEach-Object { Write-Host ("leak: " + $_.Line.Replace($token, '<TOKEN>')) }
Check ($null -eq $leaks) 'the token is not in the MSI log'

$service = Get-CimInstance Win32_Service -Filter "Name='LakeflowAgent'"
Check ($null -ne $service) 'service LakeflowAgent is installed'
Check ($service.StartName -eq 'NT SERVICE\LakeflowAgent') "runs as NT SERVICE\LakeflowAgent (is $($service.StartName))"
Check ($service.StartMode -eq 'Auto') 'starts automatically'
Check ((& sc.exe qfailure LakeflowAgent | Out-String) -match 'RESTART') 'Windows restarts it after a failure'
$sid = (& sc.exe showsid LakeflowAgent | Select-String 'SERVICE SID:').ToString().Split(':')[1].Trim()
Check ((Get-Content installer/Package.wxs -Raw).Contains($sid)) "the installer's ACLs use the service's SID ($sid)"

$data = Join-Path $env:ProgramData 'Lakeflow\Agent'
$acl = Get-Acl $data
Check ($acl.AreAccessRulesProtected) 'data folder does not inherit (Users cannot read it)'
$who = $acl.Access | ForEach-Object { $_.IdentityReference.Value }
Check (-not ($who | Where-Object { $_ -match 'Users|Everyone' })) "data folder access: $($who -join ', ')"
Check ($who -contains 'NT SERVICE\LakeflowAgent') 'the service can write its data folder'

$key = 'HKLM:\SOFTWARE\Lakeflow\Agent\Enrollment'
Check ((Get-ItemProperty $key).ControlApi -eq 'https://lakeflow.invalid:8444/') 'enrollment written for the agent'
$keyWho = (Get-Acl $key).Access | ForEach-Object { $_.IdentityReference.Value }
Check (-not ($keyWho | Where-Object { $_ -match 'Users|Everyone' })) "enrollment key access: $($keyWho -join ', ')"

# The agent starts, tries to enroll (the address does not exist), keeps running and logs why.
Start-Sleep -Seconds 20
Check ((Get-Service LakeflowAgent).Status -eq 'Running') 'the agent is running'
$agentLog = Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Select-Object -First 1
Check ($null -ne $agentLog) 'the agent writes its log in its data folder'
Check (Select-String -Path $agentLog.FullName -Pattern 'Enrollment failed' -Quiet) 'it tried to enroll with the token'

$p = Start-Process msiexec -Wait -PassThru -ArgumentList @('/x', (Resolve-Path $Msi), '/qn')
Check ($p.ExitCode -eq 0) "msiexec uninstall exit code ($($p.ExitCode))"
Check ($null -eq (Get-Service LakeflowAgent -ErrorAction SilentlyContinue)) 'the service is removed'
Check (-not (Test-Path $key)) 'the enrollment key is removed'
Check (Test-Path $data) 'the data folder (buffer) is kept'
