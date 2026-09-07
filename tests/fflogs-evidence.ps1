$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @("$root/Shikari/Services/FfLogs/LogModels.cs", "$root/Shikari/Services/FfLogs/FfLogsClient.cs", "$PSScriptRoot/fflogs-evidence-tests.cs")
$sources += @(Get-ChildItem "$root/Shikari/Services/FfLogs/LogEvidence*.cs" | ForEach-Object FullName)
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
$null = [Shikari.Tests.FfLogsEvidenceTests]::Run().GetAwaiter().GetResult()
