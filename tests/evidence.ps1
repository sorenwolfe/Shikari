$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @("$root/Shikari/Services/Replay/ReplayEvidence.cs", "$root/Shikari/Services/Replay/EvidenceTimeline.cs", "$root/Shikari/Services/Live/WorldAlignment.cs", "$PSScriptRoot/EvidenceTests.cs")
$sources += "$root/Shikari/Services/Replay/EvidenceRecorder.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable'
[Shikari.Tests.EvidenceTests]::Run()
