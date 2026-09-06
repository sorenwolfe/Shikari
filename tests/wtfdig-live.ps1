param([string]$Url = 'https://wtfdig.info/74/m9s')
$ErrorActionPreference = 'Stop'
# Compile and run the deterministic core checks before the optional live request.
. "$PSScriptRoot/wtfdig.ps1"
$client = [Shikari.Services.WtfDig.WtfDigClient]::new()
try {
    $link = [Shikari.Services.WtfDig.WtfDigLink]::Parse($Url)
    $guide = $client.LoadAsync($link, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $selection = [Shikari.Services.WtfDig.WtfDigSelection]::new()
    $selection.Strategy = $guide.Strategies[0]['stratName'].ToString()
    $preview = [Shikari.Services.WtfDig.WtfDigMapper]::Convert($guide, $selection)
    if ($preview.Plan.Slides.Count -eq 0) { throw 'Live guide produced no slides.' }
    Write-Host "Live source: $($guide.Title); strategy $($selection.Strategy); $($preview.Plan.Slides.Count) slides"
    Write-Host "Source SHA256: $($guide.SourceHash)"
    foreach ($warning in $preview.Warnings) { Write-Warning $warning }
    foreach ($variant in $preview.MissingVariants) { Write-Host "Selection required: $($variant.Key): $($variant.Choices -join ', ')" }
    Write-Host 'PASS: live source fetched and converted. This does not validate diagram downloads or in-game rendering.'
} finally { $client.Dispose() }
