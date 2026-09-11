$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += @('PlanSnapshot','PlanPersistenceQueue','EditorAutosave' | ForEach-Object { "$root/Shikari/Services/Storage/$_.cs" })
$sources += "$root/Shikari/Services/PlanJson.cs", "$PSScriptRoot/EditorAutosaveTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.EditorAutosaveTests]::Run()
