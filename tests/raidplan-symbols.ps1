$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/RaidPlanIo/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/WtfDig/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanNormaliser.cs", "$root/Shikari/Services/PlanJson.cs", "$root/Shikari/Services/ShareCode.cs"
$sources += "$root/Shikari/Services/Replay/ReplayAttempt.cs", "$root/Shikari/Services/Replay/ReplayBuffer.cs", "$root/Shikari/Services/Replay/ReplayEvidence.cs", "$root/Shikari/Services/Replay/ReplayValidation.cs"
$sources += "$PSScriptRoot/RaidPlanAreaStubs.cs", "$PSScriptRoot/RaidPlanSymbolTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.RaidPlanSymbolTests]::Run()
[Shikari.Tests.RaidPlanSymbolTests]::ActOne([IO.File]::ReadAllText("$PSScriptRoot/fixtures/caro-act1-symbols.json"))
if ($args.Count -gt 0) { [Shikari.Tests.RaidPlanSymbolTests]::Corpus($args[0]) }
