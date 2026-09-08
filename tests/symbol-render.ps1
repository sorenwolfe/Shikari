param(
    [string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'Shikari-symbol-preview.png'),
    [string]$IconDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
dotnet build "$root/Shikari/Shikari.csproj" -c Release --no-restore --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not build the embedded symbol resources.' }
$assembly = "$root/Shikari/bin/Release/Shikari.dll"
Add-Type -Path $assembly
$sources = "$root/Shikari/UI/CanvasSymbols.cs", "$root/Shikari/UI/CanvasText.cs", "$root/Shikari/UI/SymbolGeometry.cs", "$root/Shikari/UI/EmojiArtwork.cs", "$root/Shikari/UI/ArenaCanvas.Mini.cs", "$PSScriptRoot/SymbolRendererHarness.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll", "$PSHOME/System.Drawing.Common.dll", "$PSHOME/System.Private.Windows.GdiPlus.dll", "$PSHOME/System.Private.Windows.Core.dll", $assembly
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701,0436'
[Shikari.Tests.SymbolRendererTests]::Run()
[Shikari.Tests.SymbolRendererTests]::Render($OutputPath, "$root/Shikari/Resources/Emoji", $IconDirectory, [IO.File]::ReadAllText("$PSScriptRoot/fixtures/caro-act1-symbols.json"))
Write-Host "Act 1 symbol preview (source coordinates, production helpers, drawing API substitute): $OutputPath"
