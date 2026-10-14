#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Sqlite', 'PostgreSql')][string]$Provider = 'Sqlite',
    [string]$ToolsDirectory = '',
    [string]$FixtureManifest = '',
    [string]$PostgresImage = 'postgres:18',
    [switch]$KeepFixtures
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $ToolsDirectory) { $ToolsDirectory = Join-Path $repository '.local-stt' }
$tools = Get-Content -LiteralPath (Join-Path $ToolsDirectory 'tools.json') -Raw | ConvertFrom-Json
foreach ($file in @($tools.Ffmpeg, $tools.Whisper, $tools.Model)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Configured STT file is missing.' }
}
$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $ToolsDirectory "smoke\$Provider-$runId"
New-Item -ItemType Directory -Path $runRoot | Out-Null
$fixture = Join-Path $runRoot 'fixture'
New-Item -ItemType Directory -Path $fixture | Out-Null
$reference = 'On Saturday, Alex and I watched a film. We agreed to meet again next week. This is a synthetic transcription test.'
$apiDirectory = Join-Path $repository 'SocialTelemetry.Api'
$apiDll = Join-Path $apiDirectory 'bin\Debug\net10.0\SocialTelemetry.Api.dll'
if (-not (Test-Path -LiteralPath $apiDll)) { throw 'Build SocialTelemetry.slnx before running the smoke.' }
$profileId = [Guid]::NewGuid().ToString()
$containerId = $null
$appProcess = $null
$interactionId = $null
$personId = $null
$http = $null
$savedEnvironment = @{}
$observations = [Collections.Generic.List[object]]::new()

function Assert-Smoke([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-Enum($Value, [string]$Name, [int]$Number) {
    return "$Value" -eq $Name -or "$Value" -eq "$Number"
}

function Set-SmokeEnvironment([string]$Name, [string]$Value) {
    if (-not $savedEnvironment.ContainsKey($Name)) {
        $savedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Invoke-SmokeApi([string]$Method, [string]$Path, $Body = $null, $Content = $null) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $script:baseUri + $Path)
    try {
        if ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8 -Compress), [Text.Encoding]::UTF8, 'application/json')
        } elseif ($null -ne $Content) { $request.Content = $Content }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $json = if ($text) { $text | ConvertFrom-Json } else { $null }
            return [pscustomobject]@{ Status=[int]$response.StatusCode; Body=$json; Text=$text }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Add-SmokeAudio([string]$File, [string]$Mime) {
    $form = [Net.Http.MultipartFormDataContent]::new()
    $form.Add([Net.Http.StringContent]::new('Audio'), 'Type')
    $audio = [Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($File))
    $audio.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new($Mime)
    $form.Add($audio, 'File', [IO.Path]::GetFileName($File))
    $response = Invoke-SmokeApi 'POST' "/interactions/$interactionId/attachments" $null $form
    Assert-Smoke ($response.Status -eq 201) 'Audio upload did not return 201.'
    Assert-Smoke (Test-Enum $response.Body.status 'Ready' 1) 'Audio is not Ready.'
    return $response.Body
}

function Assert-NoTemporaryExecution {
    $temporary = Join-Path $runRoot 'transcription-temp'
    Assert-Smoke (@(Get-ChildItem -LiteralPath $temporary -Force -ErrorAction SilentlyContinue).Count -eq 0) 'Native temporary execution files remain.'
}

function Start-SmokeHost([bool]$WithModel = $true) {
    Set-SmokeEnvironment 'WhisperCpp__ModelFile' $(if ($WithModel) { $tools.Model } else { Join-Path $runRoot 'missing-model.bin' })
    $suffix = if ($WithModel) { 'configured' } else { 'missing-model' }
    $script:appProcess = Start-Process -FilePath 'dotnet.exe' -ArgumentList @($apiDll, '--urls', $script:baseUri) -WorkingDirectory $apiDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot "$suffix.stdout.log") -RedirectStandardError (Join-Path $runRoot "$suffix.stderr.log")
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($appProcess.HasExited) { throw 'Smoke API exited during startup; inspect the private run directory.' }
        try {
            $response = Invoke-SmokeApi 'GET' '/user-profile'
            if ($response.Status -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Smoke API startup timed out.'
}

function Stop-SmokeHost {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id
        $appProcess.WaitForExit()
    }
    $script:appProcess = $null
}

function Complete-SmokeArtifacts([string]$Root) {
    $absoluteRoot = [IO.Path]::GetFullPath($Root)
    $smokeRoot = [IO.Path]::GetFullPath((Join-Path $ToolsDirectory 'smoke'))
    $name = [IO.Path]::GetFileName($absoluteRoot)
    Assert-Smoke ([IO.Path]::GetDirectoryName($absoluteRoot) -eq $smokeRoot -and $name -match '^(Sqlite|PostgreSql)-[a-f0-9]{32}$') 'Unexpected cleanup directory.'
    $directory = [IO.DirectoryInfo]::new($absoluteRoot)
    while ($directory) {
        Assert-Smoke (-not ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Smoke cleanup cannot follow a linked directory.'
        $directory = $directory.Parent
    }
    Assert-Smoke ((Get-Content -LiteralPath (Join-Path $absoluteRoot 'result.json') -Raw | ConvertFrom-Json).status -eq 'PASS') 'Unsuccessful smoke fixtures must be retained for diagnosis.'
    $evidenceRoot = Join-Path $ToolsDirectory "smoke-evidence\$name"
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -File | Where-Object { $_.Name -eq 'result.json' -or $_.Extension -eq '.log' }) {
        $destination = Join-Path $evidenceRoot $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        Assert-Smoke ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $destination).Hash) 'Evidence copy verification failed.'
    }
    # Only this generated run directory is removed, after the owned host stopped.
    [IO.Directory]::Delete($absoluteRoot, $true)
    Write-Output "Retained smoke evidence: $evidenceRoot"
}

