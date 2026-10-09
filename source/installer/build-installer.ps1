param(
    [string]$OutputFile,
    [string]$WorkDirectory,
    [switch]$Console
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskSource = Split-Path -Parent $PSScriptRoot
$taskProduct = Split-Path -Parent $taskSource
$taskOutputs = Split-Path -Parent $taskProduct
$taskWorkspace = Split-Path -Parent $taskOutputs
if (-not $OutputFile) { $OutputFile = Join-Path $taskProduct 'build\Patchwork-Setup.exe' }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $taskProduct 'build-setup' }
$OutputFile = [IO.Path]::GetFullPath($OutputFile)
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$taskPayload = Join-Path $WorkDirectory 'payload'
New-Item -ItemType Directory -Path $taskPayload,(Split-Path -Parent $OutputFile) -Force | Out-Null
& (Join-Path $taskSource 'build.ps1') -OutputDirectory $taskPayload
Copy-Item -LiteralPath (Join-Path $taskProduct 'Patchwork.exe.config') -Destination (Join-Path $taskPayload 'Patchwork.exe.config')
Copy-Item -LiteralPath (Join-Path $taskProduct 'README.md') -Destination (Join-Path $taskPayload 'README.md')
Copy-Item -LiteralPath (Join-Path $taskProduct 'PATCH-FORMAT.md') -Destination (Join-Path $taskPayload 'PATCH-FORMAT.md')
Copy-Item -LiteralPath (Join-Path $taskProduct 'THIRD-PARTY.md') -Destination (Join-Path $taskPayload 'THIRD-PARTY.md')
$taskZip = Join-Path $WorkDirectory 'payload.zip'
$taskStream = [IO.File]::Open($taskZip,[IO.FileMode]::Create,[IO.FileAccess]::Write)
$taskArchive = [IO.Compression.ZipArchive]::new($taskStream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskName in @('Patchwork.exe','Patchwork.exe.config','Mono.Cecil.dll','Mono.Cecil.LICENSE.txt','README.md','PATCH-FORMAT.md','THIRD-PARTY.md')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskArchive,(Join-Path $taskPayload $taskName),$taskName,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $taskArchive.Dispose(); $taskStream.Dispose() }
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
$taskWpf = Join-Path $taskFramework 'WPF'
$taskReferences = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskReferences += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $taskWpf $_) }
$taskTarget = if ($Console) { 'exe' } else { 'winexe' }
$taskOptions = @('/nologo','/optimize+','/platform:x64',('/target:' + $taskTarget),('/out:' + $OutputFile),('/win32manifest:' + (Join-Path $taskSource 'app.manifest')),('/resource:' + $taskZip + ',Patchwork.Payload.zip'))
$taskIcon = Join-Path $taskSource 'Patchwork.ico'
if (Test-Path -LiteralPath $taskIcon) { $taskOptions += '/win32icon:' + $taskIcon }
& $taskCompiler @taskOptions @taskReferences (Join-Path $PSScriptRoot 'Installer.cs')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
Write-Output "Built installer $OutputFile"
