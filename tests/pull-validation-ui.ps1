$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Live/WorldAlignment.cs", "$root/Shikari/Services/Storage/PlanSnapshot.cs", "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs", "$root/Shikari/UI/Theme/Palette.cs"
$sources += @('RecordedCast','ReplayAttempt','ReplayBuffer','ReplayEvidence','ReplayValidation','EvidenceActorIdentity','PullValidationRunner','PullValidationSession','PullValidationComparison','PositionCheck','PositionCheckSession','TowerEffectObservation' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/UI/MainWindow.PullValidation.cs", "$PSScriptRoot/PullValidationUiTests.cs"
$sources += "$root/Shikari/UI/MainWindow.ValidationCases.cs"
$sources += "$root/Shikari/UI/MainWindow.PositionCheck.cs"
$sources += "$root/Shikari/Services/Replay/PullValidationCases.cs", "$root/Shikari/Services/Replay/PullValidationCaseStore.cs", "$root/Shikari/Services/Storage/AtomicFile.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.UI.MainWindow]::RunValidationUiTests()
