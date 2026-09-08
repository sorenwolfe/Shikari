$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$artPath = Join-Path $root 'Shikari/Resources/buddy-poses.png'
$bytes = [IO.File]::ReadAllBytes($artPath)
if ($bytes.Length -lt 26 -or $bytes[25] -ne 6) { throw 'Ember needs a true RGBA PNG, not a painted background.' }
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new($artPath)
try {
    if ($bitmap.Width -ne 1254 -or $bitmap.Height -ne 1254) { throw 'Pose atlas dimensions differ from the renderer contract.' }
    $cell = 627
    foreach ($index in 0..3) {
        $left = ($index % 2) * $cell
        $top = [int][Math]::Floor($index / 2) * $cell
        $visible = 0
        $clear = 0
        for ($y = 0; $y -lt $cell; $y += 8) {
            for ($x = 0; $x -lt $cell; $x += 8) {
                $alpha = $bitmap.GetPixel($left + $x, $top + $y).A
                if ($alpha -eq 0) { $clear++ }
                if ($alpha -gt 0) { $visible++ }
                if (($x -lt 8 -or $y -lt 8 -or $x -ge $cell - 8 -or $y -ge $cell - 8) -and $alpha -ne 0) {
                    throw "Pose $index touches a cell boundary and can bleed into an adjacent pose."
                }
            }
        }
        if ($clear -lt 3000 -or $visible -lt 500) { throw "Pose $index lacks an isolated creature and transparent surroundings." }
    }
} finally { $bitmap.Dispose() }
$dll = [Reflection.Assembly]::LoadFrom((Join-Path $root 'Shikari/bin/Release/Shikari.dll'))
$stream = $dll.GetManifestResourceStream('Shikari.Resources.buddy-poses.png')
if ($null -eq $stream) { throw 'The built plugin is missing the transparent atlas.' }
try {
    $memory = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($memory)
        $actual = [Convert]::ToBase64String($memory.ToArray())
        if ($actual -ne [Convert]::ToBase64String($bytes)) { throw 'The built plugin embeds stale companion artwork.' }
    } finally { $memory.Dispose() }
} finally { $stream.Dispose() }
if ($dll.GetManifestResourceNames() -contains 'Shikari.Resources.buddy-dragon.png') { throw 'The obsolete opaque portrait is still embedded.' }
Write-Host 'Ember artwork: four RGBA cutouts, clear cell edges, and exact embedded atlas passed.'
