$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = "$root/Shikari/UI/BuddyLayout.cs", "$root/Shikari/UI/BuddyMotion.cs", "$root/Shikari/Services/Buddy/BuddyAmbientPresentation.cs", "$PSScriptRoot/BuddyLayoutTests.cs", "$PSScriptRoot/BuddyMotionTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable'
[Shikari.Tests.BuddyLayoutTests]::Run()
[Shikari.Tests.BuddyMotionTests]::Run()
