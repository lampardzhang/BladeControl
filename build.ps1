$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'src'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', ('/win32icon:' + (Join-Path $PSScriptRoot 'assets\BladeControl.ico')), ('/resource:' + (Join-Path $PSScriptRoot 'assets\BladeControl.ico') + ',BladeControl.App.ico'), ('/out:' + (Join-Path $PSScriptRoot 'BladeControl.exe')), ('/win32manifest:' + (Join-Path $source 'app.manifest')), '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Runtime.Serialization.dll', (Join-Path $source 'RazerHid.cs'), (Join-Path $source 'GpuDevice.cs'), (Join-Path $source 'Controller.cs'), (Join-Path $source 'PerformanceProfiles.cs'), (Join-Path $source 'CpuTemperature.cs'), (Join-Path $source 'ExternalInputGuard.cs'), (Join-Path $source 'BluetoothSleepGuard.cs'), (Join-Path $source 'Program.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output 'Built BladeControl.exe'
