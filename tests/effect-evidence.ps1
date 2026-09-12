$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayValidation','ReplayEvidence','EvidenceActorIdentity','LogReplayBuilder' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/FfLogs/LogModels.cs", "$root/Shikari/Services/FfLogs/LogEvidence.cs", "$root/Shikari/Services/FfLogs/LogEvidenceParser.cs", "$PSScriptRoot/EffectEvidenceTests.cs"
$sources += "$root/Shikari/Services/Storage/PlanSnapshot.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.EffectEvidenceTests]::Run()
