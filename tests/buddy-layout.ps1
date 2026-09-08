$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = "$root/Shikari/UI/BuddyLayout.cs", "$PSScriptRoot/BuddyLayoutTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable'
[Shikari.Tests.BuddyLayoutTests]::Run()
