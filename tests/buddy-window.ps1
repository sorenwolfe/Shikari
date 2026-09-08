param([string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'Shikari-buddy-preview.png'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = "$root/Shikari/UI/BuddyLayout.cs", "$root/Shikari/UI/BuddyWindow.cs", "$root/Shikari/Services/Buddy/BuddyCueEngine.cs", "$root/Shikari/UI/Theme/Palette.cs", "$PSScriptRoot/BuddyWindowHarness.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/System.Drawing.Common.dll", "$PSHOME/System.Private.Windows.GdiPlus.dll", "$PSHOME/System.Private.Windows.Core.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.BuddyWindowTests]::Run()
[Shikari.Tests.BuddyWindowTests]::Render($OutputPath, "$root/Shikari/Resources/buddy-dragon.png")
Write-Host "Companion UI preview (illustrative cues, drawing API substitute): $OutputPath"
