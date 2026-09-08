param(
    [string]$DalamudHome = $env:DALAMUD_HOME,
    [string]$BindingPath = '',
    [string]$NativePath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $DalamudHome) { $DalamudHome = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'XIVLauncher/addon/Hooks/dev' }
if (-not $BindingPath) { $BindingPath = Join-Path $DalamudHome 'Dalamud.Bindings.ImGui.dll' }
if (-not $NativePath) { $NativePath = Join-Path $DalamudHome 'cimgui.dll' }
foreach ($dependency in @($BindingPath, $NativePath)) {
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw "Missing native-input test dependency: $dependency. Set DALAMUD_HOME or pass -DalamudHome." }
}
$BindingPath = (Resolve-Path -LiteralPath $BindingPath).Path
$NativePath = (Resolve-Path -LiteralPath $NativePath).Path
Add-Type -Path $BindingPath
$root = Split-Path $PSScriptRoot
$sources = "$root/Shikari/UI/BuddyLayout.cs", "$root/Shikari/UI/BuddyMotion.cs", "$root/Shikari/UI/BuddyWindow.cs", "$root/Shikari/Services/Buddy/BuddyCueEngine.cs", "$root/Shikari/Services/Buddy/BuddyAmbientPresentation.cs", "$root/Shikari/UI/Theme/Palette.cs", "$PSScriptRoot/BuddyNativeInputTests.cs"
$blinkSource = "$root/Shikari/UI/BuddyBlink.cs"
if (Test-Path -LiteralPath $blinkSource) { $sources += $blinkSource }
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + $BindingPath
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/unsafe','/nowarn:1701'
[Shikari.Tests.BuddyNativeInputTests]::Run($NativePath)
