$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$sources = @(Get-ChildItem "$root/Shikari/Model/*.cs" | ForEach-Object FullName)
$sources += "$root/Shikari/Configuration.cs", "$root/Shikari/Services/ReminderEngine.cs", "$root/Shikari/Services/CallTemplate.cs"
$sources += "$root/Shikari/Services/TimelinePrediction.cs", "$root/Shikari/Services/Speech/SpokenText.cs", "$root/Shikari/Services/Buddy/BuddyCueEngine.cs", "$PSScriptRoot/BuddyReminderTests.cs"
$refs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName) + "$PSHOME/Newtonsoft.Json.dll"
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable','/nowarn:1701,0649'
[Shikari.Tests.BuddyReminderTests]::Run()
