param([Parameter(Mandatory=$true)][string]$Viewer,[Parameter(Mandatory=$true)][string]$Managed,[Parameter(Mandatory=$true)][string]$Reference)
$ErrorActionPreference='Stop'
$cecil=Join-Path $PSScriptRoot 'vendor/Mono.Cecil.dll'
if(!(Test-Path $cecil)){$cecil='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/Extensions/TestPlatform/Extensions/Mono.Cecil.dll'}
Add-Type -Path $cecil
function Types($types){foreach($type in $types){$type;Types $type.NestedTypes}}
$module=[Mono.Cecil.ModuleDefinition]::ReadModule([IO.Path]::GetFullPath($Viewer))
$literalNames=[Collections.Generic.HashSet[string]]::new()
foreach($type in (Types $module.Types)){foreach($method in $type.Methods){if($method.HasBody){foreach($i in $method.Body.Instructions){if($i.OpCode -eq [Mono.Cecil.Cil.OpCodes]::Ldstr){$null=$literalNames.Add([string]$i.Operand)}}}}}
foreach($file in Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.cs'){foreach($match in [regex]::Matches([IO.File]::ReadAllText($file.FullName),'"([^"\r\n]+)"')){$null=$literalNames.Add($match.Groups[1].Value)}}
$literalHeads=[Collections.Generic.HashSet[string]]::new()
foreach($value in $literalNames){$null=$literalHeads.Add($value.Split(',')[0])}
$typeNames=@{};$data=@{Methods=@{};Fields=@{};FieldRows=@{};Tokens=@{}}
$wantedRows=@(2831,2832,2835,2837,2842,2843,2968,2969,2659,2654,2655)
$contractPath=Join-Path $PSScriptRoot 'PublicSymbols.contract.json'
$records=[Collections.Generic.List[object]]::new()
$counts=@{}
function Record($type,$assembly,$path){
    $methods=[Collections.Generic.List[object]]::new();$fields=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt $type.Methods.Count;$i++){
        $member=$type.Methods[$i];$token=($assembly -eq 'NKC' -and $member.MetadataToken.RID -eq 32928) -or ($assembly -eq 'Star.Templets' -and $member.MetadataToken.RID -in @(6060,6061,6367))
        if(($literalNames.Contains($member.Name) -and $member.Name -match '[\u0D00-\u0D7F]') -or $token){$methods.Add(@{Index=$i;Name=$member.Name;Token=if($token){$member.MetadataToken.ToInt32()}else{0}})}
    }
    for($i=0;$i -lt $type.Fields.Count;$i++){
        $member=$type.Fields[$i];$row=($assembly -eq 'Star.Templets' -and $member.MetadataToken.RID -in $wantedRows) -or ($assembly -eq 'NKC' -and $member.MetadataToken.RID -eq 35432)
        if(($literalNames.Contains($member.Name) -and $member.Name -match '[\u0D00-\u0D7F]') -or $row){$fields.Add(@{Index=$i;Name=$member.Name;Row=if($row){[int]$member.MetadataToken.RID}else{0}})}
    }
    $alias=$literalHeads.Contains($type.FullName) -or $literalHeads.Contains($type.FullName.Replace('/','+'))
    if($alias -or $methods.Count -or $fields.Count){$records.Add(@{Assembly=$assembly;Path=@($path);Alias=if($alias){$type.FullName}else{''};Methods=@($methods.ToArray());Fields=@($fields.ToArray())})}
    for($i=0;$i -lt $type.NestedTypes.Count;$i++){Record $type.NestedTypes[$i] $assembly (@($path)+@($i))}
}
if(Test-Path (Join-Path $Reference 'NKC.dll')){
    foreach($name in @('NKC','Star.Templets','NKM.Templets','NKM','FX','FX.Define','Bs.ServerLibrary')){
        $path=Join-Path $Reference ($name+'.dll');if(!(Test-Path $path)){continue}
        $old=[Mono.Cecil.ModuleDefinition]::ReadModule($path)
        try{$counts[$name]=$old.Types.Count;for($i=0;$i -lt $old.Types.Count;$i++){Record $old.Types[$i] $name @($i)}}finally{$old.Dispose()}
    }
    @{Counts=$counts;Types=@($records.ToArray())}|ConvertTo-Json -Depth 15 -Compress|Set-Content -LiteralPath $contractPath -Encoding utf8
}
if(!(Test-Path $contractPath)){throw 'The public reflection contract is missing.'}
$contract=Get-Content $contractPath -Raw|ConvertFrom-Json
foreach($group in ($contract.Types|Group-Object Assembly)){
    $new=[Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $Managed ($group.Name+'.dll')))
    try{
        if($new.Types.Count -ne $contract.Counts.($group.Name)){throw ('Unsupported type layout in '+$group.Name)}
        foreach($record in $group.Group){
            $target=$new.Types[[int]$record.Path[0]]
            for($i=1;$i -lt $record.Path.Count;$i++){$target=$target.NestedTypes[[int]$record.Path[$i]]}
            if($record.Alias){$typeNames[$record.Alias]=$target.FullName;$typeNames[$record.Alias.Replace('/','+')]=$target.FullName.Replace('/','+')}
            $owner=$target.FullName.Replace('/','+')
            foreach($member in $record.Methods){$matched=$target.Methods[[int]$member.Index];if($literalNames.Contains($member.Name)){$data.Methods[$owner+'|'+$member.Name]=$matched.Name};if($member.Token){$data.Tokens[$group.Name+'|'+$member.Token]=$matched.MetadataToken.ToInt32()}}
            foreach($member in $record.Fields){$matched=$target.Fields[[int]$member.Index];if($literalNames.Contains($member.Name)){$data.Fields[$owner+'|'+$member.Name]=$matched.Name};if($member.Row){$data.FieldRows[$owner+'|'+$member.Row]=[int]$matched.MetadataToken.RID}}
        }
    }finally{$new.Dispose()}
}

