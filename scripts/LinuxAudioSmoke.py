"""Call the real API from its own Linux loopback namespace; synthetic fixtures only."""
import argparse
import hashlib
import json
import pathlib
import sqlite3
import time
import urllib.error
import urllib.request
import uuid


def request(method, path, body=None, data=None, content_type=None):
    headers = {'X-SocialTelemetry-Local': '1'}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode('utf-8')
        content_type = 'application/json'
    if content_type:
        headers['Content-Type'] = content_type
    message = urllib.request.Request('http://127.0.0.1:8080' + path, data=data, headers=headers, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        response = opener.open(message, timeout=650)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read().decode('utf-8')
        return response.status, json.loads(raw) if raw else None


def upload(interaction_id, filename, data):
    boundary = 'Smoke' + uuid.uuid4().hex
    content = (f'--{boundary}\r\nContent-Disposition: form-data; name="Type"\r\n\r\nAudio\r\n'
               f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="{filename}"\r\n'
               'Content-Type: audio/wav\r\n\r\n').encode() + data + f'\r\n--{boundary}--\r\n'.encode()
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


parser = argparse.ArgumentParser()
parser.add_argument('--provider', choices=['Sqlite', 'PostgreSql'], required=True)
parser.add_argument('--profile', required=True)
parser.add_argument('--output', default='/evidence/result.json')
arguments = parser.parse_args()
for attempt in range(100):
    try:
        status, _ = request('GET', '/user-profile')
        if status in (200, 404):
            break
    except (OSError, urllib.error.URLError):
        pass
    time.sleep(.5)
else:
    raise RuntimeError('Linux API did not become ready')
if arguments.provider == 'Sqlite':
    with sqlite3.connect('/data/smoke.db') as database:
        database.execute('INSERT INTO "UserProfiles" ("Id", "DisplayName") VALUES (?, ?)',
                         (arguments.profile.upper(), 'Synthetic Linux smoke'))
status, profile = request('GET', '/user-profile')
assert status == 200, 'Profile fixture is missing'
status, person = request('POST', '/people', body={'userProfileId': arguments.profile,
                                                'displayName': 'Synthetic Linux Alex', 'relationshipContext': 0})
assert status == 201, 'Person creation failed'
status, interaction = request('POST', '/interactions', body={'userProfileId': arguments.profile,
    'title': 'Disposable real Linux STT smoke', 'description': 'Synthetic fixture only',
    'occurredAt': '2026-10-14T12:00:00Z', 'participantIds': [person['id']]})
assert status == 201, 'Interaction creation failed'
interaction_id = interaction['id']
fixtures = json.loads(pathlib.Path('/fixtures/fixtures.json').read_text(encoding='utf-8-sig'))
observations = []
for sample in fixtures:
    audio = pathlib.Path('/fixtures', sample['language'] + '.wav').read_bytes()
    attachment_id = upload(interaction_id, sample['language'] + '.wav', audio)
    prefix = f'/interactions/{interaction_id}/attachments/{attachment_id}'
    started = time.monotonic()
    status, transcript = request('POST', prefix + '/transcribe')
    elapsed = time.monotonic() - started
    assert status == 201, f"{sample['language']}: real Linux STT HTTP{status}; code={transcript.get('code')}"
    assert transcript['reviewStatus'] in ('Unreviewed', 0) and not transcript['reused']
    assert transcript['provider'] == 'whisper-cpp' and transcript['model'] == 'large-v3-turbo-q5_0'
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
    status, reviewed = request('PUT', prefix + '/transcript', body={
        'expectedVersion': corrected['version'], 'confirmReviewed': True})
    assert status == 200 and reviewed['reviewStatus'] in ('Reviewed', 1)
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
assert request('DELETE', '/interactions/' + interaction_id)[0] == 200
assert request('GET', prefix + '/transcript')[0] == 404
assert request('DELETE', '/people/' + person['id'])[0] == 200
assert not any(path.is_file() and len(path.name) == 32 for path in pathlib.Path('/data/attachments').rglob('*'))
assert_native_cleanup()
result = {'provider': arguments.provider, 'status': 'PASS', 'host': 'Linux x64 Ubuntu24.04 glibc Docker --init UID1654 no caps',
          'model': 'large-v3-turbo-q5_0', 'formats': observations,
          'limits': 'One clean synthetic WAV/language; not broad live speech or ARM64/musl/crash/server-auth/deployment validation.'}
pathlib.Path(arguments.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(arguments.provider + ' Linux corrupt/no-transcript/deletion/native/temp cleanup PASS', flush=True)
