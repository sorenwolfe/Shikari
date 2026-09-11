$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @("$root/Shikari/Services/ActionIndex.cs", "$root/Shikari/Services/ActionFilter.cs", "$root/Shikari/Model/Enums.cs", "$PSScriptRoot/action-index-tests.cs")
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
$null = [Shikari.Tests.ActionIndexTests]::Run().GetAwaiter().GetResult()