$shim=$module.GetType('StarSaviorCostumeViewer.GameSymbols');$count=0;$calls=0
try{
    foreach($type in (Types $module.Types)){foreach($method in $type.Methods){
        if(!$method.HasBody -or $type.FullName.StartsWith('StarSaviorCostumeViewer.GameSymbols')){continue}
        foreach($instruction in $method.Body.Instructions){
            if($instruction.OpCode -eq [Mono.Cecil.Cil.OpCodes]::Ldstr){
                $value=[string]$instruction.Operand;$head=$value.Split(',')[0]
                if($typeNames.ContainsKey($value)){$instruction.Operand=$typeNames[$value];$count++}
                elseif($head -ne $value -and $typeNames.ContainsKey($head)){$instruction.Operand=$typeNames[$head]+$value.Substring($head.Length);$count++}
            }
            $target=$instruction.Operand
            if($target -isnot [Mono.Cecil.MethodReference]){continue}
            if(($target.DeclaringType.FullName -eq 'System.Type' -and $target.Name -in @('GetMethod','GetField')) -or ($target.DeclaringType.FullName -eq 'System.Reflection.Module' -and $target.Name -eq 'ResolveMethod')){
                $replacement=$shim.Methods|Where-Object {$_.Name -eq $target.Name -and $_.Parameters.Count -eq $target.Parameters.Count+1}|Where-Object {
                    $valid=$true;for($p=0;$p -lt $target.Parameters.Count;$p++){if($_.Parameters[$p+1].ParameterType.FullName -ne $target.Parameters[$p].ParameterType.FullName){$valid=$false}};$valid
                }|Select-Object -First 1
                if($replacement){$instruction.OpCode=[Mono.Cecil.Cil.OpCodes]::Call;$instruction.Operand=$module.ImportReference($replacement);$calls++}
            }
        }
    }}
    $json=$data|ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($Viewer)) 'public-symbols.json'),$json,[Text.UTF8Encoding]::new($false))
    $module.Write($Viewer+'.mapped')
}finally{$module.Dispose()}
Move-Item -LiteralPath ($Viewer+'.mapped') -Destination $Viewer -Force
"Mapped $count types and $calls reflection calls for the public client."
