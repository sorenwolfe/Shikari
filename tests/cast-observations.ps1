$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs"
$sources += @('ReplayAttempt', 'ReplayBuffer', 'ReplayValidation', 'ReplayEvidence', 'LogReplayBuilder' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/Replay/RecordedCast.cs", "$root/Shikari/Services/EncounterMonitor.cs", "$PSScriptRoot/EncounterCastTests.cs"
$sources += "$root/Shikari/Services/FfLogs/LogModels.cs", "$root/Shikari/Services/FfLogs/LogEvidence.cs", "$PSScriptRoot/CastObservationTests.cs"
$sources += "$root/Shikari/Services/Replay/EvidenceActorIdentity.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.CastObservationTests]::Run()
[Shikari.Tests.EncounterCastTests]::Run()
