$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/WtfDig/*.cs" | ForEach-Object FullName)
$sources += @(Get-ChildItem "$root/Shikari/Services/RaidPlanIo/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Services/PlanNormaliser.cs", "$root/Shikari/UI/MainWindow.WtfDig.cs", "$PSScriptRoot/WtfDigUiStubs.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701,0414'
[Shikari.UI.MainWindow]::RunImportChecks()
