$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/WtfDig/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/RaidPlanIo/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanNormaliser.cs", "$PSScriptRoot/WtfDigBoardTests.cs"
$sources += @('ReplayAttempt','ReplayBuffer','ReplayValidation','ReplayEvidence','EvidenceTimeline','EvidenceRules','StrategyEnrichment' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Live/WorldAlignment.cs", "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.WtfDigBoardTests]::Run()
