$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @("$root/Shikari/Model/FightMemory.cs", "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/Storage/AtomicFile.cs", "$PSScriptRoot/LearnedPersistenceTests.cs")
$sources += @(Get-ChildItem "$root/Shikari/Services/Storage/LearnedPersistence*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/EncounterLearner.cs", "$root/Shikari/Services/TimelinePrediction.cs", "$PSScriptRoot/LearnedPersistenceServiceTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('shikari-learned-persistence-' + [guid]::NewGuid().ToString('N'))
try { [Shikari.Tests.LearnedPersistenceTests]::Run($temp); [Shikari.Tests.LearnedPersistenceServiceTests]::Run((Join-Path $temp 'service')) }
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    if ((Test-Path -LiteralPath $resolved) -and $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
