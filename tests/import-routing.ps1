$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @("$root/Shikari/Services/ImportSource.cs", "$root/Shikari/Services/WtfDig/WtfDigLink.cs", "$PSScriptRoot/ImportRoutingTests.cs")
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable'
[Shikari.Tests.ImportRoutingTests]::Run()
