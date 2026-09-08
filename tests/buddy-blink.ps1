param([string]$PreviewSource = '', [string]$PreviewPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @("$root/Shikari/UI/BuddyBlink.cs", "$root/Shikari/UI/BuddyMotion.cs", "$root/Shikari/Services/Buddy/BuddyAmbientPresentation.cs", "$PSScriptRoot/BuddyBlinkTests.cs")
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
if ($PreviewSource) {
    $sources += $PreviewSource
    $refs += "$PSHOME/System.Drawing.Common.dll", "$PSHOME/System.Private.Windows.GdiPlus.dll", "$PSHOME/System.Private.Windows.Core.dll"
}
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.BuddyBlinkTests]::Run()
if ($PreviewSource) { [Shikari.Tests.BuddyBlinkPreview]::Render($PreviewPath, "$root/Shikari/Resources/buddy-poses.png") }
