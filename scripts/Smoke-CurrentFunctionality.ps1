#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Linux')][string]$BackendOS = 'Windows',
    [ValidateSet('Sqlite', 'PostgreSql')][string]$Provider = 'Sqlite',
    [string]$PostgresImage = 'postgres:18',
    [string]$LinuxImage = 'socialtelemetry-audio-smoke-linux:local',
    [string]$PublishDirectory = '',
    [switch]$KeepFixtures
)

# One shared HTTP checkset, executed in the real backend's loopback namespace.
# Build/publish/image construction are explicit prerequisites, never implicit downloads.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
if ($env:OS -ne 'Windows_NT') { throw 'Run orchestration from Windows; Linux checks execute inside its owned container.' }
$runId = [Guid]::NewGuid().ToString('N')
$parent = [IO.Path]::GetFullPath((Join-Path $repository '.local-stt\smoke'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $parent "Functionality-$BackendOS-$Provider-$runId"))
for ($ancestor=[IO.DirectoryInfo]::new($parent); $ancestor; $ancestor=$ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Run root cannot traverse a linked directory.' }
}
if ([IO.Path]::GetDirectoryName($runRoot) -ne $parent -or (Test-Path -LiteralPath $runRoot)) { throw 'Unexpected or reused smoke root.' }
$apiDirectory = Join-Path $repository 'SocialTelemetry.Api'
$binaryDirectory = Join-Path $apiDirectory 'bin\Debug\net10.0'
if ($BackendOS -eq 'Linux') {
    $binaryDirectory = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $repository '.local-stt\linux-publish' }
}
$sourceRevision = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish source revision.' }
$binaryHashes = [ordered]@{}
foreach ($assembly in @('Api','Application','Domain','Infrastructure')) {
    $file = Join-Path $binaryDirectory "SocialTelemetry.$assembly.dll"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion -notlike "*$sourceRevision*") { throw 'Fresh current-revision build/publish required; old binaries are not smoke evidence.' }
    $binaryHashes[$assembly] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
$apiDll = Join-Path $binaryDirectory 'SocialTelemetry.Api.dll'
New-Item -ItemType Directory -Path $runRoot | Out-Null
$savedEnvironment = @{}
$appProcess = $null
$apiId = $null
$databaseId = $null
$networkId = $null
$completed = $false

