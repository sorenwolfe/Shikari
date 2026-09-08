$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$sources=@(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources+="$root/Shikari/Configuration.cs","$root/Shikari/UI/ConfigWindow.Buddy.cs","$PSScriptRoot/BuddySettingsTests.cs"
$refs=@(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)+"$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701'
[Shikari.Tests.BuddySettingsTests]::Run()
