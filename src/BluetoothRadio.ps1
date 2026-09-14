param([ValidateSet('State','Off','On')][string]$Action='State')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Devices.Radios.Radio,Windows.System.Devices,ContentType=WindowsRuntime] | Out-Null
$method=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'} | Select-Object -First 1
function Await-Radio($Operation,[Type]$ResultType) {
    $task=$method.MakeGenericMethod($ResultType).Invoke($null,@($Operation))
    if(-not $task.Wait(5000)){throw 'Bluetooth API timed out'}
    return $task.Result
}
$radios=@(Await-Radio ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]]) | Where-Object {$_.Kind -eq 'Bluetooth'})
if($radios.Count -eq 0){if($Action -eq 'On'){throw 'Bluetooth radio unavailable for restore'};'Off';exit 0}
if($radios.Count -ne 1){throw 'Expected exactly one Bluetooth radio; no radios changed'}
$radio=$radios[0]
if($Action -eq 'State'){if($radio.State -eq 'On'){'On'}else{'Off'};exit 0}
$access=Await-Radio ([Windows.Devices.Radios.Radio]::RequestAccessAsync()) ([Windows.Devices.Radios.RadioAccessStatus])
if($access -ne 'Allowed'){throw "Radio access rejected: $access"}
$desired=[Windows.Devices.Radios.RadioState]::$Action
$result=Await-Radio ($radio.SetStateAsync($desired)) ([Windows.Devices.Radios.RadioAccessStatus])
if($result -ne 'Allowed'){throw "Radio state change rejected: $result"}
$deadline=[DateTime]::UtcNow.AddSeconds(3)
while($radio.State -ne $desired -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 100}
if($radio.State -ne $desired){throw 'Bluetooth state readback did not match request'}
$radio.State.ToString()
