param([string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'Shikari-mini-preview.png'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/UI/MiniMapLayout.cs", "$root/Shikari/Services/Live/StandingSpot.cs", "$root/Shikari/UI/ArenaCanvas.Mini.cs", "$PSScriptRoot/MiniRendererHarness.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll", "$PSHOME/System.Drawing.Common.dll", "$PSHOME/System.Private.Windows.GdiPlus.dll", "$PSHOME/System.Private.Windows.Core.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.UI.ArenaCanvas]::CheckArrival()
[Shikari.UI.ArenaCanvas]::CheckPersonalView()
[Shikari.UI.ArenaCanvas]::Render($OutputPath)
Write-Host "Renderer preview (synthetic positions, drawing API substitute): $OutputPath"
