#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Sqlite', 'PostgreSql')][string]$Provider = 'Sqlite',
    [string]$Image = 'socialtelemetry-audio-smoke-linux:local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repository '.local-stt'
$runId = [Guid]::NewGuid().ToString('N')
$profileId = [Guid]::NewGuid().ToString()
$evidence = Join-Path $toolsRoot "smoke-evidence\Linux-$Provider-$runId"
New-Item -ItemType Directory -Path $evidence | Out-Null
$publish = Join-Path $toolsRoot 'linux-publish'
$fixtures = Join-Path $toolsRoot 'smoke-evidence\multilingual-fixtures'
$model = Join-Path $toolsRoot 'ggml-large-v3-turbo-q5_0.bin'
foreach ($path in @($publish, $fixtures, $model)) {
    if (-not (Test-Path -LiteralPath $path)) { throw 'Publish/model/fixture path is missing.' }
}
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
    }
}

try {
    $connection = ''
    if ($Provider -eq 'PostgreSql') {
        $networkId = (& docker network create --label "socialtelemetry.linux-smoke=$runId" "stt-smoke-$runId").Trim()
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Smoke network creation failed.'
        $databaseId = (& docker run --detach --name "stt-pg-$runId" --network $networkId --network-alias database --label "socialtelemetry.linux-smoke=$runId" --publish '127.0.0.1::5432' --tmpfs /var/lib/postgresql --env POSTGRES_DB=smoke --env POSTGRES_USER=smoke --env POSTGRES_PASSWORD=disposable-smoke postgres:18).Trim()
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
        & dotnet ef database update --project (Join-Path $repository 'SocialTelemetry.Api') --no-build -- --provider PostgreSql *> (Join-Path $evidence 'migrations.log')
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'PostgreSQL migration failed.'
        "INSERT INTO `"UserProfiles`" (`"Id`", `"DisplayName`") VALUES ('$profileId', 'Synthetic Linux smoke');" | & docker exec -i $databaseId psql -U smoke -d smoke -v ON_ERROR_STOP=1 *> (Join-Path $evidence 'seed.log')
        Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'PostgreSQL fixture seed failed.'
        $connection = 'Host=database;Port=5432;Database=smoke;Username=smoke;Password=disposable-smoke'
    }
    $dockerArguments = @('run', '--detach', '--init', '--name', "stt-linux-$runId",
        '--label', "socialtelemetry.linux-smoke=$runId", '--user', '1654:1654', '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges', '--read-only',
        '--tmpfs', '/data:uid=1654,gid=1654,mode=0700', '--tmpfs', '/tmp:uid=1654,gid=1654,mode=0700',
        '--mount', "type=bind,source=$publish,target=/app,readonly",
        '--mount', "type=bind,source=$fixtures,target=/fixtures,readonly",
        '--mount', "type=bind,source=$model,target=/models/whisper.bin,readonly",
        '--mount', "type=bind,source=$PSScriptRoot,target=/test,readonly",
        '--mount', "type=bind,source=$evidence,target=/evidence",
        '--env', 'ASPNETCORE_URLS=http://127.0.0.1:8080', '--env', 'ASPNETCORE_ENVIRONMENT=Production',
        '--env', 'HOME=/data', '--env', 'ApplicationData__RootDirectory=/data',
        '--env', "Persistence__Provider=$Provider", '--env', 'Persistence__SqliteFile=smoke.db',
        '--env', "ConnectionStrings__Default=$connection", '--env', 'AttachmentStorage__LocalDirectory=attachments',
        '--env', 'ProfileStorage__AvatarDirectory=avatars', '--env', 'ChatGpt__DataDirectory=/data/credentials',
        '--env', 'WhisperCpp__FfmpegExecutable=/usr/bin/ffmpeg', '--env', 'WhisperCpp__WhisperExecutable=/opt/whisper/whisper-cli',
        '--env', 'WhisperCpp__ModelFile=/models/whisper.bin', '--env', 'WhisperCpp__ModelId=large-v3-turbo-q5_0',
        '--env', 'WhisperCpp__TemporaryDirectory=transcription-temp')
    if ($networkId) { $dockerArguments += @('--network', $networkId) }
    $dockerArguments += $Image
    $apiId = (& docker @dockerArguments).Trim()
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Linux API container creation failed.'
    & docker image inspect $Image *> (Join-Path $evidence 'image.json')
    & docker exec $apiId python3 /test/LinuxAudioSmoke.py --provider $Provider --profile $profileId
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Real Linux API smoke failed; inspect retained evidence.'
    & docker exec $apiId python3 -c 'import platform,os; print(platform.platform()); print(os.getuid())' *> (Join-Path $evidence 'host.txt')
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Linux host metadata collection failed.'
    & docker exec $apiId dpkg-query --show ffmpeg libgomp1 *> (Join-Path $evidence 'packages.txt')
    & python (Join-Path $PSScriptRoot 'Compare-AudioTranscript.py') (Join-Path $evidence 'result.json') *> (Join-Path $evidence 'comparison.log')
    Assert-LinuxSmoke ($LASTEXITCODE -eq 0) 'Language quality comparison failed.'
} finally {
    if ($apiId) { & docker logs $apiId *> (Join-Path $evidence 'api.log') }
    Remove-OwnedSmokeContainer $apiId
    Remove-OwnedSmokeContainer $databaseId
    if ($networkId) {
        $network = & docker network inspect $networkId | ConvertFrom-Json
        if ($LASTEXITCODE -eq 0 -and $network[0].Labels.'socialtelemetry.linux-smoke' -eq $runId) {
            & docker network rm $networkId | Out-Null
        }
    }
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Default', $originalConnection, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $originalEnvironment, 'Process')
    Write-Output "Linux smoke evidence: $evidence"
}
