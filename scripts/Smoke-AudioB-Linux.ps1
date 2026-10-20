#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Sqlite', 'PostgreSql')][string]$Provider = 'Sqlite',
    [string]$Image = 'socialtelemetry-audio-smoke-linux:local',
    [string]$PostgresImage = 'postgres:18',
    [string]$PublishDirectory = '',
    [string]$ModelFile = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repository '.local-stt'
$runId = [Guid]::NewGuid().ToString('N')
$evidenceParent = [IO.Path]::GetFullPath((Join-Path $toolsRoot 'smoke-evidence'))
$evidence = [IO.Path]::GetFullPath((Join-Path $evidenceParent "Linux-$Provider-$runId"))
if ([IO.Path]::GetDirectoryName($evidence) -ne $evidenceParent -or (Test-Path -LiteralPath $evidence)) {
    throw 'Expected a new generated evidence root inside this workspace.'
}
$ancestor = $evidenceParent
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence ancestor is a reparse point.' }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
$publish = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $toolsRoot 'linux-publish' }
$fixtures = Join-Path $toolsRoot 'smoke-evidence\multilingual-fixtures'
$toolSettings = Get-Content -LiteralPath (Join-Path $toolsRoot 'tools.json') -Raw | ConvertFrom-Json
$model = if ($ModelFile) { [IO.Path]::GetFullPath($ModelFile) } else { $toolSettings.Model }
foreach ($path in @($publish, $fixtures, $model)) {
    if (-not (Test-Path -LiteralPath $path)) { throw 'Publish/model/fixture path is missing.' }
}
$sourceRevision = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify current source revision.' }
$binaryHashes = [ordered]@{}
foreach ($assembly in @('SocialTelemetry.Api', 'SocialTelemetry.Application', 'SocialTelemetry.Domain', 'SocialTelemetry.Infrastructure')) {
    $assemblyFile = Join-Path $publish ($assembly + '.dll')
    if (-not (Test-Path -LiteralPath $assemblyFile -PathType Leaf)) { throw 'Fresh four-layer Linux publish is required.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyFile).ProductVersion
    if ($version -notlike "*+$sourceRevision*") { throw 'Linux publish does not identify current HEAD; explicitly publish current source first.' }
    $binaryHashes[$assembly] = (Get-FileHash -LiteralPath $assemblyFile -Algorithm SHA256).Hash
}
New-Item -ItemType Directory -Path $evidence | Out-Null
$data = Join-Path $evidence 'data'
New-Item -ItemType Directory -Path $data | Out-Null
@{ sourceRevision = $sourceRevision; binaryHashes = $binaryHashes } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'build.json') -Encoding UTF8
$apiId = $null
$databaseId = $null
$networkId = $null
$originalConnection = [Environment]::GetEnvironmentVariable('ConnectionStrings__Default', 'Process')
$originalEnvironment = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Process')