function Assert-Current([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Set-CurrentEnvironment([string]$Name, [string]$Value) {
    if (-not $savedEnvironment.ContainsKey($Name)) { $savedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process') }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}
function Remove-CurrentContainer([string]$Id) {
    if (-not $Id) { return }
    $owned = & docker inspect $Id | ConvertFrom-Json
    Assert-Current ($LASTEXITCODE -eq 0 -and $owned[0].Config.Labels.'socialtelemetry.current-smoke' -eq $runId) 'Container ownership cannot be confirmed; unrelated resources will not be stopped.'
    & docker rm --force $Id | Out-Null
    Assert-Current ($LASTEXITCODE -eq 0) 'Owned container cleanup failed.'
}

try {
    foreach ($setting in @{
        ASPNETCORE_ENVIRONMENT='Production'; ApplicationData__RootDirectory=$runRoot
        Persistence__Provider=$Provider; Persistence__SqliteFile='smoke.db'; ConnectionStrings__Default=''
        AttachmentStorage__LocalDirectory='attachments'; ProfileStorage__AvatarDirectory='avatars'
        ChatGpt__DataDirectory=(Join-Path $runRoot 'credentials'); Observability__OtlpEnabled='false'
        WhisperCpp__FfmpegExecutable=(Join-Path $runRoot 'not-configured-ffmpeg')
        WhisperCpp__WhisperExecutable=(Join-Path $runRoot 'not-configured-whisper')
        WhisperCpp__ModelFile=(Join-Path $runRoot 'not-configured-model'); WhisperCpp__TemporaryDirectory='transcription-temp'
        ASPNETCORE_HTTPS_PORT=''; ASPNETCORE_HTTPS_PORTS=''; HTTPS_PORT=''
    }.GetEnumerator()) { Set-CurrentEnvironment $setting.Key $setting.Value }
    if ($BackendOS -eq 'Linux') {
        & docker image inspect $LinuxImage *> $null
        Assert-Current ($LASTEXITCODE -eq 0) 'Verification image missing; build the existing Dockerfile explicitly first.'
        & docker image inspect --format '{{.Id}}' $LinuxImage *> (Join-Path $runRoot 'image-id.txt')
    }
    $apiConnection = ''
    if ($Provider -eq 'PostgreSql') {
        & docker image inspect $PostgresImage *> $null
        Assert-Current ($LASTEXITCODE -eq 0) 'Required PostgreSQL image not cached; no pull attempted.'
        $databaseArguments = @('run','--pull=never','--detach','--name',"st-current-pg-$runId",'--label',"socialtelemetry.current-smoke=$runId",'--publish','127.0.0.1::5432','--tmpfs','/var/lib/postgresql','--env','POSTGRES_DB=smoke','--env','POSTGRES_USER=smoke','--env','POSTGRES_PASSWORD=disposable-smoke')
        if ($BackendOS -eq 'Linux') {
            $networkId = (& docker network create --label "socialtelemetry.current-smoke=$runId" "st-current-$runId").Trim()
            Assert-Current ($LASTEXITCODE -eq 0) 'Owned network creation failed.'
            $databaseArguments += @('--network',$networkId,'--network-alias','database')
        }
        $databaseArguments += $PostgresImage
        $databaseId = (& docker @databaseArguments).Trim()
        Assert-Current ($LASTEXITCODE -eq 0) 'Owned PostgreSQL creation failed.'
        $port = (& docker inspect $databaseId | ConvertFrom-Json)[0].NetworkSettings.Ports.'5432/tcp'[0].HostPort
        Set-CurrentEnvironment 'ConnectionStrings__Default' "Host=127.0.0.1;Port=$port;Database=smoke;Username=smoke;Password=disposable-smoke"
        $apiConnection = 'Host=database;Port=5432;Database=smoke;Username=smoke;Password=disposable-smoke'
        $ready=$false
        for ($attempt=0; $attempt -lt 60; $attempt++) {
            & docker exec $databaseId pg_isready -U smoke -d smoke *> $null
            if ($LASTEXITCODE -eq 0) { $ready=$true; break }
            Start-Sleep -Milliseconds 500
        }
        Assert-Current $ready 'Owned PostgreSQL readiness failed.'
        & dotnet ef database update --project (Join-Path $repository 'SocialTelemetry.Infrastructure') --startup-project $apiDirectory --no-build -- --provider PostgreSql *> (Join-Path $runRoot 'migrations.log')
        Assert-Current ($LASTEXITCODE -eq 0) 'Disposable PostgreSQL migrations failed.'
    }
    @{sourceRevision=$sourceRevision; binaries=$binaryHashes; backendOS=$BackendOS; provider=$Provider} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'build.json') -Encoding UTF8
    if ($BackendOS -eq 'Windows') {
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        $socket.Start(); $httpPort=$socket.LocalEndpoint.Port; $socket.Stop()
        $baseUri = "http://127.0.0.1:$httpPort"
        Set-CurrentEnvironment 'ChatGpt__CallbackUri' ($baseUri+'/ai-connection/chatgpt/callback')
        $appProcess = Start-Process -FilePath 'dotnet.exe' -ArgumentList @(('"'+$apiDll+'"'),'--urls',$baseUri) -WorkingDirectory $apiDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'api.stdout.log') -RedirectStandardError (Join-Path $runRoot 'api.stderr.log')
        $null = $appProcess.Handle
        $ready=$false
        for ($attempt=0; $attempt -lt 100; $attempt++) {
            Assert-Current (-not $appProcess.HasExited) 'Owned API exited during startup.'
            $listeners = @(Get-NetTCPConnection -LocalPort $httpPort -State Listen -ErrorAction SilentlyContinue)
            if ($listeners.Count -gt 0) {
                Assert-Current (@($listeners | Where-Object { $_.OwningProcess -ne $appProcess.Id }).Count -eq 0) 'Port race: listener is not the retained owned host.'
                $ready=$true; break
            }
            Start-Sleep -Milliseconds 300
        }
        Assert-Current $ready 'Owned API did not listen.'
        & python (Join-Path $PSScriptRoot 'CurrentFunctionalitySmoke.py') --base-url $baseUri --provider $Provider --host Windows --output (Join-Path $runRoot 'result.json')
    } else {
        # No published API port: requests originate INSIDE the real backend loopback namespace.
        $apiArguments = @('run','--pull=never','--detach','--init','--name',"st-current-api-$runId",'--label',"socialtelemetry.current-smoke=$runId",'--user','1654:1654','--cap-drop','ALL','--security-opt','no-new-privileges','--read-only',
            '--tmpfs','/data:uid=1654,gid=1654,mode=0700','--tmpfs','/tmp:uid=1654,gid=1654,mode=0700',
            '--mount',"type=bind,source=$binaryDirectory,target=/app,readonly",'--mount',"type=bind,source=$PSScriptRoot,target=/test,readonly",'--mount',"type=bind,source=$runRoot,target=/evidence",
            '--env','ASPNETCORE_URLS=http://127.0.0.1:8080','--env','ASPNETCORE_ENVIRONMENT=Production','--env','HOME=/data',
            '--env','ApplicationData__RootDirectory=/data','--env',"Persistence__Provider=$Provider",'--env','Persistence__SqliteFile=smoke.db','--env',"ConnectionStrings__Default=$apiConnection",
            '--env','AttachmentStorage__LocalDirectory=attachments','--env','ProfileStorage__AvatarDirectory=avatars','--env','ChatGpt__DataDirectory=/data/credentials',
            '--env','ChatGpt__CallbackUri=http://127.0.0.1:8080/ai-connection/chatgpt/callback','--env','Observability__OtlpEnabled=false',
            '--env','WhisperCpp__FfmpegExecutable=/data/not-configured-ffmpeg','--env','WhisperCpp__WhisperExecutable=/data/not-configured-whisper','--env','WhisperCpp__ModelFile=/data/not-configured-model','--env','WhisperCpp__TemporaryDirectory=transcription-temp')
        if ($networkId) { $apiArguments += @('--network',$networkId) }
        $apiArguments += $LinuxImage
        $apiId = (& docker @apiArguments).Trim()
        Assert-Current ($LASTEXITCODE -eq 0) 'Owned Linux API creation failed.'
        & docker exec $apiId python3 /test/CurrentFunctionalitySmoke.py --base-url http://127.0.0.1:8080 --provider $Provider --host Linux --output /evidence/result.json
    }
    Assert-Current ($LASTEXITCODE -eq 0) 'Shared HTTP checkset failed; inspect only the generated evidence.'
    $completed=$true
} catch {
    Write-Output 'Current smoke could not complete; raw response/exception withheld. Retain this isolated run for diagnosis.'
} finally {
    try {
        if ($appProcess) {
            if (-not $appProcess.HasExited) { $appProcess.Kill(); Assert-Current ($appProcess.WaitForExit(10000)) 'Owned API did not exit.' }
            $appProcess.Dispose()
        }
    } catch { $completed=$false }
    try {
        if ($apiId) { & docker logs $apiId *> (Join-Path $runRoot 'api.log') }
        Remove-CurrentContainer $apiId
    } catch { $completed=$false }
    try { Remove-CurrentContainer $databaseId } catch { $completed=$false }
    try {
        if ($networkId) {
            $network = & docker network inspect $networkId | ConvertFrom-Json
            Assert-Current ($LASTEXITCODE -eq 0 -and $network[0].Labels.'socialtelemetry.current-smoke' -eq $runId) 'Network ownership changed.'
            & docker network rm $networkId | Out-Null
            Assert-Current ($LASTEXITCODE -eq 0) 'Owned network cleanup failed.'
        }
    } catch { $completed=$false }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    @{orchestrationCompleted=$completed; limits='HTTP result.json is separate; cleanup failure fails orchestration even if HTTP checks passed.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'cleanup.json') -Encoding UTF8
    if ($completed -and -not $KeepFixtures) {
        Assert-Current ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($runRoot)) -eq $parent) 'Cleanup root changed.'
        for ($ancestor=[IO.DirectoryInfo]::new($runRoot); $ancestor; $ancestor=$ancestor.Parent) { Assert-Current (-not ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Linked cleanup ancestor.' }
        Assert-Current (@(Get-ChildItem -LiteralPath $runRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -eq 0) 'Linked cleanup child.'
        $evidence = Join-Path $repository ".local-stt\smoke-evidence\Functionality-$BackendOS-$Provider-$runId"
        for ($ancestor=[IO.DirectoryInfo]::new($evidence); $ancestor; $ancestor=$ancestor.Parent) { Assert-Current (-not ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint))) 'Linked evidence destination.' }
        New-Item -ItemType Directory -Path $evidence | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $runRoot -File) {
            $destination=Join-Path $evidence $file.Name
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            Assert-Current ((Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) 'Evidence copy verification failed.'
        }
        [IO.Directory]::Delete($runRoot,$true)
        Write-Output ("Retained technical evidence: $evidence")
    } else { Write-Output ("Retained isolated run: $runRoot") }
}
if (-not $completed) { exit 1 }
