param([string]$FixturePath = "$PSScriptRoot/fixtures/m12s-act2-observed.json")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Storage/PlanSnapshot.cs"
$sources += "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs", "$root/Shikari/Services/Live/WorldAlignment.cs"
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayEvidence','EvidenceTimeline','EncounterAssignmentDecoder','LogReplayBuilder','PullValidationRunner' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/FfLogs/LogModels.cs", "$root/Shikari/Services/FfLogs/LogEvidence.cs"
$sources += "$PSScriptRoot/ObservedAssignmentTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.ObservedAssignmentTests]::Run($FixturePath)