function Assert-LinuxSmoke([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Remove-OwnedSmokeContainer([string]$Id) {
    if (-not $Id) { return }
    $inspection = & docker inspect $Id | ConvertFrom-Json
    if ($LASTEXITCODE -eq 0 -and $inspection[0].Config.Labels.'socialtelemetry.linux-smoke' -eq $runId) {
        & docker rm --force $Id | Out-Null
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Owned container cleanup failed.'
    } else {
        throw 'Container ownership cannot be verified for cleanup.'
    }
}

try {
    foreach ($cachedImage in @($Image, $PostgresImage)) {
        if ($cachedImage -eq $PostgresImage -and $Provider -ne 'PostgreSql') { continue }
        & docker image inspect $cachedImage *> $null
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Required smoke image is not cached; build/pull prerequisites explicitly before this run.'
    }
    $connection = ''
    if ($Provider -eq 'PostgreSql') {
        $networkId = (& docker network create --label "socialtelemetry.linux-smoke=$runId" "stt-smoke-$runId").Trim()
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Smoke network creation failed.'
        $databaseId = (& docker run --pull=never --detach --name "stt-pg-$runId" --network $networkId --network-alias database --label "socialtelemetry.linux-smoke=$runId" --publish '127.0.0.1::5432' --tmpfs /var/lib/postgresql --env POSTGRES_DB=smoke --env POSTGRES_USER=smoke --env POSTGRES_PASSWORD=disposable-smoke $PostgresImage).Trim()
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'PostgreSQL container creation failed.'
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & docker exec $databaseId pg_isready -U smoke -d smoke *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        Assert-LinuxSmoke $ready 'PostgreSQL readiness failed.'
        $port = (& docker inspect $databaseId | ConvertFrom-Json)[0].NetworkSettings.Ports.'5432/tcp'[0].HostPort
        [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', "Host=127.0.0.1;Port=$port;Database=smoke;Username=smoke;Password=disposable-smoke", 'Process')
        [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Process')
        & dotnet ef database update --project (Join-Path $repository 'SocialTelemetry.Infrastructure') --startup-project (Join-Path $repository 'SocialTelemetry.Api') --no-build -- --provider PostgreSql --ApplicationData:RootDirectory $evidence *> (Join-Path $evidence 'migrations.log')
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'PostgreSQL migration failed.'
        $connection = 'Host=database;Port=5432;Database=smoke;Username=smoke;Password=disposable-smoke'
    }
    $dockerArguments = @('run', '--pull=never', '--detach', '--init', '--name', "stt-linux-$runId",
        '--label', "socialtelemetry.linux-smoke=$runId", '--user', '1654:1654', '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges', '--read-only',
        '--mount', "type=bind,source=$data,target=/data", '--tmpfs', '/tmp:uid=1654,gid=1654,mode=0700',
        '--mount', "type=bind,source=$publish,target=/app,readonly",
        '--mount', "type=bind,source=$fixtures,target=/fixtures,readonly",
        '--mount', "type=bind,source=$model,target=/models/whisper.bin,readonly",
        '--mount', "type=bind,source=$PSScriptRoot,target=/test,readonly",
        '--mount', "type=bind,source=$evidence,target=/evidence",
        '--env', 'ASPNETCORE_URLS=http://127.0.0.1:8080', '--env', 'ASPNETCORE_ENVIRONMENT=Production',
        '--env', 'HOME=/data', '--env', 'ApplicationData__RootDirectory=/data',
        '--env', "Persistence__Provider=$Provider", '--env', 'Persistence__SqliteFile=smoke.db',
        '--env', "ConnectionStrings__Default=$connection", '--env', 'AttachmentStorage__LocalDirectory=attachments',
        '--env', 'Observability__OtlpEnabled=false', '--env', 'ChatGpt__CallbackUri=http://127.0.0.1:8080/ai-connection/chatgpt/callback',
        '--env', 'PYTHONOPTIMIZE=0',
        '--env', 'ProfileStorage__AvatarDirectory=avatars', '--env', 'ChatGpt__DataDirectory=/data/credentials',
        '--env', 'WhisperCpp__FfmpegExecutable=/usr/bin/ffmpeg', '--env', 'WhisperCpp__WhisperExecutable=/opt/whisper/whisper-cli',
        '--env', 'WhisperCpp__ModelFile=/models/whisper.bin', '--env', ('WhisperCpp__ModelId=' + $toolSettings.ModelId),
        '--env', 'WhisperCpp__TemporaryDirectory=transcription-temp')
    if ($networkId) { $dockerArguments += @('--network', $networkId) }
    $dockerArguments += $Image
    $apiId = (& docker @dockerArguments).Trim()
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Linux API container creation failed.'
    & docker image inspect --format '{{.Id}}' $Image *> (Join-Path $evidence 'image-id.txt')
    & docker exec $apiId python3 /test/LinuxAudioSmoke.py --provider $Provider --model-id $toolSettings.ModelId --phase lifecycle
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Real Linux API lifecycle failed; inspect retained evidence.'
    & docker logs $apiId *> (Join-Path $evidence 'api-configured.log')
    Remove-OwnedSmokeContainer $apiId
    $apiId = $null
    $modelArgument = [Array]::IndexOf($dockerArguments, 'WhisperCpp__ModelFile=/models/whisper.bin')
    Assert-LinuxSmoke ($modelArgument -ge 0) 'Cannot locate owned model configuration for restart.'
    $dockerArguments[$modelArgument] = 'WhisperCpp__ModelFile=/models/missing-smoke-model.bin'
    $apiId = (& docker @dockerArguments).Trim()
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Linux API restart failed.'
    & docker exec $apiId python3 /test/LinuxAudioSmoke.py --provider $Provider --model-id $toolSettings.ModelId --phase restart
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Linux persisted selector/reviewed reuse/missing-model/deletion checks failed.'
    & docker exec $apiId python3 -c 'import platform,os; print(platform.platform()); print(os.getuid())' *> (Join-Path $evidence 'host.txt')
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Linux host metadata collection failed.'
    & docker exec $apiId dpkg-query --show ffmpeg libgomp1 *> (Join-Path $evidence 'packages.txt')
    & python (Join-Path $PSScriptRoot 'Compare-AudioTranscript.py') (Join-Path $evidence 'result.json') *> (Join-Path $evidence 'comparison.log')
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Language quality comparison failed.'
} finally {
    $cleanupFailed = $false
    try { if ($apiId) { & docker logs $apiId *> (Join-Path $evidence 'api.log') } } catch { $cleanupFailed = $true }
    foreach ($ownedId in @($apiId, $databaseId)) {
        try { Remove-OwnedSmokeContainer $ownedId } catch { $cleanupFailed = $true }
    }
    try {
        if ($networkId) {
            $network = & docker network inspect $networkId | ConvertFrom-Json
            if ($LASTEXITCODE -eq 0 -and $network[0].Labels.'socialtelemetry.linux-smoke' -eq $runId) {
                & docker network rm $networkId | Out-Null
                if ($LASTEXITCODE -ne 0) { $cleanupFailed = $true }
            } else { $cleanupFailed = $true }
        }
    } catch { $cleanupFailed = $true }
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', $originalConnection, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $originalEnvironment, 'Process')
    Write-Output "Linux smoke evidence: $evidence"
    if ($cleanupFailed) { throw 'Owned smoke cleanup failed; retained evidence is not a complete PASS.' }
}
