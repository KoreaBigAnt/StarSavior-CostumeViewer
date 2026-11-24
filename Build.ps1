param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'app'),[string]$ManagedDirectory)

$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$managed=if($ManagedDirectory){[IO.Path]::GetFullPath($ManagedDirectory)}else{Join-Path $workspace '.runtime/client/Data/Managed'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path (Join-Path $output 'engine-overrides'),(Join-Path $output 'catalog') -Force|Out-Null
New-Item -ItemType Directory -Path (Join-Path $output 'native-runtime') -Force|Out-Null
New-Item -ItemType Directory -Path (Join-Path $output 'mod-tools') -Force|Out-Null
Set-Content -LiteralPath (Join-Path $output 'viewer-edition.json') -Value '{"Edition":"Private"}' -Encoding utf8 -NoNewline
$guide=Join-Path $PSScriptRoot 'app/사용방법.txt'
$guideOutput=Join-Path $output '사용방법.txt'
if((Test-Path $guide) -and $guide -ne $guideOutput){Copy-Item -LiteralPath (Get-Item -LiteralPath $guide).FullName -Destination $guideOutput -Force}
Get-ChildItem (Join-Path $PSScriptRoot 'mod-tools') -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output 'mod-tools') -Force}
$nativeSource=Join-Path $workspace '.runtime/client'
if(!(Test-Path $nativeSource)){$nativeSource=Split-Path -Parent (Split-Path -Parent $managed)}
Get-ChildItem $nativeSource -Filter '*.dll' | Where-Object Name -ne 'UnityPlayer.dll' | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output ('native-runtime/'+$_.Name)) -Force}
$sdk=dotnet --list-sdks|Select-Object -Last 1
if($sdk -notmatch '^(\S+) \[(.+)\]$'){throw 'SDK not found'}
$compiler=Join-Path $Matches[2] ($Matches[1]+'/Roslyn/bincore/csc.dll')
$references=@('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll','Newtonsoft.Json.dll','UniTask.dll','UnityEngine.CoreModule.dll','UnityEngine.AssetBundleModule.dll','UnityEngine.UIModule.dll','UnityEngine.UI.dll','UnityEngine.IMGUIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','Unity.Addressables.dll','Unity.ResourceManager.dll','UnityEngine.AudioModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.AnimationModule.dll','UnityEngine.ImageConversionModule.dll')|ForEach-Object {'/reference:'+(Join-Path $managed $_)}
$sources=Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'Launcher.cs' | ForEach-Object FullName
$sources=@($sources)+(Join-Path $PSScriptRoot 'shared/ChestSettings.cs'),(Join-Path $PSScriptRoot 'shared/ChestRig.cs'),(Join-Path $PSScriptRoot 'shared/BodyRig.cs'),(Join-Path $PSScriptRoot 'shared/BodyShapeMath.cs')
dotnet exec $compiler /nologo /noconfig /nostdlib /target:library /optimize+ ('/out:'+(Join-Path $output 'mod-tools/StarSavior.ModMeshRuntime.dll')) @references (Join-Path $PSScriptRoot 'mod-tools/ModMeshOverrides.cs')
if($LASTEXITCODE -ne 0){throw 'Mod mesh runtime compile failed'}
Copy-Item (Join-Path $output 'mod-tools/StarSavior.ModMeshRuntime.dll') (Join-Path $output 'engine-overrides/StarSavior.ModMeshRuntime.dll') -Force
dotnet exec $compiler /nologo /noconfig /nostdlib /target:library /optimize+ ('/out:'+(Join-Path $output 'engine-overrides/StarSavior.CostumeViewer.dll')) @references ('/reference:'+(Join-Path $output 'engine-overrides/StarSavior.ModMeshRuntime.dll')) @sources
if($LASTEXITCODE -ne 0){throw 'Viewer compile failed'}
$framework='C:/Windows/Microsoft.NET/Framework64/v4.0.30319'
$frameworkRefs=@('mscorlib.dll','System.dll','System.Core.dll','System.Windows.Forms.dll','System.Web.Extensions.dll')|ForEach-Object {'/reference:'+(Join-Path $framework $_)}
dotnet exec $compiler /nologo /noconfig /nostdlib /target:winexe /platform:x64 /optimize+ ('/out:'+(Join-Path $output 'StarSavior-CostumeViewer.exe')) @frameworkRefs (Join-Path $PSScriptRoot 'src/Launcher.cs')
if($LASTEXITCODE -ne 0){throw 'Launcher compile failed'}
# Keep the viewer's bootstrap and its patched dependencies on the same client revision.
foreach($name in @('UnityEngine.CoreModule.dll','NKC.dll','StarSavior.Offline.dll','Bs.Windows.dll','Bs.ServerLibrary.dll','Star.Templets.dll','NKM.Templets.dll','Assembly-CSharp.dll','FX.Define.dll','FX.dll','mscorlib.dll','NKM.dll','UniTask.dll')){
    $inputAssembly=Join-Path $managed $name
    if(Test-Path $inputAssembly){Copy-Item $inputAssembly (Join-Path $output ('engine-overrides/'+$name)) -Force}
    elseif(!$name.StartsWith('StarSavior.')){throw ('Missing runtime assembly: '+$name)}
}
if(Test-Path (Join-Path $managed 'StarSavior.Appearance.dll')){Copy-Item (Join-Path $managed 'StarSavior.Appearance.dll') (Join-Path $output 'engine-overrides') -Force}
$cecilPath=Join-Path $PSScriptRoot 'vendor/Mono.Cecil.dll'
if(!(Test-Path $cecilPath)){$cecilPath='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/Extensions/TestPlatform/Extensions/Mono.Cecil.dll'}
Add-Type -Path $cecilPath
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new();$resolver.AddSearchDirectory($managed);$resolver.AddSearchDirectory((Join-Path $output 'engine-overrides'))
$parameters=[Mono.Cecil.ReaderParameters]::new();$parameters.AssemblyResolver=$resolver
$nkc=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $output 'engine-overrides/NKC.dll'),$parameters)
$viewer=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $output 'engine-overrides/StarSavior.CostumeViewer.dll'),$parameters)
function AllTypes($types){foreach($type in $types){$type;AllTypes $type.NestedTypes}}
$awake=$nkc.GetType('NKC.AppLauncher').Methods | Where-Object Name -eq 'Awake'
if(!$awake.HasBody){throw 'Viewer startup artwork hook was not found'}
$hide=$viewer.GetType('StarSaviorCostumeViewer.StartupAppearance').Methods | Where-Object Name -eq 'Begin'
$il=$awake.Body.GetILProcessor()
$first=$awake.Body.Instructions[0]
$il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
$il.InsertBefore($first,$il.Create([Mono.Cecil.Cil.OpCodes]::Call,$nkc.ImportReference($hide)))
$localStart=$viewer.GetType('StarSaviorCostumeViewer.LocalBootstrap').Methods | Where-Object Name -eq Start
$launcherStart=$nkc.GetType('NKC.AppLauncher').Methods | Where-Object Name -eq Start
$launcherStart.CustomAttributes.Clear()
$launcherStart.Body.Instructions.Clear()
$launcherStart.Body.ExceptionHandlers.Clear()
$launcherStart.Body.Variables.Clear()
$il=$launcherStart.Body.GetILProcessor()
$il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
$il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call,$nkc.ImportReference($localStart)))
$il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
$temp=Join-Path $output 'engine-overrides/NKC.viewer.dll';$nkc.Write($temp);$nkc.Dispose();$viewer.Dispose();Move-Item -LiteralPath $temp -Destination (Join-Path $output 'engine-overrides/NKC.dll') -Force
$windowsPath=Join-Path $output 'engine-overrides/Bs.Windows.dll'
$windows=[Mono.Cecil.ModuleDefinition]::ReadModule($windowsPath)
try {
    $check=$windows.GetType('Bs.Windows.WindowsExtends').Methods | Where-Object Name -eq 'CheckMultiClient'
    $locks=@($check.Body.Instructions | Where-Object {
        $_.OpCode -eq [Mono.Cecil.Cil.OpCodes]::Ldstr -and ([string]$_.Operand).StartsWith('StarClientStandAlone', [StringComparison]::Ordinal)
    })
    if($locks.Count -ne 1){throw 'Expected one game multi-client mutex'}
    $locks[0].Operand='StarSaviorCostumeViewerPlayer'
    $temporary=$windowsPath+'.new'
    $windows.Write($temporary)
} finally { $windows.Dispose() }
Move-Item -LiteralPath $temporary -Destination $windowsPath -Force
'Built standalone costume viewer.'
