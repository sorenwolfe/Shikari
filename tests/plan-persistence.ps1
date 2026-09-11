$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$files = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$files += @('Services/PlanJson.cs','Services/PlanNormaliser.cs','Services/PlanStore.cs') | ForEach-Object { Join-Path $root "Shikari/$_" }
$files += @(Get-ChildItem "$root/Shikari/Services/Storage/*.cs" | ForEach-Object FullName)
$files += Join-Path $PSScriptRoot 'PlanPersistenceStubs.cs'
$files += Join-Path $PSScriptRoot 'PlanPersistenceTests.cs'
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $files -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
if ($null -eq [Shikari.Services.PlanStore].GetMethod('RequestSave')) {
    throw 'Plan autosaves have no asynchronous persistence request API.'
}
$temp = Join-Path ([IO.Path]::GetTempPath()) ('shikari-plan-persistence-' + [guid]::NewGuid().ToString('N'))
try { [Shikari.Tests.PlanPersistenceTests]::Run($temp) }
finally {
    if ((Test-Path -LiteralPath $temp) -and ([IO.Path]::GetFullPath($temp)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
