$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Storage/AtomicFile.cs"
$sources += @('PlanSnapshot', 'PlanPersistenceQueue' | ForEach-Object { "$root/Shikari/Services/Storage/$_.cs" })
$sources += @('ReplayAttempt','ReplayBuffer','ReplayPlayback','ReplayValidation','ReplayStore','ReplayStore.Library','ReplayCatalog','ReplaySnapshot','StrategyMergeSession','StrategyEnrichment','EvidenceTimeline','EvidenceRules','ReplayEvidence','RecordedCast','EvidenceActorIdentity' | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/Live/WorldAlignment.cs", "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs", "$PSScriptRoot/ReplayIntegrationStubs.cs", "$PSScriptRoot/ReplayBackgroundTests.cs", "$PSScriptRoot/ReplayLibraryTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Shikari-library-' + [guid]::NewGuid().ToString('N'))
try { [Shikari.Tests.ReplayIntegration]::RunLibrary($testDirectory) }
finally {
    if ([IO.Path]::GetFullPath($testDirectory).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) -and (Test-Path -LiteralPath $testDirectory)) {
        Remove-Item -LiteralPath $testDirectory -Recurse -Force
    }
}
