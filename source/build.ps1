param([string]$OutputDirectory = (Split-Path -Parent $PSScriptRoot), [switch]$Console, [switch]$Tests)
$ErrorActionPreference = 'Stop'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'The Windows .NET Framework 4.x compiler is required.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskTarget = if ($Console) { 'exe' } else { 'winexe' }
$taskOutput = Join-Path $OutputDirectory 'Patchwork.exe'
$taskWpf = Join-Path $taskFramework 'WPF'
$taskReferences = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskReferences += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $taskWpf $_) }
$taskReferences += '/reference:' + (Join-Path $PSScriptRoot 'lib\Mono.Cecil.dll')
$taskSources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object { $Tests -or $_.Name -notin @('SelfTests.cs','ManagedTests.cs','TestFixtures.cs','UpdaterTests.cs','SelectorTests.cs','SourceTests.cs','PatchUpdateTests.cs','ThemeTests.cs','PresentationTests.cs') } | Select-Object -ExpandProperty FullName
$taskOptions = @('/nologo','/optimize+','/platform:x64',('/target:' + $taskTarget),('/out:' + $taskOutput),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',Patchwork.MainWindow.xaml'))
$taskIcon = Join-Path $PSScriptRoot 'Patchwork.ico'
if ($Tests) { $taskOptions += '/define:TEST_BUILD' }
if (Test-Path -LiteralPath $taskIcon) { $taskOptions += '/win32icon:' + $taskIcon }
& $taskCompiler @taskOptions @taskReferences @taskSources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib\Mono.Cecil.dll') -Destination (Join-Path $OutputDirectory 'Mono.Cecil.dll')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lib\Mono.Cecil.LICENSE.txt') -Destination (Join-Path $OutputDirectory 'Mono.Cecil.LICENSE.txt')
Write-Output "Built $taskOutput"
