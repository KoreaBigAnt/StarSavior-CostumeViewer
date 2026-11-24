param([Parameter(Mandatory=$true)][string]$App)
$ErrorActionPreference='Stop'
$App=[IO.Path]::GetFullPath($App)
$command=Join-Path $App 'viewer-command.json'
function Wait-For($predicate){$end=[DateTime]::UtcNow.AddSeconds(45);while(!(& $predicate)){if([DateTime]::UtcNow -gt $end){throw 'Viewer did not respond'};Start-Sleep -Milliseconds 80}}
function Send($value){Wait-For {!(Test-Path $command)};$temp=$command+'.new';$value|ConvertTo-Json|Set-Content -LiteralPath $temp -Encoding utf8;Move-Item -LiteralPath $temp -Destination $command;Wait-For {!(Test-Path $command)}}
function Read-Result($action,$name){$path=Join-Path $App $name;if(Test-Path $path){Remove-Item -LiteralPath $path};Send @{action=$action};Wait-For {Test-Path $path};Get-Content $path -Raw|ConvertFrom-Json}
$before=Read-Result 'status' 'viewer-status.json'
# Intentionally omit Save, with a value different from the persisted wardrobe setting.
Send @{action='chest';height=1.1;fullness=1.5}
$after=Read-Result 'status' 'viewer-status.json'
if($before.Camera.Distance -ne $after.Camera.Distance -or ($before.Camera.Target -join ',') -ne ($after.Camera.Target -join ',')){throw 'Height editing moved the camera'}
$result=Join-Path $App 'viewer-profile-result.json';if(Test-Path $result){Remove-Item -LiteralPath $result}
Send @{action='profile';zip=$false};Wait-For {Test-Path $result}
$folder=(Get-Content $result -Raw|ConvertFrom-Json).Path
$shape=(Get-Content (Join-Path $folder 'appearance-profile.json') -Raw|ConvertFrom-Json).ChestShape
if([Math]::Abs($shape.Height-1.1) -gt .0001 -or [Math]::Abs($shape.Fullness-1.5) -gt .0001){throw 'Profile exported saved values instead of the current draft'}
Send @{action='chest';height=.9;fullness=.1}
$ready=Join-Path $App 'viewer-ready.json';$originalUnit=(Get-Content $ready -Raw|ConvertFrom-Json).Unit
$otherUnit=if($originalUnit -eq 1001){1005}else{1001}
Remove-Item -LiteralPath $ready
Send @{action='load';unit=$otherUnit;skin=0}
Wait-For {try{(Get-Content $ready -Raw|ConvertFrom-Json).Unit -eq $otherUnit}catch{$false}}
Send @{action='history-load';folder=$folder}
Wait-For {
    $state=Read-Result 'chest-status' 'viewer-chest-status.json'
    [Math]::Abs($state.Height-1.1) -lt .0001 -and [Math]::Abs($state.Fullness-1.5) -lt .0001
}
'PASS: Height keeps camera fixed; unsaved height/fullness export and history restore round-trip correctly.'
