# Optional wrapper. DatabaseInfo.json is the sole persistent definition.
[CmdletBinding()]
param([string]$ToolRoot,[switch]$Plan,[switch]$ListBackups,[switch]$PromptWithWindow)
$ErrorActionPreference='Stop'
if(!$ToolRoot){
    $adjacentExe=Join-Path $PSScriptRoot 'IT Database Builder.exe'
    $ToolRoot=if(Test-Path -LiteralPath $adjacentExe){$PSScriptRoot}else{Join-Path $PSScriptRoot '../app'}
}
$ToolRoot=(Resolve-Path -LiteralPath $ToolRoot).Path
$exe=Join-Path $ToolRoot 'IT Database Builder.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'IT Database Builder.exe is missing. Use the complete tool package.'}
if($PromptWithWindow -or (!$Plan -and !$ListBackups)){
    Start-Process -FilePath $exe -ArgumentList @('--root',('"'+$ToolRoot+'"')) -WindowStyle Normal
    return
}
$mode=if($ListBackups){'--inventory'}else{'--plan'}
& $exe $mode --root $ToolRoot | Write-Output
if($LASTEXITCODE -ne 0){throw 'Preview failed. Check DatabaseInfo.json and the local PostgreSQL connection.'}
