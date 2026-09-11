$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sources = @("$root/Shikari/Plugin.cs", "$PSScriptRoot/PluginLifecycleStubs.cs", "$PSScriptRoot/PluginLifecycleTests.cs")
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.PluginLifecycleTests]::Run()
