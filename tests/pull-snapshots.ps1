$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Storage/PlanSnapshot.cs"
$sources += "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs", "$root/Shikari/Services/Adaptive/AdaptiveRuleSnapshot.cs"
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayEvidence' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$PSScriptRoot/PullSnapshotTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.PullSnapshotTests]::Run()
