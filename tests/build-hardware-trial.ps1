$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Join-Path $root 'src'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$files=@('RazerHid.cs','Controller.cs','CpuTemperature.cs','GpuDevice.cs','PerformanceProfiles.cs') | ForEach-Object {Join-Path $source $_}
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$root\HardwareTrial.exe" "/win32manifest:$source\app.manifest" /r:System.Runtime.Serialization.dll @files (Join-Path $PSScriptRoot 'HardwareTrial.cs')
if($LASTEXITCODE -ne 0){throw 'Hardware trial build failed'}