try {
    Add-Type -AssemblyName System.Net.Http
    if ($FixtureManifest) {
        $languageFixtures = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $FixtureManifest -Raw -Encoding UTF8)
        $formats = @(foreach ($sample in $languageFixtures) {
            Assert-Smoke ($sample.language -is [string] -and $sample.file -is [string] -and $sample.reference -is [string]) 'Fixture fields must be individual strings.'
            @{ Extension=$sample.language; Mime='audio/wav'; File=$sample.file; Reference=$sample.reference }
        })
        Assert-Smoke ($formats.Count -gt 0) 'Fixture manifest is empty.'
        foreach ($format in $formats) {
            Copy-Item -LiteralPath $format.File -Destination (Join-Path $fixture ("sample." + $format.Extension))
        }
        Copy-Item -LiteralPath $formats[0].File -Destination (Join-Path $fixture 'sample.wav')
    } else {
        Add-Type -AssemblyName System.Speech
        $speaker = [Speech.Synthesis.SpeechSynthesizer]::new()
        try {
            $speaker.SelectVoice('Microsoft Zira Desktop')
            $speaker.SetOutputToWaveFile((Join-Path $fixture 'speech.wav'))
            $speaker.Speak($reference)
        } finally { $speaker.Dispose() }
    $formats = @(
        @{ Extension='wav'; Mime='audio/wav'; Codec='pcm_s16le' },
        @{ Extension='mp3'; Mime='audio/mpeg'; Codec='libmp3lame' },
        @{ Extension='m4a'; Mime='audio/mp4'; Codec='aac' },
        @{ Extension='aac'; Mime='audio/aac'; Codec='aac' },
        @{ Extension='ogg'; Mime='audio/ogg'; Codec='libvorbis' },
        @{ Extension='opus'; Mime='audio/ogg'; Codec='libopus' },
        @{ Extension='webm'; Mime='audio/webm'; Codec='libopus' }
    )
    foreach ($format in $formats) {
        $destination = Join-Path $fixture ("sample." + $format.Extension)
        & $tools.Ffmpeg -nostdin -hide_banner -loglevel error -y -i (Join-Path $fixture 'speech.wav') -vn -ac 1 -c:a $format.Codec $destination
        Assert-Smoke ($LASTEXITCODE -eq 0) 'Synthetic format conversion failed.'
    }
    }
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start()
    $httpPort = $socket.LocalEndpoint.Port
    $socket.Stop()
    $script:baseUri = "http://127.0.0.1:$httpPort"
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromMinutes(12)
    $http.DefaultRequestHeaders.Add('X-SocialTelemetry-Local', '1')

    Set-SmokeEnvironment 'ASPNETCORE_ENVIRONMENT' 'Production'
    Set-SmokeEnvironment 'Persistence__Provider' $Provider
    Set-SmokeEnvironment 'ApplicationData__RootDirectory' $runRoot
    Set-SmokeEnvironment 'Persistence__SqliteFile' 'smoke.db'
    Set-SmokeEnvironment 'AttachmentStorage__LocalDirectory' 'attachments'
    Set-SmokeEnvironment 'ProfileStorage__AvatarDirectory' 'avatars'
    Set-SmokeEnvironment 'ChatGpt__DataDirectory' (Join-Path $runRoot 'credentials')
    Set-SmokeEnvironment 'WhisperCpp__FfmpegExecutable' $tools.Ffmpeg
    Set-SmokeEnvironment 'WhisperCpp__WhisperExecutable' $tools.Whisper
    Set-SmokeEnvironment 'WhisperCpp__ModelFile' $tools.Model
    Set-SmokeEnvironment 'WhisperCpp__ModelId' $tools.ModelId
    Set-SmokeEnvironment 'WhisperCpp__TemporaryDirectory' 'transcription-temp'
    Set-SmokeEnvironment 'ConnectionStrings__Default' ''

    if ($Provider -eq 'PostgreSql') {
        $containerId = (& docker run --detach --name "socialtelemetry-audio-smoke-$runId" --label "socialtelemetry.audio-smoke=$runId" --publish '127.0.0.1::5432' --tmpfs '/var/lib/postgresql' --env 'POSTGRES_DB=smoke' --env 'POSTGRES_USER=smoke' --env 'POSTGRES_PASSWORD=disposable-smoke' $PostgresImage).Trim()
        Assert-Smoke ($LASTEXITCODE -eq 0) 'Disposable PostgreSQL container did not start.'
        $portMap = (& docker inspect $containerId | ConvertFrom-Json)[0].NetworkSettings.Ports.'5432/tcp'[0].HostPort
        Set-SmokeEnvironment 'ConnectionStrings__Default' "Host=127.0.0.1;Port=$portMap;Database=smoke;Username=smoke;Password=disposable-smoke"
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & docker exec $containerId pg_isready -U smoke -d smoke *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        Assert-Smoke $ready 'PostgreSQL readiness timed out.'
    }
    & dotnet ef database update --project $apiDirectory --no-build -- --provider $Provider *> (Join-Path $runRoot 'migrations.log')
    Assert-Smoke ($LASTEXITCODE -eq 0) 'Provider migrations failed; inspect the run directory.'
    # Profile creation is not exposed by the current API. Seed only this isolated fixture.
    if ($Provider -eq 'Sqlite') {
        @'
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as db:
    db.execute('INSERT INTO "UserProfiles" ("Id", "DisplayName") VALUES (?, ?)', (sys.argv[2].upper(), 'Synthetic audio smoke'))
'@ | & python - (Join-Path $runRoot 'smoke.db') $profileId
        Assert-Smoke ($LASTEXITCODE -eq 0) 'SQLite fixture seed failed.'
    } else {
        "INSERT INTO `"UserProfiles`" (`"Id`", `"DisplayName`") VALUES ('$profileId', 'Synthetic audio smoke');" | & docker exec -i $containerId psql -U smoke -d smoke -v ON_ERROR_STOP=1 *> (Join-Path $runRoot 'seed.log')
        Assert-Smoke ($LASTEXITCODE -eq 0) 'PostgreSQL fixture seed failed.'
    }
    Start-SmokeHost
    $person = Invoke-SmokeApi 'POST' '/people' @{userProfileId=$profileId; displayName='Synthetic Alex'; relationshipContext=0}
    Assert-Smoke ($person.Status -eq 201) 'Person creation failed.'
    $personId = $person.Body.id
    $interaction = Invoke-SmokeApi 'POST' '/interactions' @{userProfileId=$profileId; title='Disposable audio smoke'; description='Synthetic verification only'; occurredAt=[DateTimeOffset]::UtcNow.ToString('o'); participantIds=@($personId)}
    Assert-Smoke ($interaction.Status -eq 201) 'Interaction creation failed.'
    $interactionId = $interaction.Body.id
    $retainedAttachment = $null
    $retainedTranscript = $null
    foreach ($format in $formats) {
        $attachment = Add-SmokeAudio (Join-Path $fixture ("sample." + $format.Extension)) $format.Mime
        $prefix = "/interactions/$interactionId/attachments/$($attachment.id)"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $generated = Invoke-SmokeApi 'POST' "$prefix/transcribe"
        $watch.Stop()
        Assert-Smoke ($generated.Status -eq 201) "Real transcription failed for $($format.Extension), HTTP $($generated.Status)."
        $transcript = $generated.Body
        Assert-Smoke (-not $transcript.reused -and (Test-Enum $transcript.reviewStatus 'Unreviewed' 0)) 'Generated transcript was reused or reviewed.'
        Assert-Smoke ($transcript.provider -eq 'whisper-cpp' -and $transcript.model -eq $tools.ModelId -and $transcript.transcriptionVersion -eq 'whisper-cli-text-v1') 'STT provenance mismatch.'
        Assert-Smoke ($transcript.sourceSha256 -eq (Get-FileHash -LiteralPath (Join-Path $fixture ("sample." + $format.Extension)) -Algorithm SHA256).Hash) 'Source digest mismatch.'
        if ($FixtureManifest) {
            $reference = $format.Reference
        } else {
            foreach ($word in @('Saturday', 'watched', 'film', 'next week')) {
                Assert-Smoke ($transcript.effectiveText -match [regex]::Escape($word)) 'Known synthetic speech content was not recognized.'
            }
        }
        $stored = Invoke-SmokeApi 'GET' "$prefix/transcript"
        Assert-Smoke ($stored.Status -eq 200 -and $stored.Body.version -eq $transcript.version) 'Persisted transcript mismatch.'
        $reused = Invoke-SmokeApi 'POST' "$prefix/transcribe"
        Assert-Smoke ($reused.Status -eq 200 -and $reused.Body.reused -and $reused.Body.version -eq $transcript.version -and $reused.Body.effectiveText -eq $transcript.effectiveText) 'Repeat transcription did not preserve the result.'
        $corrected = Invoke-SmokeApi 'PUT' "$prefix/transcript" @{expectedVersion=$transcript.version; correctedText=$reference}
        Assert-Smoke ($corrected.Status -eq 200 -and $corrected.Body.generatedText -eq $transcript.generatedText -and (Test-Enum $corrected.Body.reviewStatus 'Unreviewed' 0)) 'Correction did not preserve generated text/unreviewed state.'
        $reviewed = Invoke-SmokeApi 'PUT' "$prefix/transcript" @{expectedVersion=$corrected.Body.version; confirmReviewed=$true}
        Assert-Smoke ($reviewed.Status -eq 200 -and (Test-Enum $reviewed.Body.reviewStatus 'Reviewed' 1)) 'Version-aware review failed.'
        $reuseCorrected = Invoke-SmokeApi 'POST' "$prefix/transcribe"
        Assert-Smoke ($reuseCorrected.Status -eq 200 -and $reuseCorrected.Body.reused -and $reuseCorrected.Body.version -eq $reviewed.Body.version -and $reuseCorrected.Body.effectiveText -eq $reference) 'Reuse overwrote corrected/reviewed transcript.'
        Assert-NoTemporaryExecution
        $retainedAttachment = $attachment
        $retainedTranscript = $reviewed.Body
        if ($FixtureManifest) {
            $observations.Add([pscustomobject]@{language=$format.Extension; seconds=[math]::Round($watch.Elapsed.TotalSeconds,2); lifecycle='PASS'; reference=$reference; recognized=$transcript.generatedText})
        } else {
            $observations.Add([pscustomobject]@{format=$format.Extension; seconds=[math]::Round($watch.Elapsed.TotalSeconds,2); lifecycle='PASS'; recognition='Known-content check passed'})
        }
        Write-Output "$Provider $($format.Extension): transcription/persistence/reuse/correction/review/temp cleanup PASS ($([math]::Round($watch.Elapsed.TotalSeconds,1))s)"
    }
    $corrupt = Join-Path $fixture 'corrupt.wav'
    [IO.File]::WriteAllText($corrupt, 'Disposable corrupt input')
    $badAttachment = Add-SmokeAudio $corrupt 'audio/wav'
    $badPrefix = "/interactions/$interactionId/attachments/$($badAttachment.id)"
    $bad = Invoke-SmokeApi 'POST' "$badPrefix/transcribe"
    Assert-Smoke ($bad.Status -eq 400 -and $bad.Body.code -eq 'InvalidAudio') 'Corrupt input did not produce safe InvalidAudio.'
    Assert-Smoke ((Invoke-SmokeApi 'GET' "$badPrefix/transcript").Status -eq 404) 'Failed transcription persisted a transcript.'
    Assert-NoTemporaryExecution
    $retainedPrefix = "/interactions/$interactionId/attachments/$($retainedAttachment.id)"
    $unchanged = Invoke-SmokeApi 'GET' "$retainedPrefix/transcript"
    Assert-Smoke ($unchanged.Body.version -eq $retainedTranscript.version) 'Corrupt input changed an existing transcript.'
    $missingAttachment = Add-SmokeAudio (Join-Path $fixture 'sample.wav') 'audio/wav'
    Stop-SmokeHost
    Start-SmokeHost $false
    $missingPrefix = "/interactions/$interactionId/attachments/$($missingAttachment.id)"
    $missing = Invoke-SmokeApi 'POST' "$missingPrefix/transcribe"
    Assert-Smoke ($missing.Status -eq 503 -and $missing.Body.code -eq 'ConfigurationRequired') 'Missing model did not produce safe ConfigurationRequired.'
    Assert-Smoke ((Invoke-SmokeApi 'GET' "$missingPrefix/transcript").Status -eq 404) 'Missing configuration persisted a transcript.'
    $preserved = Invoke-SmokeApi 'POST' "$retainedPrefix/transcribe"
    Assert-Smoke ($preserved.Status -eq 200 -and $preserved.Body.reused -and $preserved.Body.version -eq $retainedTranscript.version) 'Existing transcript cannot be reused without STT configuration.'
    foreach ($errorResponse in @($bad, $missing)) {
        Assert-Smoke ($errorResponse.Text -notmatch [regex]::Escape($runRoot)) 'ProblemDetails leaked a private path.'
        Assert-Smoke ($errorResponse.Text -notmatch 'whisper_init|ffmpeg version|On Saturday') 'ProblemDetails leaked native output/content.'
    }
    Assert-NoTemporaryExecution
    $delete = Invoke-SmokeApi 'DELETE' "/interactions/$interactionId"
    Assert-Smoke ($delete.Status -eq 200) 'Synthetic Interaction deletion failed.'
    Assert-Smoke ((Invoke-SmokeApi 'GET' "$retainedPrefix/transcript").Status -eq 404) 'Transcript remains after Interaction deletion.'
    $deletePerson = Invoke-SmokeApi 'DELETE' "/people/$personId"
    Assert-Smoke ($deletePerson.Status -eq 200) 'Synthetic Person deletion failed.'
    $remainingMedia = @(Get-ChildItem -LiteralPath (Join-Path $runRoot 'attachments') -File -Recurse | Where-Object { $_.BaseName -match '^[a-fA-F0-9]{32}$' })
    Assert-Smoke ($remainingMedia.Count -eq 0) 'Synthetic media remains after deletion.'
    Write-Output "$Provider corrupt/missing-config/no-transcript/preservation/restart/deletion PASS"
    $limits = if ($FixtureManifest) { 'One synthetic WAV per language; quality comparison is separate from lifecycle PASS. Windows CPU backend, not noisy/live/multi-speaker or real Linux inference.' } else { 'English synthetic speech; Windows CPU backend, not a Linux native runtime or broad multilingual quality test.' }
    @{provider=$Provider; status='PASS'; model=$tools.ModelId; formats=$observations; limits=$limits} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding UTF8
} finally {
    Stop-SmokeHost
    if ($http) { $http.Dispose() }
    if ($containerId) {
        $inspection = & docker inspect $containerId | ConvertFrom-Json
        if ($LASTEXITCODE -eq 0 -and $inspection[0].Config.Labels.'socialtelemetry.audio-smoke' -eq $runId) {
            & docker rm --force $containerId | Out-Null
        }
    }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if (-not $KeepFixtures -and (Test-Path -LiteralPath (Join-Path $runRoot 'result.json'))) {
        Complete-SmokeArtifacts $runRoot
    } else { Write-Output "Smoke artifacts: $runRoot" }
}
