$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/reliability.ps1"

function LegacyCode($plan) {
    $json = [Newtonsoft.Json.JsonConvert]::SerializeObject($plan, [Shikari.Services.PlanJson]::Compact())
    $raw = [Text.Encoding]::UTF8.GetBytes($json)
    $output = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($output, [IO.Compression.CompressionLevel]::SmallestSize, $true)
    $gzip.Write($raw); $gzip.Dispose()
    $result = 'RPLAN1:' + [Convert]::ToBase64String($output.ToArray()).Replace('+','-').Replace('/','_').TrimEnd('=')
    $output.Dispose()
    return $result
}

$plan = [Shikari.Model.PlanDocument]::CreateDefault()
$plan.Slides.Clear()
$plan.Notes = 'Spread → stack. 北 / 南'
for ($i = 0; $i -lt 40; $i++) {
    $slide = [Shikari.Model.Slide]::new()
    $slide.Title = "Mechanic $i"
    for ($j = 0; $j -lt 12; $j++) {
        $item = [Shikari.Model.CanvasItem]::new()
        $item.Kind = [Shikari.Model.CanvasItemKind]::Zone
        $item.Color = [Shikari.Model.CanvasItem]::DefaultAoeColor
        $item.Position = [Numerics.Vector2]::new(($j + 1) / 14.0, ($i + 1) / 42.0)
        $slide.Items.Add($item)
    }
    $plan.Slides.Add($slide)
    $entry = [Shikari.Model.TimelineEntry]::new()
    $entry.SlideId = $slide.Id
    $entry.Label = "Mechanic $i"
    $plan.Timeline.Add($entry)
}
$legacy = LegacyCode $plan
$timer = [Diagnostics.Stopwatch]::StartNew()
$compact = [Shikari.Services.ShareCode]::Encode($plan)
$timer.Stop()
Assert ($compact.StartsWith('RPLAN2:')) 'Representative plan did not select compact encoding'
Assert ($compact.Length -lt $legacy.Length) 'Compact encoding did not reduce size'
Write-Host "40-slide synthetic plan: $($legacy.Length) -> $($compact.Length) characters ($([math]::Round(100 * (1 - $compact.Length / $legacy.Length), 1))% smaller), encode $($timer.ElapsedMilliseconds) ms"
foreach ($code in @($legacy, $legacy.Substring(7), $compact, ("  rplan2: `n" + $compact.Substring(7) + " `r`n"))) {
    Assert ([Shikari.Services.ShareCode]::TryDecode($code, [ref]$decoded, [ref]$errorText)) "Round trip failed: $errorText"
    Assert ($decoded.Notes -eq $plan.Notes) 'Unicode notes changed'
    Assert ($decoded.Id -eq $plan.Id) 'Plan identity changed'
    Assert ($decoded.Slides.Count -eq 40) 'Slides lost'
    Assert ($decoded.Timeline[17].SlideId -eq $decoded.Slides[17].Id) 'Timeline slide reference lost'
    Assert ($decoded.Slides[9].Items[2].Color -eq [Shikari.Model.CanvasItem]::DefaultAoeColor) 'RGBA color lost'
}
$payload = $compact.Substring(7).Replace('-','+').Replace('_','/')
$payload = $payload.PadRight(($payload.Length + 3) -band -4, '=')
$bytes = [Convert]::FromBase64String($payload)
function Pack($bytes) { return 'RPLAN2:' + [Convert]::ToBase64String($bytes).Replace('+','-').Replace('/','_').TrimEnd('=') }
$bad = $bytes.Clone(); $bad[4] = $bad[4] -bxor 1
Assert (![Shikari.Services.ShareCode]::TryDecode((Pack $bad), [ref]$decoded, [ref]$errorText)) 'Failed checksum accepted'
Assert (![Shikari.Services.ShareCode]::TryDecode((Pack $bytes[0..($bytes.Length - 2)]), [ref]$decoded, [ref]$errorText)) 'Truncated Brotli accepted'
Assert (![Shikari.Services.ShareCode]::TryDecode((Pack ([byte[]]($bytes + 0))), [ref]$decoded, [ref]$errorText)) 'Trailing bytes accepted'
$bad = $bytes.Clone(); [BitConverter]::GetBytes([int]4194305).CopyTo($bad, 0)
Assert (![Shikari.Services.ShareCode]::TryDecode((Pack $bad), [ref]$decoded, [ref]$errorText)) 'Oversized compact length accepted'
$small = [Shikari.Model.PlanDocument]::CreateDefault()
Assert ([Shikari.Services.ShareCode]::Encode($small).Length -le (LegacyCode $small).Length) 'Small plan grew'
$plan.Slides[0].BackdropId = 'local-only-image'
Assert ([Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($plan), [ref]$decoded, [ref]$errorText)) 'Backdrop export failed'
Assert ($decoded.Slides[0].BackdropId -eq '') 'Local backdrop exported'
Assert ($plan.Slides[0].BackdropId -eq 'local-only-image') 'Export mutated source'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('shikari-sharing-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    [Shikari.Plugin]::PluginInterface.Directory = $testRoot
    $store = [Shikari.Services.PlanStore]::new()
    $plan.Slides[0].Items[0].Color = 123
    $token = [Shikari.Model.CanvasItem]::new()
    $token.Color = 456
    $plan.Slides[0].Items.Add($token)
    $rule = [Shikari.Model.AdaptiveMechanic]::new()
    $rule.Enabled = $true; $rule.TerritoryId = 1; $rule.AnchorActionId = 100; $rule.Occurrence = 0
    $branch = [Shikari.Model.StatusBranch]::new()
    $branch.StatusId = 10; $branch.Parameter = 0; $branch.SlideId = $plan.Slides[0].Id
    $rule.Branches.Add($branch); $plan.AdaptiveMechanics.Add($rule)
    Assert ([Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($plan), [ref]$decoded, [ref]$errorText)) 'Adaptive sharing failed'
    Assert ($decoded.FormatVersion -eq 2) 'Adaptive plan did not declare format 2'
    Assert ($decoded.AdaptiveMechanics[0].Occurrence -eq 0) 'Every-use occurrence lost during serialization'
    Assert ($decoded.AdaptiveMechanics[0].Branches[0].Parameter -eq 0) 'Exact zero status parameter became wildcard'
    Assert ($decoded.AdaptiveMechanics[0].Branches[0].SlideId -eq $plan.Slides[0].Id) 'Branch slide reference lost'
    $json = [Newtonsoft.Json.JsonConvert]::SerializeObject($plan, [Shikari.Services.PlanJson]::Compact())
    Assert ($json.Contains('"FormatVersion":2')) 'Wire format version omitted; old clients could silently drop rules'
    $extra = [Shikari.Model.StatusCondition]::new()
    $extra.StatusId = 11; $extra.Parameter = 0; $extra.MaximumSeconds = 3600
    $branch.AdditionalStatuses.Add($extra)
    Assert ([Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($plan), [ref]$decoded, [ref]$errorText)) 'Compound sharing failed'
    Assert ($decoded.FormatVersion -eq 3) 'Compound plan must require a client that understands AND conditions'
    Assert (!$decoded.AdaptiveMechanics[0].Enabled) 'Decoded shared rules must require explicit verification before enabling'
    Assert ($decoded.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].Parameter -eq 0) 'Extra exact zero parameter changed'
    Assert ($decoded.AdaptiveMechanics[0].Branches[0].AdditionalStatuses[0].MaximumSeconds -eq 3600) 'Unrestricted extra duration changed'
    $json = [Newtonsoft.Json.JsonConvert]::SerializeObject($plan, [Shikari.Services.PlanJson]::Compact())
    Assert ($json.Contains('"FormatVersion":3')) 'Compound wire version omitted'
    $branch.AdditionalStatuses.Add($extra); $branch.AdditionalStatuses.Add($extra); $branch.AdditionalStatuses.Add($extra)
    Assert (!$rule.IsValid($plan)) 'Over-limit conjunction accepted by evaluator validation'
    Assert (![Shikari.Services.ShareCode]::TryDecode([Shikari.Services.ShareCode]::Encode($plan), [ref]$decoded, [ref]$errorText)) 'Over-limit conjunction accepted by sharing'
    $rule.Enabled = $true
    $null = [Shikari.Services.PlanNormaliser]::Normalise($plan)
    Assert (!$rule.Enabled) 'Invalid conjunction remained enabled after disk normalization'
    Assert ($branch.AdditionalStatuses.Count -le 3) 'Disk conjunction list was not bounded'
    $branch.AdditionalStatuses.Clear(); $branch.AdditionalStatuses.Add($extra); $rule.Enabled = $true
    $unknown = [Shikari.Model.StatusObservation]::new()
    $unknown.ParameterKnown = $false; $unknown.DurationKnown = $false
    $observationJson = [Newtonsoft.Json.JsonConvert]::SerializeObject($unknown, [Shikari.Services.PlanJson]::Compact())
    $roundTrip = [Newtonsoft.Json.JsonConvert]::DeserializeObject($observationJson, [Shikari.Model.StatusObservation], [Shikari.Services.PlanJson]::Compact())
    Assert (!$roundTrip.ParameterKnown -and !$roundTrip.DurationKnown) 'Compact observation JSON changed unknown values into known zero'
    $null = $store.Import($plan, $false)
    Assert (!$plan.AdaptiveMechanics[0].Enabled) 'Imported automation was enabled without review'
    Assert ($plan.Slides[0].Items[0].Color -eq [Shikari.Model.CanvasItem]::DefaultAoeColor) 'Imported AoE not orange'
    Assert ($token.Color -eq 456) 'Import recolored a player token'
    $plan.Slides[0].Items[0].Color = 789
    Assert ($store.SaveActive()) 'Color edit save failed'
    $reloaded = [Shikari.Services.PlanStore]::new()
    Assert ($reloaded.Active.Slides[0].Items[0].Color -eq 789) 'Saved custom color was reset on load'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'PASS: legacy and compact formats, references, Unicode, integrity, size bounds, backdrops, import colors and saved overrides'
Write-Host 'PASS: adaptive format version, occurrence zero, exact parameter zero, branch references, disabled imported rules'
Write-Host 'PASS: compound format compatibility, AND condition serialization, bounded invalid rules, unknown observation defaults'
