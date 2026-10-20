#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Sqlite', 'PostgreSql')][string]$Provider = 'Sqlite',
    [string]$PostgresImage = 'postgres:18',
    [switch]$IncludeAnalysis
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$apiDirectory = Join-Path $repositoryRoot 'SocialTelemetry.Api'
$apiDll = Join-Path $apiDirectory 'bin\Debug\net10.0\SocialTelemetry.Api.dll'
$runId = [Guid]::NewGuid().ToString('N')
$smokeParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.local-stt\smoke'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $smokeParent "Ai71-$Provider-$runId"))
$script:binding = $null
$containerId = $null
$savedEnvironment = @{}
$binaryHashes = [ordered]@{}
$sourceRevision = $null
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
    'Startup', 'Profile binding', 'Owned connection selection', 'Profile AI isolation', 'Begin connection', 'Browser OAuth/callback', 'Connected status',
    'Inference permission', 'Model discovery', 'Model selection',
    'Neutral selected-model metadata', 'Tiny inference', 'Restart/session persistence',
    'Natural refresh', 'Local disconnect', 'Remote revocation confirmed', 'Reconnect',
    'Safe disconnected error', 'Local-request protection', 'Process cleanup'
)) {
    $results[$name] = [pscustomobject]@{ Check = $name; Status = 'NOT EXERCISED'; Note = 'Not reached.' }
}
$results['Neutral selected-model metadata'].Note = 'No HTTP endpoint for IAiClient.GetSelectedModelAsync().'
$results['Tiny inference'].Note = 'Optional -IncludeAnalysis uses the real AnalyzeInteraction route after explicit manual consent; not yet exercised.'
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
        [switch]$ProductRoute,
        [int]$TimeoutMilliseconds = 90000
    )
    # Only fixed local routes are used. Redirects, proxies, and raw response logging are disabled.
    $target = if ($ProductRoute) { 'http://127.0.0.1:5059' + $Path } else { $baseUri + $Path }
    $request = [System.Net.HttpWebRequest]::Create($target)
    $request.Method = $Method
    $request.Accept = 'application/json'
    $request.AllowAutoRedirect = $false
    $request.Proxy = $null
    $request.Timeout = $TimeoutMilliseconds
    $request.ReadWriteTimeout = $TimeoutMilliseconds
    if (-not $WithoutLocalHeader) { $request.Headers['X-SocialTelemetry-Local'] = '1' }
    if ($script:binding -and $Path -notlike '/local-profiles*') {
        $request.Headers['X-SocialTelemetry-ProfileId'] = [string]$script:binding.userProfileId
        $request.Headers['X-SocialTelemetry-ProfileVersion'] = [string]$script:binding.version
    }
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
        try { $parsed = if ([string]::IsNullOrWhiteSpace($content)) { $null } else { ConvertFrom-Json -InputObject $content } }
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
    param($Response, [int]$ExpectedStatus = 0)
    if ($ExpectedStatus -ne 0 -and $Response.StatusCode -ge 200 -and $Response.StatusCode -lt 300 -and $Response.StatusCode -ne $ExpectedStatus) {
        Stop-SmokeCheck "Expected HTTP $ExpectedStatus, received HTTP $($Response.StatusCode)."
    }
    if ($Response.StatusCode -lt 200 -or $Response.StatusCode -ge 300) {
        # Never display arbitrary provider text or response fields, including ProblemDetails.detail.
        $knownCodes = @(
            'NotConnected', 'AuthorizationDenied', 'InvalidCallback', 'InvalidIdentity',
            'InferencePermissionMissing', 'ReconnectRequired', 'RefreshFailed', 'InvalidClient',
            'UsageLimitReached', 'ModelNotSelected', 'ModelUnavailable', 'ProviderUnavailable',
            'ProviderRejected', 'MalformedResponse', 'IncompleteResponse', 'StorageUnavailable',
            'ConnectionBusy', 'BrowserUnavailable', 'LocalRequestRequired',
            'UnsupportedEvidence', 'EvidenceNotReady', 'TranscriptNotReady', 'StaleTranscript',
            'EvidenceLimitExceeded', 'InvalidEvidenceSelection', 'CapabilityMissing',
            'InvalidStructuredResponse', 'StaleContext', 'ExecutionInvalidated'
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
    $startInfo.Arguments = '"' + $apiDll + '" --urls http://127.0.0.1:5059'
    $startInfo.WorkingDirectory = $apiDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $script:appProcess = New-Object System.Diagnostics.Process
    $script:appProcess.StartInfo = $startInfo
    $script:outputObserver = New-Object SocialTelemetry.Ai71OutputObserver($script:appProcess)
    if (-not $script:appProcess.Start()) { Stop-SmokeCheck 'The API process could not start.' }
    $null = $script:appProcess.Handle
    $script:appStarted = $true
    $script:appProcess.BeginOutputReadLine()
    $script:appProcess.BeginErrorReadLine()

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($script:appProcess.HasExited) {
            Stop-SmokeCheck 'The API exited during startup. Inspect only the isolated run migration/build evidence.'
        }
        $ready = $null
        try { $ready = Invoke-AiRequest -ProductRoute -Path '/local-profiles' -TimeoutMilliseconds 1000 }
        catch { }
        if ($null -ne $ready -and $ready.StatusCode -eq 200 -and
            (Get-ResponseField $ready.Body 'version')) {
            Find-OwnedApiProcess
            if ($null -eq $script:apiProcess -or $script:apiProcess.HasExited) {
                Stop-SmokeCheck 'The listening API process could not be tracked safely.'
            }
            return
        }
        Start-Sleep -Milliseconds 300
    }
    Stop-SmokeCheck 'The API did not become ready within 60 seconds. Inspect the isolated run migration/build evidence.'
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
        # Stop only retained owned process handles, never descendants such as the system browser.
        try {
            if ($null -ne $script:apiProcess -and -not $script:apiProcess.HasExited) {
                $script:apiProcess.Kill()
                if (-not $script:apiProcess.WaitForExit(10000)) { throw 'Owned API process did not exit.' }
            }
        }
        finally {
            if (-not $script:appProcess.HasExited) {
                $script:appProcess.Kill()
                if (-not $script:appProcess.WaitForExit(10000)) { throw 'Owned host process did not exit.' }
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

function Set-IsolatedEnvironment([string]$Name, [string]$Value) {
    if (-not $savedEnvironment.ContainsKey($Name)) { $savedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process') }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Initialize-IsolatedStore {
    for ($ancestor=[IO.DirectoryInfo]::new($smokeParent); $ancestor; $ancestor=$ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Run root cannot traverse a linked directory.' }
    }
    if ([IO.Path]::GetDirectoryName($runRoot) -ne $smokeParent -or (Test-Path -LiteralPath $runRoot)) { throw 'Unexpected/reused run root.' }
    $script:sourceRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish source revision.' }
    foreach ($assembly in @('Api','Application','Domain','Infrastructure')) {
        $file = Join-Path (Split-Path -Parent $apiDll) "SocialTelemetry.$assembly.dll"
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
            [Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion -notlike "*$sourceRevision*") { throw 'Fresh current-revision binaries are required.' }
        $binaryHashes[$assembly] = (Get-FileHash -LiteralPath $file).Hash
    }
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    foreach ($setting in @{
        ASPNETCORE_ENVIRONMENT='Production'; Persistence__Provider=$Provider; Persistence__SqliteFile='smoke.db'
        ApplicationData__RootDirectory=$runRoot; ConnectionStrings__Default=''
        AttachmentStorage__LocalDirectory='attachments'; ProfileStorage__AvatarDirectory='avatars'
        ChatGpt__DataDirectory=(Join-Path $runRoot 'credentials'); ChatGpt__CallbackUri='http://127.0.0.1:5059/ai-connection/chatgpt/callback'
        Observability__OtlpEnabled='false'; ASPNETCORE_HTTPS_PORT=''; ASPNETCORE_HTTPS_PORTS=''; HTTPS_PORT=''
    }.GetEnumerator()) { Set-IsolatedEnvironment $setting.Key $setting.Value }
    if ($Provider -eq 'PostgreSql') {
        & docker image inspect $PostgresImage *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Required PostgreSQL image is not cached; no pull attempted.' }
        $script:containerId = (& docker run --pull=never --detach --name "st-ai71-$runId" --label "socialtelemetry.ai71-smoke=$runId" --publish '127.0.0.1::5432' --tmpfs /var/lib/postgresql --env POSTGRES_DB=smoke --env POSTGRES_USER=smoke --env POSTGRES_PASSWORD=disposable-smoke $PostgresImage).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Owned PostgreSQL creation failed.' }
        $port = (& docker inspect $containerId | ConvertFrom-Json)[0].NetworkSettings.Ports.'5432/tcp'[0].HostPort
        Set-IsolatedEnvironment 'ConnectionStrings__Default' "Host=127.0.0.1;Port=$port;Database=smoke;Username=smoke;Password=disposable-smoke"
        $ready=$false
        for ($attempt=0; $attempt -lt 60; $attempt++) {
            & docker exec $containerId pg_isready -U smoke -d smoke *> $null
            if ($LASTEXITCODE -eq 0) { $ready=$true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'Owned PostgreSQL readiness failed.' }
    }
    & dotnet ef database update --project (Join-Path $repositoryRoot 'SocialTelemetry.Infrastructure') --startup-project $apiDirectory --no-build -- --provider $Provider *> (Join-Path $runRoot 'migrations.log')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated provider migrations failed.' }
}

function Initialize-SmokeProfile {
    $profiles = Invoke-AiRequest -ProductRoute -Path '/local-profiles'
    Assert-HttpSuccess $profiles
    if (@($profiles.Body.profiles).Count -ne 0 -or $profiles.Body.userProfileId) { throw 'Expected an empty isolated store; no existing account may be used.' }
    $created = Invoke-AiRequest -ProductRoute -Method 'POST' -Path '/local-profiles' -Body @{displayName='Synthetic AI smoke'; expectedVersion=$profiles.Body.version}
    Assert-HttpSuccess $created 201
    $profiles = Invoke-AiRequest -ProductRoute -Path '/local-profiles'
    Assert-HttpSuccess $profiles
    $selected = Invoke-AiRequest -ProductRoute -Method 'PUT' -Path '/local-profiles/current' -Body @{userProfileId=$created.Body.id; expectedVersion=$profiles.Body.version}
    Assert-HttpSuccess $selected 200
    if ($selected.Body.userProfileId -ne $created.Body.id) { throw 'Profile selection mismatch.' }
    $script:binding = $selected.Body
    Set-SmokeResult 'Profile binding' 'PASS' 'New synthetic profile selected via CAS; explicit immutable owner/version headers.'
}

function Invoke-SyntheticAnalysis {
    if (-not $IncludeAnalysis) { return }
    Write-Host 'One small structured analysis will send only newly created synthetic text to the selected provider.'
    if ((Read-Host 'Type ANALYZE to allow this one inference, or anything else to stop') -cne 'ANALYZE') { Stop-SmokeCheck 'Synthetic inference was not confirmed.' }
    $person = Invoke-AiRequest -ProductRoute -Method 'POST' -Path '/people' -Body @{userProfileId=$binding.userProfileId; displayName='Synthetic Alex'; relationshipContext=0; notes='Synthetic original notes'}
    Assert-HttpSuccess $person 201
    $interaction = Invoke-AiRequest -ProductRoute -Method 'POST' -Path '/interactions' -Body @{userProfileId=$binding.userProfileId; title='Synthetic AI smoke'; description='Alex and I agreed to meet next week after watching a film.'; occurredAt=[DateTimeOffset]::UtcNow.ToString('o'); participantIds=@($person.Body.id)}
    Assert-HttpSuccess $interaction 201
    $completed = Invoke-AiRequest -ProductRoute -Method 'POST' -Path ("/interactions/"+$interaction.Body.id+'/analyze') -Body @{attachmentIds=@(); userQuestion='Identify uncertainty and a practical follow-up; do not invent facts.'} -TimeoutMilliseconds 180000
    Assert-HttpSuccess $completed
    if ($completed.StatusCode -ne 201 -or $completed.Body.interactionId -ne $interaction.Body.id -or
        $completed.Body.schemaVersion -cne 'interaction-analysis-v2' -or $completed.Body.promptVersion -cne 'analyze-interaction-v2' -or
        [string]::IsNullOrWhiteSpace($completed.Body.provider) -or [string]::IsNullOrWhiteSpace($completed.Body.model) -or
        $completed.Body.requestedModel -cne $script:selectedModel -or [string]::IsNullOrWhiteSpace($completed.Body.result.summary)) { Stop-SmokeCheck 'Structured analysis/provenance contract mismatch.' }
    $facts = Invoke-AiRequest -ProductRoute -Path ("/people/"+$person.Body.id+'/facts')
    Assert-HttpSuccess $facts
    if (@($facts.Body.facts).Count -ne 0) { Stop-SmokeCheck 'Analysis unexpectedly created confirmed facts.' }
    Set-SmokeResult 'Tiny inference' 'PASS' 'Real synthetic AnalyzeInteraction HTTP201 + structured v2/provenance; no confirmed facts. Pending database atomicity remains automated-regression evidence, not a new retrieval endpoint.'
    Assert-HttpSuccess (Invoke-AiRequest -ProductRoute -Method 'DELETE' -Path ("/interactions/"+$interaction.Body.id))
    Assert-HttpSuccess (Invoke-AiRequest -ProductRoute -Method 'DELETE' -Path ("/people/"+$person.Body.id))
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
    if (-not (Test-Path -LiteralPath $apiDll) -or
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

    Write-Host 'AI live smoke: isolated synthetic store/credentials, current build, manual browser OAuth twice.'
    Write-Host 'OAuth is manual twice. Technical results stay in the isolated run; raw output is discarded and provider credentials remain protected.'
    Initialize-IsolatedStore
    Start-SmokeApp
    Initialize-SmokeProfile
    Set-SmokeResult 'Startup' 'PASS' 'Owned API process is reachable on 127.0.0.1:5059.'

    $script:currentCheck = 'Begin connection'
    $begin = Invoke-AiRequest -Method 'POST' -Path '/connect' -Body @{newAccount=$true}
    Assert-HttpSuccess $begin 200
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

    $script:currentCheck = 'Owned connection selection'
    Assert-HttpSuccess (Invoke-AiRequest -Method 'PUT' -Path '/connection' -Body @{connectionId=$connectionId; adoptUnassigned=$false}) 204
    Set-SmokeResult 'Owned connection selection' 'PASS' 'Only the OAuth-created connection owned by this synthetic profile was selected.'

    $script:currentCheck = 'Model discovery'
    $catalog = Get-ModelCatalog
    Set-SmokeResult 'Model discovery' 'PASS' 'Live nonempty catalog returned.'
    $models = @(Get-ResponseField $catalog 'models')
    for ($index = 0; $index -lt $models.Count; $index++) {
        Write-Host ('{0}. {1} - {2}' -f ($index + 1), (Get-ResponseField $models[$index] 'id'), (Get-ResponseField $models[$index] 'displayName'))
    }
    $script:currentCheck = 'Model selection'
    Write-Host 'Catalog availability does not establish StructuredOutput support. Analysis fails closed for unsupported capabilities.'
    Write-Host 'Current published adapter confirms StructuredOutput only for exact gpt-5.6-sol or gpt-5.6 IDs; this does not change capability policy or select a model for you.'
    $choice = 0
    $answer = Read-Host 'Select a model by its displayed number'
    if (-not [int]::TryParse($answer, [ref]$choice) -or $choice -lt 1 -or $choice -gt $models.Count) {
        Stop-SmokeCheck 'Invalid model selection; no selection request was sent.'
    }
    $modelId = Get-ResponseField $models[$choice - 1] 'id'
    $script:selectedModel = $modelId
    Assert-HttpSuccess (Invoke-AiRequest -Method 'PUT' -Path '/model' -Body @{ modelId = $modelId }) 200
    $catalog = Get-ModelCatalog
    if ((Get-ResponseField $catalog 'selectedModel') -cne $modelId -or
        (Get-ResponseField $catalog 'selectedModelAvailable') -cne $true) { Stop-SmokeCheck 'The chosen model was not saved as available.' }
    Set-SmokeResult 'Model selection' 'PASS' 'Exact catalog ID saved and rechecked as available.'

    $script:currentCheck = 'Tiny inference'
    Invoke-SyntheticAnalysis

    $script:currentCheck = 'Restart/session persistence'
    Stop-SmokeApp
    Start-SmokeApp
    $restarted = Invoke-AiRequest -ProductRoute -Path '/local-profiles'
    Assert-HttpSuccess $restarted
    if ($restarted.Body.userProfileId -ne $binding.userProfileId -or $restarted.Body.version -ne $binding.version) { Stop-SmokeCheck 'Restart changed the original profile selection.' }
    $status = Get-ConnectedStatus
    Assert-InferencePermission $status
    $catalog = Get-ModelCatalog
    if ((Get-ResponseField $status 'connectionId') -cne $connectionId -or
        (Get-ResponseField $status 'selectedModel') -cne $modelId -or
        (Get-ResponseField $catalog 'selectedModel') -cne $modelId -or
        (Get-ResponseField $catalog 'selectedModelAvailable') -cne $true) { Stop-SmokeCheck 'Connection or selected model did not survive restart.' }
    Set-SmokeResult 'Restart/session persistence' 'PASS' 'Same connection and model; permission and live catalog verified.'

    $script:currentCheck = 'Profile AI isolation'
    $ownerBinding = $script:binding
    $profiles = Invoke-AiRequest -ProductRoute -Path '/local-profiles'
    Assert-HttpSuccess $profiles
    $other = Invoke-AiRequest -ProductRoute -Method 'POST' -Path '/local-profiles' -Body @{displayName='Synthetic disconnected B'; expectedVersion=$profiles.Body.version}
    Assert-HttpSuccess $other
    $switched = Invoke-AiRequest -ProductRoute -Method 'PUT' -Path '/local-profiles/current' -Body @{userProfileId=$other.Body.id; expectedVersion=$profiles.Body.version}
    Assert-HttpSuccess $switched
    $script:binding = $switched.Body
    $otherStatus = Invoke-AiRequest
    Assert-HttpSuccess $otherStatus
    if ($otherStatus.Body.connected -or $otherStatus.Body.connectionId -or @($otherStatus.Body.accounts).Count -ne 0) { Stop-SmokeCheck 'Connection identity leaked across profiles.' }
    $foreignClaim = Invoke-AiRequest -Method 'PUT' -Path '/connection' -Body @{connectionId=$connectionId; adoptUnassigned=$true}
    Assert-SafeProblem $foreignClaim 409 'NotConnected' 'Connect an AI provider first.'
    $back = Invoke-AiRequest -ProductRoute -Method 'PUT' -Path '/local-profiles/current' -Body @{userProfileId=$ownerBinding.userProfileId; expectedVersion=$binding.version}
    Assert-HttpSuccess $back
    $script:binding = $back.Body
    $status = Get-ConnectedStatus
    if ($status.connectionId -ne $connectionId -or $status.selectedModel -cne $modelId) { Stop-SmokeCheck 'Returning to A lost its owned AI selection.' }
    Set-SmokeResult 'Profile AI isolation' 'PASS' 'B remained disconnected/cannot adopt A; A connection/model preserved after explicit return.'

    $script:currentCheck = 'Local disconnect'
    $disconnect = Invoke-AiRequest -Method 'DELETE' -Path ''
    Assert-HttpSuccess $disconnect 200
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
    Assert-SafeProblem (Invoke-AiRequest -Path '/models') 409 'NotConnected' 'Connect an AI provider first.'
    Set-SmokeResult 'Safe disconnected error' 'PASS' 'HTTP 409; fixed safe ProblemDetails; code=NotConnected.'

    $script:currentCheck = 'Reconnect'
    $begin = Invoke-AiRequest -Method 'POST' -Path '/connect' -Body @{connectionId=$connectionId}
    Assert-HttpSuccess $begin 200
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
        Set-SmokeResult 'Process cleanup' 'PASS' 'Retained owned API handle stopped, or no process was started.'
    }
    catch {
        Set-SmokeResult 'Process cleanup' 'FAIL' 'Owned process cleanup could not be confirmed.'
        $exitCode = 1
    }
    if ($script:refreshCount -gt 0) {
        Set-SmokeResult 'Natural refresh' 'PASS' 'Observed the safe application credential-refresh event.'
    }
    if ($containerId) {
        try {
            $owned = & docker inspect $containerId | ConvertFrom-Json
            if ($LASTEXITCODE -eq 0 -and $owned[0].Config.Labels.'socialtelemetry.ai71-smoke' -eq $runId) {
                & docker rm --force $containerId | Out-Null
                if ($LASTEXITCODE -ne 0) { $exitCode=1 }
            }
            else { $exitCode=1 }
        } catch { $exitCode=1 }
    }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if (Test-Path -LiteralPath $runRoot) {
        @{backendOS='Windows'; provider=$Provider; sourceRevision=$sourceRevision; binaries=$binaryHashes; checks=@($results.Values); limits='Manual OAuth/live provider only when invoked; no password/token/prompt/result envelopes recorded. Retained credentials stay protected in this isolated run root, never copied to evidence/export. Linux connected OAuth/analysis is NOT EXERCISED: it needs an interactive system browser in the Linux backend loopback namespace. The verification image has no browser; forwarded Docker requests are not a permitted workaround.'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding UTF8
        Write-Host ('Isolated run retained: '+$runRoot)
    }
    Write-Host ''
    $results.Values | Format-Table Check, Status, Note -AutoSize -Wrap | Out-Host
    Write-Host 'Remote revocation is advisory; an unconfirmed remote revocation does not negate verified local clearing.'
    Write-Host 'Skipped metadata/inference/refresh checks do not establish complete live AI verification.'
}
exit $exitCode
