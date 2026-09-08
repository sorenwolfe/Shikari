$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @("$root/Shikari/Services/Buddy/BuddyAmbientPresentation.cs", "$root/Shikari/Services/Buddy/BuddyAmbientController.cs", "$PSScriptRoot/BuddyAmbientTests.cs")
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.BuddyAmbientTests]::Run()
