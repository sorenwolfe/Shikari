$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayValidation','ReplayEvidence','EvidenceTimeline','EvidenceRules','EvidenceActorIdentity','StrategyEnrichment','StrategyMergeSession','EvidencePreparationSession' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs", "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Live/WorldAlignment.cs"
$sources += "$root/Shikari/Services/Storage/PlanSnapshot.cs", "$root/Shikari/Services/Storage/PlanPersistenceQueue.cs"
$sources += "$PSScriptRoot/EvidencePreparationTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.EvidencePreparationTests]::Run()
