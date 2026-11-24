param([string]$Version='1.14.0',[string]$App=(Join-Path $PSScriptRoot 'app'),[ValidateSet('Public','Private')][string]$Edition='Public')
$ErrorActionPreference='Stop'
$App=[IO.Path]::GetFullPath($App)
$stage=Join-Path $PSScriptRoot ('package/releases/'+$Version+'-'+[Guid]::NewGuid().ToString('N')+'/StarSavior-CostumeViewer')
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach($item in @('StarSavior-CostumeViewer.exe','viewer-edition.json','engine-overrides','native-runtime','mod-tools')){
    Copy-Item -LiteralPath (Join-Path $app $item) -Destination $stage -Recurse -Force
}
if($Edition -eq 'Public'){Copy-Item -LiteralPath (Join-Path $app 'runtime-patcher') -Destination $stage -Recurse -Force}
Get-ChildItem $stage -File -Recurse|Where-Object {$_.Extension -in @('.md','.txt','.rst')}|ForEach-Object {Remove-Item -LiteralPath $_.FullName}
if($Edition -eq 'Public'){
    $allowed=@('StarSavior.CostumeViewer.dll','StarSavior.ModMeshRuntime.dll','Mono.Cecil.dll')
    $unexpected=@(Get-ChildItem $stage -Recurse -Filter '*.dll'|Where-Object {$_.Name -notin $allowed})
    if($unexpected){throw ('Public package includes original runtime binaries: '+($unexpected.Name -join ', '))}
}
$suffix=if($Edition -eq 'Private'){'-Private'}else{''}
$zip=Join-Path $PSScriptRoot ('StarSavior-CostumeViewer'+$suffix+'-v'+$Version+'.zip')
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$true)
$files=Get-ChildItem $stage -Recurse -File|ForEach-Object {
    [pscustomobject]@{Path=$_.FullName.Substring($stage.Length+1);Length=$_.Length;SHA256=(Get-FileHash $_.FullName).Hash}
}
[pscustomobject]@{Release=('viewer-v'+$Version);Edition=$Edition;Zip=$zip;Length=(Get-Item $zip).Length;SHA256=(Get-FileHash $zip).Hash;Files=$files}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $PSScriptRoot ('package-manifest-'+$Edition.ToLowerInvariant()+'.json')) -Encoding utf8
Get-Item $zip|Select-Object FullName,Length
