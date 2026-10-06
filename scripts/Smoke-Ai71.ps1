#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'SocialTelemetry.Api\SocialTelemetry.Api.csproj'
$baseUri = 'http://127.0.0.1:5059/ai-connection/chatgpt'
$script:appProcess = $null
$script:apiProcess = $null
$script:appStarted = $false
$script:outputObserver = $null
$script:refreshCount = 0
$script:currentCheck = 'Startup'
$exitCode = 1
$results = [ordered]@{}
foreach ($name in @(
    'Startup', 'Begin connection', 'Browser OAuth/callback', 'Connected status',
    'Inference permission', 'Model discovery', 'Model selection',
    'Neutral selected-model metadata', 'Tiny inference', 'Restart/session persistence',
    'Natural refresh', 'Local disconnect', 'Remote revocation confirmed', 'Reconnect',
    'Safe disconnected error', 'Local-request protection', 'Process cleanup'
)) {
    $results[$name] = [pscustomobject]@{ Check = $name; Status = 'NOT EXERCISED'; Note = 'Not reached.' }
}
$results['Neutral selected-model metadata'].Note = 'No HTTP endpoint for IAiClient.GetSelectedModelAsync().'
$results['Tiny inference'].Note = 'No HTTP endpoint for IAiClient.GenerateTextAsync().'
$results['Natural refresh'].Note = 'No natural refresh observed; expiry is never manipulated.'

function Set-SmokeResult {
    param([string]$Name, [string]$Status, [string]$Note)
    $results[$Name].Status = $Status
    $results[$Name].Note = $Note
}

function Stop-SmokeCheck {
    param([string]$Note)
    Set-SmokeResult $script:currentCheck 'FAIL' $Note
    throw 'Smoke check failed.'
}

