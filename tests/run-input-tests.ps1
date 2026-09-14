param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$files=@('RazerHid.cs','Controller.cs','CpuTemperature.cs','GpuDevice.cs','PerformanceProfiles.cs','ExternalInputGuard.cs','BluetoothSleepGuard.cs') | ForEach-Object {Join-Path $root ('src\'+$_)}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$output\InputGuardTests.exe" /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll @files (Join-Path $PSScriptRoot 'InputGuardTests.cs')
if($LASTEXITCODE -ne 0){throw 'Build failed'}
& (Join-Path $output 'InputGuardTests.exe') $output
if($LASTEXITCODE -ne 0){throw 'Input guard tests failed'}
