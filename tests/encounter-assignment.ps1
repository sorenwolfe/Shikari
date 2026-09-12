$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Live/WorldAlignment.cs"
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayEvidence','EvidenceTimeline','EncounterAssignmentDecoder' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$PSScriptRoot/EncounterAssignmentDecoderTests.cs"
$sources += "$root/Shikari/Services/Storage/PlanSnapshot.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.EncounterAssignmentDecoderTests]::Run()
