param([switch]$PackageOnly)
$ErrorActionPreference='Stop'
$toolRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination=Join-Path $toolRoot 'app'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
[IO.Directory]::CreateDirectory($destination) | Out-Null
if (!$PackageOnly) {
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4 compiler was not found on this PC.' }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.ServiceProcess.dll ('/out:'+(Join-Path $destination 'IT Database Builder.exe')) (Join-Path $PSScriptRoot 'ITDatabaseTool.cs')
if ($LASTEXITCODE -ne 0) { throw 'Database builder compilation failed.' }
} elseif (!(Test-Path -LiteralPath (Join-Path $destination 'IT Database Builder.exe'))) { throw 'Build the EXE before packaging it.' }
foreach ($name in @('README.txt','CREDITS.txt','Setup-ITDatabases.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $destination $name) -Force
}
$packageRoot=Join-Path $toolRoot 'output'
[IO.Directory]::CreateDirectory($packageRoot) | Out-Null
$definition=Join-Path $destination 'DatabaseInfo.json'
if(!(Test-Path -LiteralPath $definition)){
    & (Join-Path $destination 'IT Database Builder.exe') --inventory --root $destination | Out-Null
    if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $definition)){throw 'DatabaseInfo.json is missing and legacy TXT import failed.'}
}
$stage=Join-Path $packageRoot ('package-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
foreach($name in @('IT Database Builder.exe','README.txt','CREDITS.txt','Setup-ITDatabases.ps1')) {Copy-Item -LiteralPath (Join-Path $destination $name) -Destination (Join-Path $stage $name)}
$shareableDefinition=Get-Content -LiteralPath $definition -Raw | ConvertFrom-Json
$shareableDefinition.connection.server='127.0.0.1'
$shareableDefinition.connection.port=5432
$shareableDefinition.connection.pgBin=$null
$shareableDefinition.connection.password=''
foreach($game in $shareableDefinition.games) { $game | Add-Member -NotePropertyName databaseFile -NotePropertyValue $null -Force }
$packageJson=$shareableDefinition | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText((Join-Path $stage 'DatabaseInfo.json'),$packageJson,[Text.UTF8Encoding]::new($false))
[IO.Directory]::CreateDirectory((Join-Path $stage 'PG_backup')) | Out-Null
$installerSource=Join-Path $destination 'installs/PostgreSQL v8.3'
foreach($name in @('SETUP.bat','postgresql-8.3.msi','postgresql-8.3-int.msi','vcredist_x86.exe')){
    if(!(Test-Path -LiteralPath (Join-Path $installerSource $name))){throw "Bundled installer is missing $name."}
}
$installerStage=Join-Path $stage 'installs/PostgreSQL v8.3'
[IO.Directory]::CreateDirectory($installerStage) | Out-Null
foreach($name in @('SETUP.bat','postgresql-8.3.msi','postgresql-8.3-int.msi','vcredist_x86.exe')){Copy-Item -LiteralPath (Join-Path $installerSource $name) -Destination (Join-Path $installerStage $name)}
$files=@('IT Database Builder.exe','DatabaseInfo.json','README.txt','CREDITS.txt','Setup-ITDatabases.ps1','PG_backup','installs') | ForEach-Object { Join-Path $stage $_ }
Compress-Archive -LiteralPath $files -DestinationPath (Join-Path $packageRoot 'IT-Database-Setup.zip') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::Open((Join-Path $packageRoot 'IT-Database-Setup.zip'),[IO.Compression.ZipArchiveMode]::Update)
try { if(!$archive.GetEntry('PG_backup/')) { $archive.CreateEntry('PG_backup/') | Out-Null } } finally { $archive.Dispose() }
Write-Output (Join-Path $destination 'IT Database Builder.exe')
