param([Parameter(Mandatory=$true)][string]$SourceDirectory, [string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$taskCecilRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$taskCompatibility = Join-Path $PSScriptRoot 'cecil-csharp5.patch'
& git -C $taskCecilRoot apply --ignore-space-change --check $taskCompatibility
if ($LASTEXITCODE -ne 0) { throw 'Use a clean Mono.Cecil 0.11.6 checkout.' }
& git -C $taskCecilRoot apply --ignore-space-change $taskCompatibility
if ($LASTEXITCODE -ne 0) { throw 'Compatibility patch failed.' }
$taskInputs = @((Join-Path $taskCecilRoot 'ProjectInfo.cs'))
foreach ($taskFolder in @('Mono','Mono.Cecil','Mono.Cecil.Cil','Mono.Cecil.Metadata','Mono.Cecil.PE','Mono.Collections.Generic','Mono.Security.Cryptography')) {
    $taskInputs += Get-ChildItem -LiteralPath (Join-Path $taskCecilRoot $taskFolder) -Filter '*.cs' | ForEach-Object FullName
}
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskCecilOutput = [IO.Path]::GetFullPath($OutputDirectory)
& $taskCompiler /nologo /target:library /optimize+ ('/out:' + (Join-Path $taskCecilOutput 'Mono.Cecil.dll')) @taskInputs
if ($LASTEXITCODE -ne 0) { throw 'Cecil compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskCecilRoot 'LICENSE.txt') -Destination (Join-Path $taskCecilOutput 'Mono.Cecil.LICENSE.txt')
