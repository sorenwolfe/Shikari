$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/RaidPlanIo/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanNormaliser.cs", "$PSScriptRoot/RaidPlanAreaStubs.cs", "$PSScriptRoot/RaidPlanAreaTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.RaidPlanAreaTests]::Run()
if ($args.Count -gt 0) { [Shikari.Tests.RaidPlanAreaTests]::Replication((Get-Content $args[0] -Raw)) }
if ($args.Count -gt 1) { [Shikari.Tests.RaidPlanAreaTests]::CachedMarkers($args[1]) }
