param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$testOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $testOutput 'UiTests.exe'
$icon = Join-Path $root 'assets\BladeControl.ico'
& $compiler /nologo /target:exe /main:UiTests /platform:x64 /codepage:65001 "/out:$testExe" "/resource:$icon,BladeControl.App.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll /r:System.Core.dll (Join-Path $source 'RazerHid.cs') (Join-Path $source 'GpuDevice.cs') (Join-Path $source 'Controller.cs') (Join-Path $source 'CpuTemperature.cs') (Join-Path $source 'PerformanceProfiles.cs') (Join-Path $source 'ExternalInputGuard.cs') (Join-Path $source 'BluetoothSleepGuard.cs') (Join-Path $source 'Program.cs') (Join-Path $PSScriptRoot 'UiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test build failed' }
& $testExe $testOutput
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed' }
