$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Storage/AtomicFile.cs"
$sources += @('PlanSnapshot', 'PlanPersistenceQueue' | ForEach-Object { "$root/Shikari/Services/Storage/$_.cs" })
$sources += @("ReplayAttempt", "ReplayBuffer", "ReplayPlayback", "ReplayValidation", "ReplayStore", "StrategyMergeSession", "StrategyEnrichment", "EvidenceTimeline", "EvidenceRules" | ForEach-Object { "$root/Shikari/Services/Replay/$_.cs" })
$sources += "$root/Shikari/Services/Live/WorldAlignment.cs", "$root/Shikari/Services/Adaptive/AdaptiveEngine.cs"
$sources += "$PSScriptRoot/ReplayIntegrationStubs.cs"
$sources += "$PSScriptRoot/ReplayBackgroundTests.cs"
$sources += "$root/Shikari/Services/Replay/ReplayEvidence.cs"
$sources += "$root/Shikari/Services/Replay/RecordedCast.cs"
$sources += "$root/Shikari/Services/Replay/EvidenceActorIdentity.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('shikari-replay-' + [guid]::NewGuid().ToString('N'))
try { [Shikari.Tests.ReplayIntegration]::Run($temp) }
finally {
    if ((Test-Path -LiteralPath $temp) -and ([IO.Path]::GetFullPath($temp)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
