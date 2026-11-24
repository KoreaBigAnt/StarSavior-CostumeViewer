param([Parameter(Mandatory=$true)][string]$App,[int]$Seed=20261125,[int]$Count=10)
$ErrorActionPreference='Stop';$App=[IO.Path]::GetFullPath($App)
$command=Join-Path $App 'viewer-command.json';$ready=Join-Path $App 'viewer-ready.json'
function Wait-For($predicate){$end=[DateTime]::UtcNow.AddSeconds(45);while(!(& $predicate)){if([DateTime]::UtcNow -gt $end){throw 'Viewer did not respond'};Start-Sleep -Milliseconds 60}}
function Send($value){Wait-For {!(Test-Path $command)};$temp=$command+'.new';$value|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $temp -Encoding utf8;Move-Item -LiteralPath $temp -Destination $command;Wait-For {!(Test-Path $command)}}
Wait-For {Test-Path $ready}
$random=[Random]::new($Seed)
$units=@(Get-Content (Join-Path $App 'costumes.json') -Raw|ConvertFrom-Json|Where-Object {$_.Skin -eq 0 -and $_.Body -and $_.Unit -ge 1000 -and $_.Unit -lt 1100 -and $_.CharacterName -ne "PLAYER_NAME"}|Sort-Object Unit|ForEach-Object {[pscustomobject]@{Unit=$_.Unit;Name=$_.CharacterName;Key=$random.Next()}}|Sort-Object Key|Select-Object -First $Count)
$units|Select-Object Unit,Name|ConvertTo-Json|Set-Content (Join-Path $App 'review-plan.json') -Encoding utf8
$cases=@(@{Name='base';Height=1;Fullness=0},@{Name='min';Height=.85;Fullness=0},@{Name='max';Height=1.2;Fullness=0},@{Name='fat';Height=1;Fullness=1.5},@{Name='minfat';Height=.85;Fullness=1.5},@{Name='maxfat';Height=1.2;Fullness=1.5})
foreach($unit in $units){
    Send @{action='selection';value=$false}
    if(Test-Path $ready){Remove-Item -LiteralPath $ready}
    Send @{action='load';unit=$unit.Unit;skin=0}
    Wait-For {try{(Get-Content $ready -Raw|ConvertFrom-Json).Unit -eq $unit.Unit}catch{$false}}
    Send @{action='selection';value=$true}
    foreach($case in $cases){
        Send @{action='chest';height=$case.Height;fullness=$case.Fullness}
        $result=Join-Path $App 'viewer-body-review-result.json';if(Test-Path $result){Remove-Item -LiteralPath $result}
        Send @{action='body-review';name=($unit.Unit.ToString()+'-'+$case.Name);frame=($case.Name -eq 'base')}
        Wait-For {Test-Path $result}
        Write-Output ('Captured '+$unit.Unit+' '+$unit.Name+' '+$case.Name)
    }
}
Send @{action='selection';value=$false}
'Completed random-character geometry and front/side capture pass.'
