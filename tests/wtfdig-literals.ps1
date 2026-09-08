param([string]$SourcePath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path "$root/Shikari/Services/WtfDig/LiteralData.cs", "$PSScriptRoot/WtfDigLiteralTests.cs" -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.WtfDigLiteralTests]::Run($SourcePath)
