param([string]$FixturePath = "$PSScriptRoot/fixtures/m12s-act2-resolutions.json")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs"
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayEvidence','LogReplayBuilder','TowerEffectObservation' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/FfLogs/LogModels.cs", "$root/Shikari/Services/FfLogs/LogEvidence.cs", "$root/Shikari/Services/FfLogs/LogEvidenceParser.cs"
$sources += "$PSScriptRoot/ObservedResolutionTests.cs"
$sources += "$root/Shikari/Services/Storage/PlanSnapshot.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.ObservedResolutionTests]::Run($FixturePath)
