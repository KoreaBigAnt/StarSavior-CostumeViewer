param([Parameter(Mandatory=$true)][string]$App)
$ErrorActionPreference='Stop'
$App=[IO.Path]::GetFullPath($App)
$command=Join-Path $App 'viewer-command.json'
function Wait-For($predicate){$end=[DateTime]::UtcNow.AddSeconds(12);while(!(& $predicate)){if([DateTime]::UtcNow -gt $end){throw 'Viewer did not respond'};Start-Sleep -Milliseconds 50}}
function Send($value){Wait-For {!(Test-Path $command)};$temp=$command+'.new';$value|ConvertTo-Json|Set-Content -LiteralPath $temp -Encoding utf8;Move-Item -LiteralPath $temp -Destination $command;Wait-For {!(Test-Path $command)}}
function Read-Body {
    $path=Join-Path $App 'viewer-chest-status.json'
    if(Test-Path $path){Remove-Item -LiteralPath $path}
    Send @{action='chest-status'};Wait-For {Test-Path $path}
    Get-Content $path -Raw|ConvertFrom-Json
}
Send @{action='selection';value=$true}
Send @{action='chest';reset=$true}
$review=Join-Path $App 'viewer-body-review-result.json';if(Test-Path $review){Remove-Item -LiteralPath $review}
Send @{action='body-review';name='persistence-baseline';frame=$true};Wait-For {Test-Path $review}
$original=Read-Body
Send @{action='chest';height=1;fullness=1}
$fatOnly=Read-Body
foreach($bone in $original.Body.Nodes|Where-Object {$_.Name -match 'Thigh|Calf|Foot'}){
    $after=$fatOnly.Body.Nodes|Where-Object Name -eq $bone.Name
    if([Math]::Abs($bone.Position[1]-$after.Position[1]) -gt .0001){throw ('Fullness displaced knee height: '+$bone.Name)}
    foreach($axis in @(0,2)){
        $origin=$original.Body.RootMatrix[12+$axis]
        $expected=$origin+($bone.Position[$axis]-$origin)*1.12
        if([Math]::Abs($expected-$after.Position[$axis]) -gt .001){throw ('Fullness bent leg chain: '+$bone.Name)}
    }
}
Send @{action='chest';height=1.2;fullness=1;save=$true}
$modified=Read-Body
Start-Sleep -Milliseconds 300
$later=Read-Body
foreach($name in @('Bip001 L Foot','Bip001 R Foot','Bip001 Head')){
    $a=$original.Body.Nodes|Where-Object Name -eq $name
    $b=$modified.Body.Nodes|Where-Object Name -eq $name
    $c=$later.Body.Nodes|Where-Object Name -eq $name
    if(!$a -or !$b){throw ('Missing reference bone '+$name)}
    if($name -like '*Foot' -and [Math]::Abs($a.Position[1]-$b.Position[1]) -gt .0001){throw 'Foot anchor moved'}
    if($name -like '*Head'){
        if($b.Position[1]-$a.Position[1] -lt .2){throw 'Height did not increase'}
        for($axis=0;$axis -lt 3;$axis++){if([Math]::Abs($b.WorldScale[$axis]/$a.WorldScale[$axis]-1) -gt .02){throw 'Head scale changed'}}
    }
    for($axis=0;$axis -lt 3;$axis++){if([Math]::Abs($b.Position[$axis]-$c.Position[$axis]) -gt .0001){throw 'Body deformation accumulated across frames'}}
}
$saved=Get-Content (Join-Path $App 'ChestSettings/1001_0.json') -Raw|ConvertFrom-Json
if([Math]::Abs($saved.Height-1.2) -gt .0001 -or $saved.Fullness -ne 1){throw 'Height/fullness not saved'}
Send @{action='chest';reset=$true}
$reset=Read-Body
foreach($bone in $original.Body.Nodes){
    $restored=$reset.Body.Nodes|Where-Object Name -eq $bone.Name
    for($axis=0;$axis -lt 3;$axis++){
        if([Math]::Abs($bone.Position[$axis]-$restored.Position[$axis]) -gt .0001){throw ('Reset changed bone '+$bone.Name)}
    }
}
Send @{action='selection';value=$false}
'PASS: Native model height, fixed feet, preserved head size, non-accumulating deformation, persisted sliders and reset.'
