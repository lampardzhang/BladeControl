param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$testOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
$source = Join-Path $PSScriptRoot '..\src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $testOutput 'Tests.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$testExe" /r:System.Runtime.Serialization.dll (Join-Path $source 'GpuDevice.cs') (Join-Path $source 'Controller.cs') (Join-Path $source 'PerformanceProfiles.cs') (Join-Path $source 'CpuTemperature.cs') (Join-Path $source 'RazerHid.cs') (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& $testExe $testOutput
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