function Get-ResponseField {
    param($Body, [string]$Name)
    if ($null -eq $Body) { return $null }
    $property = $Body.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Invoke-AiRequest {
    param(
        [string]$Method = 'GET',
        [string]$Path = '/status',
        $Body = $null,
        [switch]$WithoutLocalHeader,
        [int]$TimeoutMilliseconds = 90000
    )
    # Only fixed local routes are used. Redirects, proxies, and raw response logging are disabled.
    $request = [System.Net.HttpWebRequest]::Create($baseUri + $Path)
    $request.Method = $Method
    $request.Accept = 'application/json'
    $request.AllowAutoRedirect = $false
    $request.Proxy = $null
    $request.Timeout = $TimeoutMilliseconds
    $request.ReadWriteTimeout = $TimeoutMilliseconds
    if (-not $WithoutLocalHeader) { $request.Headers['X-SocialTelemetry-Local'] = '1' }
    if ($null -ne $Body) {
        $json = ConvertTo-Json -InputObject $Body -Compress
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $request.ContentType = 'application/json'
        $request.ContentLength = $bytes.Length
        $stream = $request.GetRequestStream()
        try { $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
    }

    $response = $null
    try {
        try { $response = $request.GetResponse() }
        catch [System.Net.WebException] {
            if ($null -eq $_.Exception.Response) { throw 'Local HTTP request could not complete.' }
            $response = $_.Exception.Response
        }
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        try { $content = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $parsed = $null
        try { $parsed = ConvertFrom-Json -InputObject $content }
        catch { throw 'Local API returned an unreadable JSON response.' }
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            ContentType = $response.ContentType
            Body = $parsed
        }
    }
    finally { if ($null -ne $response) { $response.Dispose() } }
}

function Assert-HttpSuccess {
    param($Response)
    if ($Response.StatusCode -lt 200 -or $Response.StatusCode -ge 300) {
        # Never display arbitrary provider text or response fields, including ProblemDetails.detail.
        $knownCodes = @(
            'NotConnected', 'AuthorizationDenied', 'InvalidCallback', 'InvalidIdentity',
            'InferencePermissionMissing', 'ReconnectRequired', 'RefreshFailed', 'InvalidClient',
            'UsageLimitReached', 'ModelNotSelected', 'ModelUnavailable', 'ProviderUnavailable',
            'ProviderRejected', 'MalformedResponse', 'IncompleteResponse', 'StorageUnavailable',
            'ConnectionBusy', 'BrowserUnavailable', 'LocalRequestRequired'
        )
        $code = Get-ResponseField $Response.Body 'code'
        $safeCode = 'Unrecognized or absent error category'
        if ($code -is [string] -and $knownCodes -ccontains $code) { $safeCode = $code }
        Stop-SmokeCheck "HTTP $($Response.StatusCode); $safeCode. Stopped before further provider actions."
    }
}

function Find-OwnedApiProcess {
    if ($null -ne $script:apiProcess -or -not $script:appStarted) { return }
    $listeners = @(Get-NetTCPConnection -LocalPort 5059 -State Listen -ErrorAction SilentlyContinue)
    $ownerIds = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($ownerIds.Count -eq 0) { return }
    if ($ownerIds.Count -ne 1 -or $script:appProcess.HasExited) {
        throw 'The API listener ownership could not be verified.'
    }

    $candidate = [System.Diagnostics.Process]::GetProcessById($ownerIds[0])
    $verified = $false
    try {
        # Retain the process handle so PID reuse cannot redirect later cleanup to another process.
        $null = $candidate.Handle
        $ancestorId = $candidate.Id
        $childStartedAt = $candidate.StartTime
        $visited = @{}
        while ($ancestorId -ne $script:appProcess.Id) {
            if ($visited.ContainsKey($ancestorId)) { throw 'Process ancestry contains a cycle.' }
            $visited[$ancestorId] = $true
            $ancestor = Get-CimInstance Win32_Process -Filter "ProcessId = $ancestorId"
            if ($null -eq $ancestor -or $ancestor.CreationDate -gt $childStartedAt -or
                $ancestor.CreationDate -lt $script:appProcess.StartTime) {
                throw 'The API listener is not a verified descendant of the runner.'
            }
            $childStartedAt = $ancestor.CreationDate
            $ancestorId = [int]$ancestor.ParentProcessId
        }
        if ($script:appProcess.HasExited -or $candidate.HasExited -or
            $childStartedAt -lt $script:appProcess.StartTime) {
            throw 'The API listener ownership changed during verification.'
        }
        $script:apiProcess = $candidate
        $verified = $true
    }
    finally {
        if (-not $verified) { $candidate.Dispose() }
    }
}

function Start-SmokeApp {
    if (@(Get-NetTCPConnection -LocalPort 5059 -State Listen -ErrorAction SilentlyContinue).Count -gt 0) {
        Stop-SmokeCheck 'Port 5059 is already occupied. Stop that application yourself; nothing was killed.'
    }
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = (Get-Command dotnet -ErrorAction Stop).Source
    $startInfo.Arguments = 'run --project "' + $projectPath + '" --launch-profile http --no-build'
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $script:appProcess = New-Object System.Diagnostics.Process
    $script:appProcess.StartInfo = $startInfo
    $script:outputObserver = New-Object SocialTelemetry.Ai71OutputObserver($script:appProcess)
    if (-not $script:appProcess.Start()) { Stop-SmokeCheck 'The API process could not start.' }
    $script:appStarted = $true
    $script:appProcess.BeginOutputReadLine()
    $script:appProcess.BeginErrorReadLine()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($script:appProcess.HasExited) {
            Stop-SmokeCheck 'The API exited during startup. Verify the existing database, migrations, and build separately.'
        }
        $ready = $null
        try { $ready = Invoke-AiRequest -TimeoutMilliseconds 1000 }
        catch { }
        if ($null -ne $ready -and $ready.StatusCode -eq 200 -and
            (Get-ResponseField $ready.Body 'connected') -is [bool]) {
            Find-OwnedApiProcess
            if ($null -eq $script:apiProcess -or $script:apiProcess.HasExited) {
                Stop-SmokeCheck 'The listening API process could not be tracked safely.'
            }
            return
        }
        Start-Sleep -Milliseconds 300
    }
    Stop-SmokeCheck 'The API did not become ready within 60 seconds. Verify the existing database and build separately.'
}

function Stop-SmokeApp {
    if ($null -eq $script:appProcess) { return }
    $stopped = $false
    try {
        if (-not $script:appStarted) {
            $stopped = $true
            return
        }
        # Startup may have failed before ownership was captured. Never stop an unverified listener.
        try { Find-OwnedApiProcess }
        catch { }
        # Kill only our API and dotnet run, never descendants such as the user's system browser.
        try {
            if ($null -ne $script:apiProcess -and -not $script:apiProcess.HasExited) {
                $script:apiProcess.Kill()
                if (-not $script:apiProcess.WaitForExit(10000)) { throw 'Owned API process did not exit.' }
            }
        }
        finally {
            if (-not $script:appProcess.HasExited) {
                $script:appProcess.Kill()
                if (-not $script:appProcess.WaitForExit(10000)) { throw 'Owned dotnet run process did not exit.' }
            }
        }
        $stopped = $true
        $script:refreshCount += $script:outputObserver.RefreshCount
    }
    finally {
        if ($stopped) {
            if ($null -ne $script:apiProcess) { $script:apiProcess.Dispose() }
            $script:apiProcess = $null
            $script:appProcess.Dispose()
            $script:appProcess = $null
            $script:appStarted = $false
            $script:outputObserver = $null
        }
    }
}

function Confirm-BrowserOAuth {
    Write-Host 'Finish ChatGPT authorization and plan-usage consent in the system browser.'
    Write-Host 'Wait for a successful local callback. Never paste its URL, codes, or tokens here.'
    if ((Read-Host 'Type DONE after success, or anything else to stop') -cne 'DONE') {
        Stop-SmokeCheck 'Browser authorization was not confirmed.'
    }
}

function Get-ConnectedStatus {
    $response = Invoke-AiRequest
    Assert-HttpSuccess $response
    $connected = Get-ResponseField $response.Body 'connected'
    if ($connected -isnot [bool] -or -not $connected) {
        Stop-SmokeCheck 'The connection is not connected.'
    }
    return $response.Body
}

function Assert-InferencePermission {
    param($Status)
    $permitted = Get-ResponseField $Status 'inferencePermissionAvailable'
    if ($permitted -isnot [bool] -or -not $permitted) {
        Stop-SmokeCheck 'Inference permission is unavailable. No consent retry was attempted.'
    }
}

function Get-ModelCatalog {
    $response = Invoke-AiRequest -Path '/models'
    Assert-HttpSuccess $response
    $models = @(Get-ResponseField $response.Body 'models')
    if ($models.Count -eq 0 -or $null -eq $models[0]) { Stop-SmokeCheck 'The model catalog is empty.' }
    foreach ($model in $models) {
        if ([string]::IsNullOrWhiteSpace((Get-ResponseField $model 'id')) -or
            [string]::IsNullOrWhiteSpace((Get-ResponseField $model 'displayName'))) {
            Stop-SmokeCheck 'The model catalog lacks usable IDs or display names.'
        }
    }
    return $response.Body
}

function Assert-SafeProblem {
    param($Response, [int]$StatusCode, [string]$Code, [string]$Detail)
    $body = $Response.Body
    $allowedFields = @('type', 'title', 'status', 'detail', 'instance', 'code', 'traceId')
    $unexpected = @($body.PSObject.Properties.Name | Where-Object { $_ -notin $allowedFields })
    if ($Response.StatusCode -ne $StatusCode -or $Response.ContentType -notlike 'application/problem+json*' -or
        (Get-ResponseField $body 'status') -ne $StatusCode -or
        (Get-ResponseField $body 'code') -cne $Code -or
        (Get-ResponseField $body 'title') -cne 'AI connection or request failed.' -or
        (Get-ResponseField $body 'detail') -cne $Detail -or $unexpected.Count -ne 0) {
        Stop-SmokeCheck "Expected safe HTTP $StatusCode ProblemDetails with code $Code was not received."
    }
}

try {
    if ($env:OS -ne 'Windows_NT') { Stop-SmokeCheck 'This harness requires Windows PowerShell or PowerShell on Windows.' }
    if (-not (Test-Path -LiteralPath $projectPath) -or
        -not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'SocialTelemetry.slnx'))) {
        Stop-SmokeCheck 'Repository root could not be located relative to scripts/Smoke-Ai71.ps1.'
    }

    # PowerShell event callbacks need a runspace. This observer discards output on .NET worker threads.
    if (-not ('SocialTelemetry.Ai71OutputObserver' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Threading;
namespace SocialTelemetry
{
    public sealed class Ai71OutputObserver
    {
        private int refreshCount;
        public int RefreshCount { get { return Interlocked.CompareExchange(ref refreshCount, 0, 0); } }
        public Ai71OutputObserver(Process process)
        {
            process.OutputDataReceived += Observe;
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { };
        }
        private void Observe(object sender, DataReceivedEventArgs args)
        {
            if (args.Data != null && args.Data.IndexOf("ChatGPT credentials refreshed for connection ", StringComparison.Ordinal) >= 0)
                Interlocked.Increment(ref refreshCount);
        }
    }
}
'@
    }

    Write-Host 'AI 7.1 live smoke test. Requires the current build and configured PostgreSQL database.'
    Write-Host 'OAuth is manual twice. Results are in memory; no raw logs or credentials are displayed or saved.'
    Start-SmokeApp
    Set-SmokeResult 'Startup' 'PASS' 'Owned API process is reachable on 127.0.0.1:5059.'

    $script:currentCheck = 'Begin connection'
    $begin = Invoke-AiRequest -Method 'POST' -Path '/connect' -Body @{}
    Assert-HttpSuccess $begin
    if ((Get-ResponseField $begin.Body 'browserOpened') -cne $true) { Stop-SmokeCheck 'The system browser was not opened.' }
    Set-SmokeResult 'Begin connection' 'PASS' 'HTTP success; browserOpened=true.'

    $script:currentCheck = 'Browser OAuth/callback'
    Confirm-BrowserOAuth
    $status = Get-ConnectedStatus
    Set-SmokeResult 'Browser OAuth/callback' 'PASS' 'User confirmed callback; connected state verified.'
    Set-SmokeResult 'Connected status' 'PASS' 'connected=true.'
    $script:currentCheck = 'Inference permission'
    Assert-InferencePermission $status
    Set-SmokeResult 'Inference permission' 'PASS' 'inferencePermissionAvailable=true.'
    $connectionId = Get-ResponseField $status 'connectionId'

    $script:currentCheck = 'Model discovery'
    $catalog = Get-ModelCatalog
    Set-SmokeResult 'Model discovery' 'PASS' 'Live nonempty catalog returned.'
    $models = @(Get-ResponseField $catalog 'models')
    for ($index = 0; $index -lt $models.Count; $index++) {
        Write-Host ('{0}. {1} - {2}' -f ($index + 1), (Get-ResponseField $models[$index] 'id'), (Get-ResponseField $models[$index] 'displayName'))
    }
    $script:currentCheck = 'Model selection'
    $choice = 0
    $answer = Read-Host 'Select a model by its displayed number'
    if (-not [int]::TryParse($answer, [ref]$choice) -or $choice -lt 1 -or $choice -gt $models.Count) {
        Stop-SmokeCheck 'Invalid model selection; no selection request was sent.'
    }
    $modelId = Get-ResponseField $models[$choice - 1] 'id'
    Assert-HttpSuccess (Invoke-AiRequest -Method 'PUT' -Path '/model' -Body @{ modelId = $modelId })
    $catalog = Get-ModelCatalog
    if ((Get-ResponseField $catalog 'selectedModel') -cne $modelId -or
        (Get-ResponseField $catalog 'selectedModelAvailable') -cne $true) { Stop-SmokeCheck 'The chosen model was not saved as available.' }
    Set-SmokeResult 'Model selection' 'PASS' 'Exact catalog ID saved and rechecked as available.'

    $script:currentCheck = 'Restart/session persistence'
    Stop-SmokeApp
    Start-SmokeApp
    $status = Get-ConnectedStatus
    Assert-InferencePermission $status
    $catalog = Get-ModelCatalog
    if ((Get-ResponseField $status 'connectionId') -cne $connectionId -or
        (Get-ResponseField $status 'selectedModel') -cne $modelId -or
        (Get-ResponseField $catalog 'selectedModel') -cne $modelId -or
        (Get-ResponseField $catalog 'selectedModelAvailable') -cne $true) { Stop-SmokeCheck 'Connection or selected model did not survive restart.' }
    Set-SmokeResult 'Restart/session persistence' 'PASS' 'Same connection and model; permission and live catalog verified.'

    $script:currentCheck = 'Local disconnect'
    $disconnect = Invoke-AiRequest -Method 'DELETE' -Path ''
    Assert-HttpSuccess $disconnect
    $response = Invoke-AiRequest
    Assert-HttpSuccess $response
    $connected = Get-ResponseField $response.Body 'connected'
    if ($connected -isnot [bool] -or $connected) { Stop-SmokeCheck 'Connection remained connected after disconnect.' }
    Set-SmokeResult 'Local disconnect' 'PASS' 'Disconnect succeeded; connected=false.'
    $revoked = Get-ResponseField $disconnect.Body 'remoteRevocationConfirmed'
    if ($revoked -is [bool] -and $revoked) { Set-SmokeResult 'Remote revocation confirmed' 'PASS' 'remoteRevocationConfirmed=true.' }
    elseif ($revoked -is [bool] -and -not $revoked) { Set-SmokeResult 'Remote revocation confirmed' 'NOT EXERCISED' 'Advisory: remoteRevocationConfirmed=false; confirmation unavailable. Local clearing passed.' }
    else { Set-SmokeResult 'Remote revocation confirmed' 'NOT EXERCISED' 'Boolean was not returned.' }

    $script:currentCheck = 'Safe disconnected error'
    Assert-SafeProblem (Invoke-AiRequest -Path '/models') 409 'NotConnected' 'Connect a ChatGPT account first.'
    Set-SmokeResult 'Safe disconnected error' 'PASS' 'HTTP 409; fixed safe ProblemDetails; code=NotConnected.'

    $script:currentCheck = 'Reconnect'
    $begin = Invoke-AiRequest -Method 'POST' -Path '/connect' -Body @{}
    Assert-HttpSuccess $begin
    if ((Get-ResponseField $begin.Body 'browserOpened') -cne $true) { Stop-SmokeCheck 'Reconnect did not open the browser.' }
    Confirm-BrowserOAuth
    $status = Get-ConnectedStatus
    Assert-InferencePermission $status
    $null = Get-ModelCatalog
    Set-SmokeResult 'Reconnect' 'PASS' 'Browser confirmed; connected, permission, and live models verified.'

    $script:currentCheck = 'Local-request protection'
    Assert-SafeProblem (Invoke-AiRequest -WithoutLocalHeader) 403 'LocalRequestRequired' 'Use a local connection and the X-SocialTelemetry-Local: 1 header.'
    Set-SmokeResult 'Local-request protection' 'PASS' 'HTTP 403; fixed safe ProblemDetails; code=LocalRequestRequired.'
    $exitCode = 0
}
catch {
    if ($results[$script:currentCheck].Status -ne 'FAIL') {
        Set-SmokeResult $script:currentCheck 'FAIL' 'Check could not complete. Raw diagnostics were withheld.'
    }
    Write-Host "Stopped during: $script:currentCheck. No further provider actions will run."
}
finally {
    try {
        Stop-SmokeApp
        Set-SmokeResult 'Process cleanup' 'PASS' 'Owned API/runner stopped, or no process was started.'
    }
    catch {
        Set-SmokeResult 'Process cleanup' 'FAIL' 'Owned process cleanup could not be confirmed.'
        $exitCode = 1
    }
    if ($script:refreshCount -gt 0) {
        Set-SmokeResult 'Natural refresh' 'PASS' 'Observed the safe application credential-refresh event.'
    }
    Write-Host ''
    $results.Values | Format-Table Check, Status, Note -AutoSize -Wrap | Out-Host
    Write-Host 'Remote revocation is advisory; an unconfirmed remote revocation does not negate verified local clearing.'
    Write-Host 'Skipped metadata/inference/refresh checks do not establish complete live AI verification.'
}
exit $exitCode
