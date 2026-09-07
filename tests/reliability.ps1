$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$files = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$files += @('Services/PlanJson.cs','Services/PlanNormaliser.cs','Services/ShareCode.cs','Services/PlanStore.cs','Services/Storage/AtomicFile.cs','Services/RosterResolver.cs','Services/Live/ArenaTracker.cs','Services/Live/WorldAlignment.cs') | ForEach-Object { Join-Path $root "Shikari/$_" }
$files += Join-Path $PSScriptRoot 'ReliabilityStubs.cs'
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $files -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
function Assert($value, $message) { if (!$value) { throw $message } }
$doc = [Shikari.Model.PlanDocument]::CreateDefault()
$decoded = $null; $errorText = ''
Assert ([Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($doc),[ref]$decoded,[ref]$errorText)) 'Valid round trip failed'
$doc.Notes = 'x' * (4 * 1024 * 1024 + 1)
$code = [Shikari.Services.ShareCode]::Encode($doc)
Assert (![Shikari.Services.ShareCode]::TryDecode($code,[ref]$decoded,[ref]$errorText)) 'Decompressed payload over 4 MiB was accepted'
$doc = [Shikari.Model.PlanDocument]::CreateDefault()
1..256 | ForEach-Object { $doc.Slides.Add([Shikari.Model.Slide]::new()) }
Assert (![Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($doc),[ref]$decoded,[ref]$errorText)) 'Too many slides accepted'
$doc = [Shikari.Model.PlanDocument]::CreateDefault()
$validCode = [Shikari.Services.ShareCode]::Encode($doc)
Assert (![Shikari.Services.ShareCode]::TryDecode($validCode + (' ' * 1048576),[ref]$decoded,[ref]$errorText)) 'Oversized encoded input accepted'
$item = [Shikari.Model.CanvasItem]::new()
$item.Kind = [Shikari.Model.CanvasItemKind]::Freehand
for ($i=0; $i -lt 131073; $i++) { $item.Points.Add([System.Numerics.Vector2]::Zero) }
$doc.Slides[0].Items.Add($item)
Assert (![Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($doc),[ref]$decoded,[ref]$errorText)) 'Too many drawing points accepted'
Write-Host 'PASS: share code round trip, encoded/decompressed limits, slide and point counts'

$temp = Join-Path ([IO.Path]::GetTempPath()) ('shikari-reliability-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
 [Shikari.Plugin]::PluginInterface.Directory = $temp
 $store = [Shikari.Services.PlanStore]::new()
 $path = Join-Path $temp ('plans/' + $store.Active.Id + '.json')
 $original = [IO.File]::ReadAllText($path)
 $locked = [IO.File]::Open($path,'Open','Read','Read')
 try {
  $store.Active.Name = 'unsaved'
  $result = $store.SaveActive()
  Assert ($result -eq $false) 'Failed save did not return false'
  Assert (![string]::IsNullOrEmpty($store.LastSaveError)) 'Failed save did not expose error'
  Assert ([IO.File]::ReadAllText($path) -eq $original) 'Failed save changed original file'
 } finally { $locked.Dispose() }
 Assert ($store.SaveActive()) 'Save retry failed'
 Assert ([IO.File]::ReadAllText($path).Contains('unsaved')) 'Retry did not persist pending changes'
 $store.Active.Id = '../escape'
 Assert (!$store.SaveActive()) 'Unsafe plan id accepted'
 Assert (!(Test-Path (Join-Path $temp 'escape.json'))) 'Save escaped plans directory'
 Write-Host 'PASS: failed save preserves original, reports error, retries, and rejects unsafe ids'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }


[Shikari.Services.Live.ReliabilityLiveTest]::Run()
Write-Host 'PASS: arena reader respects local pin without duplicate seats'

