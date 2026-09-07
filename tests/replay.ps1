$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs"
$sources += "$root/Shikari/Services/Replay/ReplayAttempt.cs", "$root/Shikari/Services/Replay/ReplayBuffer.cs", "$root/Shikari/Services/Replay/ReplayPlayback.cs"
$sources += "$PSScriptRoot/ReplayTests.cs"
$sources += "$root/Shikari/Services/Replay/ReplayValidation.cs"
$sources += "$root/Shikari/Services/Replay/ReplayEvidence.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.ReplayTests]::Run()
