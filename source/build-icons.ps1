param(
    [string]$WorkDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build-icons'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'Patchwork.ico')
)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'The Windows .NET Framework 4.x compiler is required.' }
$taskWpf = Join-Path $taskFramework 'WPF'
$taskReferences = @('System.dll','System.Core.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskReferences += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $taskWpf $_) }
$taskOutputDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Path $taskOutputDirectory -Force | Out-Null
$taskExecutable = Join-Path $taskOutputDirectory 'GenerateIcons.exe'
$taskIconSource = Join-Path $PSScriptRoot 'tools\GenerateIcons.cs'
$taskAppIcons = Join-Path $PSScriptRoot 'AppIcons.cs'
if (-not (Test-Path -LiteralPath $taskAppIcons)) { throw "Missing renderer: $taskAppIcons" }
& $taskCompiler /nologo /optimize+ /platform:x64 /target:exe ("/out:" + $taskExecutable) @taskReferences $taskAppIcons $taskIconSource
if ($LASTEXITCODE -ne 0) { throw "Icon generator compilation failed with exit code $LASTEXITCODE." }
$taskTarget = [System.IO.Path]::GetFullPath($OutputPath)
& $taskExecutable $taskTarget
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed with exit code $LASTEXITCODE." }
