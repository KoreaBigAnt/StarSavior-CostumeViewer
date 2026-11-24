param([string]$App)
$ErrorActionPreference='Stop';$App=[IO.Path]::GetFullPath($App)
function Wait-For($predicate){$end=[DateTime]::UtcNow.AddSeconds(40);while(!(& $predicate)){if([DateTime]::UtcNow -gt $end){throw 'Viewer response timeout'};Start-Sleep -Milliseconds 80}}
function Send($v){$p=Join-Path $App 'viewer-command.json';Wait-For {!(Test-Path $p)};$v|ConvertTo-Json -Depth 8|Set-Content ($p+'.tmp') -Encoding utf8;Move-Item ($p+'.tmp') $p;Wait-For {!(Test-Path $p)}}
function Status{$p=Join-Path $App 'viewer-status.json';if(Test-Path $p){Remove-Item $p};Send @{action='status'};Wait-For {Test-Path $p};Get-Content $p -Raw|ConvertFrom-Json}
Wait-For {Test-Path (Join-Path $App 'viewer-ready.json')}
Send @{action='skill';index=2}
Wait-For {$s=Status;!$s.Playing -and $s.Completed -ge 1}
Send @{action='chest';height=1.1;fullness=1.5}
$p=Join-Path $App 'viewer-profile-result.json';if(Test-Path $p){Remove-Item $p};Send @{action='profile';zip=$true};Wait-For {Test-Path $p}
$result=Get-Content $p -Raw|ConvertFrom-Json
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($result.Output)
try{
 $entry=$zip.Entries|Where-Object {$_.FullName.EndsWith('/appearance-profile.json')};if(!$entry){throw 'ZIP profile missing'}
 $reader=[IO.StreamReader]::new($entry.Open());try{$profile=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
 if([Math]::Abs($profile.ChestShape.Height-1.1)-gt .001 -or $profile.ChestShape.Fullness -ne 1.5){throw 'ZIP shape differs'}
 if($zip.Entries|Where-Object {$_.FullName -match '\.(md|txt|rst)$'}){throw 'ZIP includes explanation files'}
 'PASS: Public skill completed; game ZIP exported height and 150% fullness without explanation files.'
}finally{$zip.Dispose()}
