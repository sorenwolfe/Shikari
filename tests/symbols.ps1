$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
dotnet build "$root/Shikari/Shikari.csproj" -c Release --no-restore --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not build the actual embedded emoji resource assembly.' }
$assembly = "$root/Shikari/bin/Release/Shikari.dll"
Add-Type -Path $assembly
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll", $assembly
Add-Type -Path "$PSScriptRoot/SymbolTests.cs" -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.SymbolTests]::Run()
$embedded = @([Shikari.Services.Symbols.EmojiCatalog].Assembly.GetManifestResourceNames() | Where-Object { $_ -like 'Shikari.Resources.Emoji.*.png' })
if ($embedded.Count -ne 4009) { throw 'The installed DLL does not contain the complete emoji artwork collection.' }
$pngs = @(Get-ChildItem "$root/Shikari/Resources/Emoji/*.png")
if ($pngs.Count -ne 4009) { throw 'Pinned emoji asset set is incomplete.' }
$index = Get-Content "$root/Shikari/Resources/Emoji/index.txt"
$assetNames = @($pngs | ForEach-Object BaseName)
if (@(Compare-Object $index $assetNames).Count -gt 0) { throw 'Emoji index differs from bundled assets.' }
Write-Host 'All 4009 indexed emoji PNGs are present.'
