$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Live/WorldAlignment.cs", "$root/Shikari/Services/Storage/PlanSnapshot.cs", "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs"
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayEvidence','ReplayValidation','EvidenceActorIdentity','PullValidationRunner','PullValidationComparison','PullValidationSession' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/Replay/PullValidationCases.cs", "$PSScriptRoot/PullValidationSessionTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.PullValidationSessionTests]::Run()
