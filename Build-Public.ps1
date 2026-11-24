param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$GameRoot='C:/Program Files (x86)/Steam/steamapps/common/StarSavior',[string]$ReferenceDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) '.runtime/viewer-reference-1.13/Data/Managed'))
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $output -ManagedDirectory (Join-Path $GameRoot 'Data/Managed')
Set-Content -LiteralPath (Join-Path $output 'viewer-edition.json') -Value '{"Edition":"Public"}' -Encoding utf8 -NoNewline
& (Join-Path $PSScriptRoot 'Map-PublicSymbols.ps1') -Viewer (Join-Path $output 'engine-overrides/StarSavior.CostumeViewer.dll') -Managed (Join-Path $GameRoot 'Data/Managed') -Reference $ReferenceDirectory
# Public distributions contain our assemblies; original game DLLs are read and patched locally.
foreach($file in Get-ChildItem (Join-Path $output 'engine-overrides') -File){
    if($file.Name -notin @('StarSavior.CostumeViewer.dll','StarSavior.ModMeshRuntime.dll','public-symbols.json')){Remove-Item -LiteralPath $file.FullName}
}
foreach($file in Get-ChildItem (Join-Path $output 'native-runtime') -File){Remove-Item -LiteralPath $file.FullName}
$patcher=Join-Path $output 'runtime-patcher';New-Item -ItemType Directory -Path $patcher -Force|Out-Null
$workspace=Split-Path -Parent $PSScriptRoot
$cecil=Join-Path $PSScriptRoot 'vendor/Mono.Cecil.dll'
if(!(Test-Path $cecil)){$cecil=Join-Path $workspace 'bepinex-loader/vendor/BepInEx-5.4.23.6/BepInEx/core/Mono.Cecil.dll'}
Copy-Item -LiteralPath $cecil -Destination $patcher -Force
$license=Join-Path $PSScriptRoot 'vendor/Mono.Cecil-LICENSE'
if(!(Test-Path $license)){$license=Join-Path $workspace 'bepinex-loader/vendor/licenses/jbevain-cecil-LICENSE.txt'}
Copy-Item -LiteralPath $license -Destination (Join-Path $patcher 'LICENSE') -Force
$sdk=dotnet --list-sdks|Select-Object -Last 1
if($sdk -notmatch '^(\S+) \[(.+)\]$'){throw 'SDK not found'}
$compiler=Join-Path $Matches[2] ($Matches[1]+'/Roslyn/bincore/csc.dll')
$framework='C:/Windows/Microsoft.NET/Framework64/v4.0.30319'
$refs=@('mscorlib.dll','System.dll','System.Core.dll')|ForEach-Object {'/reference:'+(Join-Path $framework $_)}
dotnet exec $compiler /nologo /noconfig /nostdlib /target:exe /optimize+ ('/out:'+(Join-Path $patcher 'RuntimePatcher.exe')) @refs ('/reference:'+$cecil) (Join-Path $PSScriptRoot 'RuntimePatcher.cs')
if($LASTEXITCODE -ne 0){throw 'Runtime patcher compile failed'}
'Built public viewer without original game assemblies.'
