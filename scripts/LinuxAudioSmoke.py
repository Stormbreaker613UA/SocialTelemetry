"""Call the real API from its own Linux loopback namespace; synthetic fixtures only."""
import argparse
import hashlib
import json
import pathlib
import os
import platform
import subprocess
import time
import urllib.error
import urllib.request
import uuid


PROFILE_HEADERS = {}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def request(method, path, body=None, data=None, content_type=None):
    headers = {'X-SocialTelemetry-Local': '1'}
    if not path.startswith('/local-profiles'):
        headers.update(PROFILE_HEADERS)
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode('utf-8')
        content_type = 'application/json'
    if content_type:
        headers['Content-Type'] = content_type
    message = urllib.request.Request('http://127.0.0.1:8080' + path, data=data, headers=headers, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        response = opener.open(message, timeout=650)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read(16 * 1024 * 1024 + 1)
        assert len(raw) <= 16 * 1024 * 1024, 'Response exceeded smoke bound'
        raw = raw.decode('utf-8')
        return response.status, json.loads(raw) if raw else None


def upload(interaction_id, filename, data, mime='audio/wav'):
    boundary = 'Smoke' + uuid.uuid4().hex
    content = (f'--{boundary}\r\nContent-Disposition: form-data; name="Type"\r\n\r\nAudio\r\n'
               f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="{filename}"\r\n'
               f'Content-Type: {mime}\r\n\r\n').encode() + data + f'\r\n--{boundary}--\r\n'.encode()
    status, attachment = request('POST', f'/interactions/{interaction_id}/attachments', data=content,
                                 content_type=f'multipart/form-data; boundary={boundary}')
    assert status == 201 and attachment['status'] in ('Ready', 1), 'Upload failed'
    return attachment['id']


def assert_native_cleanup():
    for directory in pathlib.Path('/proc').iterdir():
        if not directory.name.isdigit():
            continue
        try:
            status = (directory / 'status').read_text()
            state = next(line for line in status.splitlines() if line.startswith('State:')).split()[1]
            command = (directory / 'cmdline').read_bytes()
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
        if state in ('Z', 'X'):
            continue
        assert not any(name in command for name in (b'/opt/whisper/whisper-cli', b'/usr/bin/ffmpeg', b'--socialtelemetry-stt-launcher')), 'Owned native process still running'
    temporary = pathlib.Path('/data/transcription-temp')
    assert not temporary.exists() or not any(temporary.iterdir()), 'Execution temporary files remain'


def finish_after_restart(arguments, selection):
    checkpoint = json.loads(pathlib.Path('/evidence/restart.json').read_text(encoding='utf-8'))
    assert selection['userProfileId'] == checkpoint['profileId'], 'Restart changed selected profile'
    assert selection['version'] == checkpoint['profileVersion'], 'Restart changed selection version'
    PROFILE_HEADERS.update({'X-SocialTelemetry-ProfileId': checkpoint['profileId'],
                            'X-SocialTelemetry-ProfileVersion': checkpoint['profileVersion']})
    status, journal = request('GET', '/user-profile')
    assert status == 200 and journal['id'] == checkpoint['profileId']
    prefix = checkpoint['reviewedPrefix']
    status, stored = request('GET', prefix + '/transcript')
    assert status == 200 and stored['version'] == checkpoint['transcriptVersion']
    assert stored['effectiveText'] == checkpoint['effectiveText']
    assert stored['sourceSha256'] == checkpoint['sourceSha256']
    assert stored['reviewStatus'] in ('Reviewed', 1) and stored['reviewedAt']
    status, reused = request('POST', prefix + '/transcribe')
    assert status == 200 and reused['reused'] and reused['version'] == checkpoint['transcriptVersion']
    assert reused['effectiveText'] == checkpoint['effectiveText'], 'Missing model changed reviewed correction'
    missing_prefix = checkpoint['missingModelPrefix']
    status, failure = request('POST', missing_prefix + '/transcribe')
    assert status == 503 and failure['code'] == 'ConfigurationRequired'
    assert request('GET', missing_prefix + '/transcript')[0] == 404
    assert '/models/' not in json.dumps(failure) and '/data/' not in json.dumps(failure)
    assert_native_cleanup()
    assert request('DELETE', '/interactions/' + checkpoint['interactionId'])[0] == 200
    assert request('GET', prefix + '/transcript')[0] == 404
    assert request('DELETE', '/people/' + checkpoint['personId'])[0] == 200
    assert not any(path.is_file() and len(path.name) == 32 for path in pathlib.Path('/data/attachments').rglob('*'))
    assert_native_cleanup()
    result = checkpoint['result']
    result.update(status='PASS', restart='PASS', missingModel='PASS', deletionCleanup='PASS')
    pathlib.Path(arguments.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(arguments.provider + ' Linux restart/missing-model/reviewed-reuse/deletion/native cleanup PASS', flush=True)


parser = argparse.ArgumentParser()
parser.add_argument('--provider', choices=['Sqlite', 'PostgreSql'], required=True)
parser.add_argument('--model-id', default='large-v3-turbo-q5_0')
parser.add_argument('--phase', choices=['lifecycle', 'restart'], default='lifecycle')
parser.add_argument('--output', default='/evidence/result.json')
arguments = parser.parse_args()
for attempt in range(100):
    try:
        status, selection = request('GET', '/local-profiles')
        if status == 200 and selection.get('version'):
            break
    except (OSError, urllib.error.URLError):
        pass
    time.sleep(.5)
else:
    raise RuntimeError('Linux API did not become ready')
if arguments.phase == 'restart':
    finish_after_restart(arguments, selection)
    raise SystemExit(0)
assert not selection['profiles'] and selection['userProfileId'] is None, 'Expected a fresh isolated store'
status, profile = request('POST', '/local-profiles', body={
    'displayName': 'Synthetic Linux smoke', 'expectedVersion': selection['version']})
assert status == 201, 'Profile creation failed'
profile_id = profile['id']
status, selection = request('GET', '/local-profiles')
assert status == 200 and selection['userProfileId'] is None, 'Creation unexpectedly selected a profile'
status, selected = request('PUT', '/local-profiles/current', body={
    'userProfileId': profile_id, 'expectedVersion': selection['version']})
assert status == 200 and selected['userProfileId'] == profile_id, 'Profile selection failed'
PROFILE_HEADERS.update({'X-SocialTelemetry-ProfileId': profile_id,
                        'X-SocialTelemetry-ProfileVersion': selected['version']})
status, journal = request('GET', '/user-profile')
assert status == 200 and journal['id'] == profile_id, 'Profile-bound journal is missing'
status, person = request('POST', '/people', body={'userProfileId': profile_id,
                                                'displayName': 'Synthetic Linux Alex', 'relationshipContext': 0})
assert status == 201, 'Person creation failed'
status, interaction = request('POST', '/interactions', body={'userProfileId': profile_id,
    'title': 'Disposable real Linux STT smoke', 'description': 'Synthetic fixture only',
    'occurredAt': '2026-10-14T12:00:00Z', 'participantIds': [person['id']]})
assert status == 201, 'Interaction creation failed'
interaction_id = interaction['id']
fixtures = json.loads(pathlib.Path('/fixtures/fixtures.json').read_text(encoding='utf-8-sig'))
assert fixtures, 'Language manifest is empty'
samples = [dict(sample, file=pathlib.Path('/fixtures', sample['language'] + '.wav'), mime='audio/wav') for sample in fixtures]
# Reuse one existing synthetic fixture; no TTS/model download and no input modification.
converted = pathlib.Path('/data/smoke-fixtures')
converted.mkdir(exist_ok=False)
for extension, codec, mime in [('mp3', 'libmp3lame', 'audio/mpeg'), ('m4a', 'aac', 'audio/mp4'),
                              ('aac', 'aac', 'audio/aac'), ('ogg', 'libvorbis', 'audio/ogg'),
                              ('opus', 'libopus', 'audio/ogg'), ('webm', 'libopus', 'audio/webm')]:
    destination = converted / ('sample.' + extension)
    subprocess.run(['/usr/bin/ffmpeg', '-nostdin', '-hide_banner', '-loglevel', 'error',
                    '-i', str(samples[0]['file']), '-vn', '-ac', '1', '-c:a', codec, str(destination)],
                   check=True, timeout=60, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    samples.append({'language': fixtures[0]['language'] + '-' + extension,
                    'reference': fixtures[0]['reference'], 'file': destination, 'mime': mime})
observations = []
for sample in samples:
    audio = sample['file'].read_bytes()
    attachment_id = upload(interaction_id, sample['file'].name, audio, sample['mime'])
    prefix = f'/interactions/{interaction_id}/attachments/{attachment_id}'
    started = time.monotonic()
    status, transcript = request('POST', prefix + '/transcribe')
    elapsed = time.monotonic() - started
    assert status == 201, f"{sample['language']}: real Linux STT HTTP{status}; code={transcript.get('code')}"
    assert transcript['reviewStatus'] in ('Unreviewed', 0) and not transcript['reused']
    assert transcript['provider'] == 'whisper-cpp' and transcript['model'] == arguments.model_id
    assert transcript['transcriptionVersion'] == 'whisper-cli-text-v1'
    assert transcript['sourceSha256'].lower() == hashlib.sha256(audio).hexdigest()
    status, stored = request('GET', prefix + '/transcript')
    assert status == 200 and stored['generatedText'] == transcript['generatedText']
    status, reused = request('POST', prefix + '/transcribe')
    assert status == 200 and reused['reused'] and reused['version'] == transcript['version']
    status, corrected = request('PUT', prefix + '/transcript', body={
        'expectedVersion': transcript['version'], 'correctedText': sample['reference']})
    assert status == 200 and corrected['generatedText'] == transcript['generatedText']
    assert corrected['reviewStatus'] in ('Unreviewed', 0)
    assert corrected['effectiveText'] == sample['reference']
    assert corrected['sourceSha256'] == transcript['sourceSha256']
    status, reviewed = request('PUT', prefix + '/transcript', body={
        'expectedVersion': corrected['version'], 'confirmReviewed': True})
    assert status == 200 and reviewed['reviewStatus'] in ('Reviewed', 1)
    assert reviewed['reviewedAt'] and reviewed['generatedText'] == transcript['generatedText']
    status, preserved = request('POST', prefix + '/transcribe')
    assert status == 200 and preserved['reused'] and preserved['version'] == reviewed['version']
    assert preserved['effectiveText'] == sample['reference']
    assert_native_cleanup()
    observations.append({'language': sample['language'], 'seconds': round(elapsed, 2),
        'lifecycle': 'PASS', 'reference': sample['reference'], 'recognized': transcript['generatedText']})
    print(f"{arguments.provider} Linux {sample['language']}: real STT/Unicode/reuse/review/native cleanup PASS ({elapsed:.1f}s)", flush=True)
bad_id = upload(interaction_id, 'corrupt.wav', b'Synthetic corrupt audio')
bad_prefix = f'/interactions/{interaction_id}/attachments/{bad_id}'
status, failure = request('POST', bad_prefix + '/transcribe')
assert status == 400 and failure['code'] == 'InvalidAudio'
assert request('GET', bad_prefix + '/transcript')[0] == 404
assert '/data/' not in json.dumps(failure) and 'whisper_init' not in json.dumps(failure)
assert_native_cleanup()
missing_id = upload(interaction_id, 'missing-model.wav', audio)
result = {'provider': arguments.provider, 'status': 'PENDING_RESTART',
          'host': {'system': platform.system(), 'machine': platform.machine(), 'libc': platform.libc_ver(), 'uid': os.getuid()},
          'model': arguments.model_id, 'profileId': profile_id, 'profileVersion': selected['version'], 'formats': observations,
          'limits': 'One clean WAV per language plus six encodings of the first fixture (seven formats total); not broad live speech or ARM64/musl/crash/server-auth/deployment validation.'}
checkpoint = {'profileId': profile_id, 'profileVersion': selected['version'], 'interactionId': interaction_id,
              'personId': person['id'], 'reviewedPrefix': prefix, 'transcriptVersion': preserved['version'],
              'effectiveText': preserved['effectiveText'], 'sourceSha256': preserved['sourceSha256'],
              'missingModelPrefix': f'/interactions/{interaction_id}/attachments/{missing_id}', 'result': result}
pathlib.Path('/evidence/restart.json').write_text(json.dumps(checkpoint, ensure_ascii=False), encoding='utf-8')
pathlib.Path(arguments.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(arguments.provider + ' Linux lifecycle/corrupt/no-transcript PASS; restart still pending', flush=True)
