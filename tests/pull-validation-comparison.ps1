$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Storage/PlanSnapshot.cs"
$sources += "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs"
$sources += @('RecordedCast', 'ReplayAttempt', 'ReplayBuffer', 'ReplayEvidence', 'PullValidationRunner', 'PullValidationComparison' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$PSScriptRoot/PullValidationComparisonTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.PullValidationComparisonTests]::Run()
