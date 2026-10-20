"""One concrete current-API HTTP checkset, run locally on Windows or inside the Linux API namespace.
No OAuth connection, inference, native recognition, or production-only test switches.
"""
import argparse
import hashlib
import json
import pathlib
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import base64

BASE_URL = ''
BINDING = None
CHECKS = []
CURRENT_CHECK = 'Startup'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def require(condition, message):
    if not condition:
        if CHECKS:
            CHECKS[-1]['outcome'] = 'FAIL'
        raise RuntimeError(message)


def request(method, path, body=None, data=None, mime=None, binding=None, with_binding=True,
            without_local=False, extra=None):
    require(path.startswith('/') and not path.startswith('//'), 'Only fixed relative API paths are permitted')
    headers = {} if without_local else {'X-SocialTelemetry-Local': '1'}
    owner = binding if binding is not None else BINDING
    if with_binding and owner and not path.startswith(('/local-profiles', '/ai-connection/chatgpt/callback')):
        headers.update({'X-SocialTelemetry-ProfileId': owner['userProfileId'],
                        'X-SocialTelemetry-ProfileVersion': owner['version']})
    headers.update(extra or {})
    if body is not None:
        data = json.dumps(body).encode('utf-8')
        mime = 'application/json'
    if mime:
        headers['Content-Type'] = mime
    message = urllib.request.Request(BASE_URL + path, data=data, headers=headers, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        response = opener.open(message, timeout=90)
    except urllib.error.HTTPError as failure:
        response = failure
    with response:
        raw = response.read(16 * 1024 * 1024 + 1)
        require(len(raw) <= 16 * 1024 * 1024, 'Response exceeded smoke bound')
        content_type = response.headers.get('Content-Type', '')
        value = json.loads(raw) if raw and 'json' in content_type else None
        return {'status': response.status, 'body': value, 'bytes': raw,
                'headers': dict(response.headers.items())}


def check(name, method, path, status, code=None, **parameters):
    global CURRENT_CHECK
    CURRENT_CHECK = name
    response = request(method, path, **parameters)
    actual_code = (response['body'] or {}).get('code') if isinstance(response['body'], dict) else None
    passed = response['status'] == status and (code is None or actual_code == code)
    CHECKS.append({'check': name, 'method': method, 'path': path, 'expectedStatus': status,
                   'actualStatus': response['status'], 'expectedCode': code,
                   'outcome': 'PASS' if passed else 'FAIL'})
    require(passed, 'Unexpected status/category; raw response withheld')
    return response


def form(kind=None, data=None, mime='image/png', text=None):
    boundary = 'Smoke' + uuid.uuid4().hex
    pieces = []
    for name, value in [('Type', kind), ('TextContent', text)]:
        if value is not None:
            pieces.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    if data is not None:
        pieces.append((f'--{boundary}\r\nContent-Disposition: form-data; name="File"; filename="synthetic.bin"\r\n'
                       f'Content-Type: {mime}\r\n\r\n').encode() + data + b'\r\n')
    pieces.append(f'--{boundary}--\r\n'.encode())
    return {'data': b''.join(pieces), 'mime': f'multipart/form-data; boundary={boundary}'}


def select(owner):
    global BINDING
    before = check('Read selection', 'GET', '/local-profiles', 200)['body']
    BINDING = check('Select with CAS', 'PUT', '/local-profiles/current', 200,
                    body={'userProfileId': owner, 'expectedVersion': before['version']})['body']
    require(BINDING['userProfileId'] == owner, 'Wrong selected owner')


def run_checks():
    for attempt in range(100):
        try:
            ready = request('GET', '/local-profiles')
            if ready['status'] == 200 and (ready['body'] or {}).get('version'):
                break
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(.3)
    else:
        raise RuntimeError('Owned API readiness failed')
    initial = check('Fresh profile listing', 'GET', '/local-profiles', 200)['body']
    require(not initial['profiles'] and initial['userProfileId'] is None, 'Expected a fresh isolated store')
    version = initial['version']
    check('No selected journal', 'GET', '/user-profile', 409, 'SelectionRequired')
    check('Selector local-request protection', 'GET', '/local-profiles', 403, 'LocalRequestRequired', without_local=True)
    check('Cross-origin blocked', 'GET', '/local-profiles', 403, 'LocalRequestRequired', extra={'Origin': 'https://example.invalid'})
    check('Cross-site blocked', 'GET', '/local-profiles', 403, 'LocalRequestRequired', extra={'Sec-Fetch-Site': 'cross-site'})
    check('Foreign Host blocked', 'GET', '/local-profiles', 403, 'LocalRequestRequired', extra={'Host': 'example.invalid'})
    check('Blank profile rejected', 'POST', '/local-profiles', 400,
          body={'displayName': ' ', 'expectedVersion': version})
    owner_a = check('Create A without Account/selection', 'POST', '/local-profiles', 201,
                    body={'displayName': 'Synthetic A', 'expectedVersion': version})['body']['id']
    owner_b = check('Create B without Account/selection', 'POST', '/local-profiles', 201,
                    body={'displayName': 'Synthetic B', 'expectedVersion': version})['body']['id']
    profiles = check('List both local profiles', 'GET', '/local-profiles', 200)['body']
    require(len(profiles['profiles']) == 2 and profiles['userProfileId'] is None and
            all(not profile['accountLinked'] for profile in profiles['profiles']), 'Creation changed selection/account linkage')
    select(owner_a)
    binding_a = dict(BINDING)
    check('Stale selection CAS', 'PUT', '/local-profiles/current', 409, 'SelectionChanged',
          body={'userProfileId': owner_b, 'expectedVersion': version})
    same = check('Same selection is a no-op', 'PUT', '/local-profiles/current', 200,
                 body={'userProfileId': owner_a, 'expectedVersion': BINDING['version']})['body']
    require(same['version'] == BINDING['version'], 'No-op selection changed version')
    check('Missing assertion', 'GET', '/user-profile', 409, 'AssertionRequired', with_binding=False)
    check('Malformed assertion', 'GET', '/user-profile', 400, 'InvalidAssertion',
          extra={'X-SocialTelemetry-ProfileId': 'invalid'})
    check('Forged owner', 'GET', '/user-profile', 409, 'SelectionChanged',
          extra={'X-SocialTelemetry-ProfileId': owner_b})
    journal = check('Read selected journal', 'GET', '/user-profile', 200)
    require(journal['body']['id'] == owner_a, 'Journal retargeted')
    headers = {key.lower(): value for key, value in journal['headers'].items()}
    require(headers.get('x-socialtelemetry-profileid') == owner_a and
            headers.get('x-socialtelemetry-profileversion') == BINDING['version'] and
            'no-store' in headers.get('cache-control', ''), 'Owner/cache response headers mismatch')
    updated = check('Update journal', 'PUT', '/user-profile', 200,
                    body={'displayName': 'Synthetic A updated', 'aboutMe': 'Synthetic journal', 'goals': 'Synthetic goal'})['body']
    require(updated['id'] == owner_a and updated['aboutMe'] == 'Synthetic journal', 'Journal update mismatch')
    person = check('Create Person', 'POST', '/people', 201,
                   body={'userProfileId': owner_a, 'displayName': ' Synthetic Alex ', 'notes': 'Synthetic original notes', 'relationshipContext': 0})['body']['id']
    require(check('Read Person', 'GET', f'/people/{person}', 200)['body']['displayName'] == 'Synthetic Alex', 'Name was not trimmed')
    check('Update Person', 'PUT', f'/people/{person}', 200,
          body={'displayName': 'Synthetic Alex updated', 'notes': 'Synthetic original notes', 'relationshipContext': 0})
    require(check('Read updated Person', 'GET', f'/people/{person}', 200)['body']['displayName'] == 'Synthetic Alex updated', 'Person update not persisted')
    check('List People', 'GET', f'/people?userProfileId={owner_a}', 200)
    check('Archive Person', 'PUT', f'/people/{person}/archive', 204)
    archived = check('Archived filter', 'GET', f'/people?userProfileId={owner_a}&archived=true', 200)['body']['people']
    require(any(row['id'] == person and row['archivedAt'] for row in archived), 'Archived visibility mismatch')
    active = check('Active filter excludes archived', 'GET', f'/people?userProfileId={owner_a}&archived=false', 200)['body']['people']
    require(all(row['id'] != person for row in active), 'Archived person leaked into active filter')
    check('Unarchive Person', 'DELETE', f'/people/{person}/archive', 204)
    require(any(row['id'] == person and row['archivedAt'] is None for row in
                check('Unarchived Person visible', 'GET', f'/people?userProfileId={owner_a}&archived=false', 200)['body']['people']), 'Unarchive not persisted')
    png = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=')
    avatars = ['/user-profile/avatar', f'/people/{person}/avatar']
    for path in avatars:
        check('Upload avatar', 'PUT', path, 204, **form(data=png))
        require(check('Read avatar bytes', 'GET', path, 200)['bytes'] == png, 'Avatar bytes changed')
        check('Invalid avatar signature', 'PUT', path, 400, **form(data=b'bad'))
        require(check('Invalid replacement preserved avatar', 'GET', path, 200)['bytes'] == png, 'Old avatar lost')
    external = {'platform': ' Example ', 'externalUserId': 'opaque-smoke-id', 'handle': 'synthetic', 'profileUrl': 'https://example.invalid/synthetic'}
    connections = []
    for path in ['/user-profile/external-connections', f'/people/{person}/external-connections']:
        value = check('Create external identity', 'POST', path, 201, body=external)['body']
        require(value['platform'] == 'example', 'Platform normalization mismatch')
        child = value['id']
        connections.append((path, child))
        check('Duplicate stable identity', 'POST', path, 409, body=external)
        check('List external identities', 'GET', path, 200)
        check('Read external identity', 'GET', f'{path}/{child}', 200)
        check('Update external identity', 'PUT', f'{path}/{child}', 200,
              body={**external, 'platform': 'example', 'handle': 'updated'})
        require(check('Read updated identity', 'GET', f'{path}/{child}', 200)['body']['handle'] == 'updated', 'External identity update mismatch')
    fact = check('Add confirmed fact', 'POST', f'/people/{person}/facts', 201,
                 body={'value': 'Synthetic confirmed fact', 'source': 'Synthetic input'})['body']['id']
    check('List facts', 'GET', f'/people/{person}/facts', 200)
    check('Update fact', 'PUT', f'/people/{person}/facts/{fact}', 200,
          body={'value': 'Synthetic corrected fact', 'source': 'Synthetic correction'})
    require(any(row['id'] == fact and row['value'] == 'Synthetic corrected fact' for row in
                check('Read updated facts', 'GET', f'/people/{person}/facts', 200)['body']['facts']), 'Fact update mismatch')
    occurred = '2026-10-20T12:00:00Z'
    interaction_body = {'userProfileId': owner_a, 'title': 'Synthetic interaction', 'description': 'Synthetic evidence',
                        'occurredAt': occurred, 'participantIds': [person, person]}
    interaction = check('Create interaction', 'POST', '/interactions', 201, body=interaction_body)['body']['id']
    require(len(check('Read interaction/participants', 'GET', f'/interactions/{interaction}', 200)['body']['participants']) == 1, 'Duplicate participant retained')
    check('Update interaction', 'PUT', f'/interactions/{interaction}', 200,
          body={'title': 'Synthetic updated', 'description': 'Synthetic updated evidence', 'occurredAt': occurred, 'participantIds': [person]})
    check('List Person interactions', 'GET', f'/people/{person}/interactions', 200)
    prefix = f'/interactions/{interaction}/attachments'
    text = check('Add text attachment', 'POST', prefix, 201, **form(kind='Text', text='Synthetic text evidence'))['body']
    check('Read text metadata', 'GET', f"{prefix}/{text['id']}", 200)
    check('Text has no file content', 'GET', f"{prefix}/{text['id']}/content", 400)
    file_ids = []
    for kind in ['Image', 'Screenshot']:
        attachment = check('Add ' + kind, 'POST', prefix, 201, **form(kind=kind, data=png))['body']
        require(attachment['status'] in ('Ready', 1), 'File not Ready')
        file_ids.append(attachment['id'])
        check('Read Ready file metadata', 'GET', f"{prefix}/{attachment['id']}", 200)
        require(check('Download evidence bytes', 'GET', f"{prefix}/{attachment['id']}/content", 200)['bytes'] == png, 'Evidence bytes changed')
    audio_bytes = b'Synthetic stored audio bytes; no recognition claim.'
    audio = check('Add Audio storage fixture', 'POST', prefix, 201, **form(kind='Audio', data=audio_bytes, mime='audio/wav'))['body']
    audio_path = f"{prefix}/{audio['id']}"
    require(check('Download raw audio unchanged', 'GET', audio_path + '/content', 200)['bytes'] == audio_bytes, 'Raw audio changed')
    check('No transcript yet', 'GET', audio_path + '/transcript', 404)
    check('STT missing configuration, no native execution', 'POST', audio_path + '/transcribe', 503, 'ConfigurationRequired')
    check('Failed STT created no transcript', 'GET', audio_path + '/transcript', 404)
    manual = check('Create manual unreviewed transcript', 'PUT', audio_path + '/transcript', 201,
                   body={'correctedText': 'Synthetic manual transcript'})['body']
    digest = hashlib.sha256(audio_bytes).hexdigest().upper()
    require(manual['sourceSha256'] == digest and manual['generatedText'] is None and
            manual['effectiveText'] == manual['correctedText'], 'Manual text/source provenance mismatch')
    check('Unreviewed audio not analyzed', 'POST', f'/interactions/{interaction}/analyze', 409, 'TranscriptNotReady', body={'attachmentIds': [audio['id']]})
    reviewed = check('Review exact version', 'PUT', audio_path + '/transcript', 200,
                     body={'expectedVersion': manual['version'], 'confirmReviewed': True})['body']
    require(reviewed['reviewedAt'] and reviewed['reviewStatus'] in ('Reviewed', 1), 'Review mismatch')
    check('Stale transcript CAS', 'PUT', audio_path + '/transcript', 409,
          body={'expectedVersion': manual['version'], 'correctedText': 'Stale edit'})
    require(check('Read reviewed transcript', 'GET', audio_path + '/transcript', 200)['body']['version'] == reviewed['version'], 'Failed CAS changed transcript')
    reused = check('Manual transcript reused without STT configuration', 'POST', audio_path + '/transcribe', 200)['body']
    require(reused['reused'] and reused['version'] == reviewed['version'] and reused['sourceSha256'] == digest and
            reused['effectiveText'] == manual['effectiveText'], 'Reuse overwrote transcript')
    check('Reviewed audio normalized but AI disconnected', 'POST', f'/interactions/{interaction}/analyze', 409, 'NotConnected', body={'attachmentIds': [audio['id']]})
    check('Text analysis disconnected', 'POST', f'/interactions/{interaction}/analyze', 409, 'NotConnected', body={'attachmentIds': []})
    check('Missing evidence not silently dropped', 'POST', f'/interactions/{interaction}/analyze', 400, 'InvalidEvidenceSelection', body={'attachmentIds': [str(uuid.uuid4())]})
    ai = '/ai-connection/chatgpt'
    status = check('Read disconnected AI status', 'GET', ai + '/status', 200)['body']
    require(not status['connected'] and status['connectionId'] is None, 'Fresh store connected unexpectedly')
    require(check('Fresh unassigned credentials empty', 'GET', ai + '/unassigned', 200)['body'] == [], 'Credential store not isolated')
    check('Models require connection', 'GET', ai + '/models', 409, 'NotConnected')
    check('Model selection requires connection', 'PUT', ai + '/model', 409, 'NotConnected', body={'modelId': 'synthetic-model'})
    check('Unknown connection cannot be implicitly adopted', 'PUT', ai + '/connection', 409, 'NotConnected', body={'connectionId': str(uuid.uuid4()), 'adoptUnassigned': False})
    check('Begin blocked without local header, no browser', 'POST', ai + '/connect', 403, 'LocalRequestRequired', body={}, without_local=True)
    check('Invalid callback, no token exchange', 'GET', ai + '/callback', 400, 'InvalidCallback')
    check('Disconnect empty owned AI state', 'DELETE', ai, 200)
    select(owner_b)
    status_b = check('B AI state disconnected', 'GET', ai + '/status', 200)['body']
    require(not status_b['connected'] and status_b['connectionId'] is None, 'A AI state leaked to B')
    journal_b = check('Separate B journal', 'GET', '/user-profile', 200)['body']
    require(journal_b['id'] == owner_b and not journal_b['aboutMe'], 'A journal leaked to B')
    check('Old form rejected after switch', 'GET', f'/people/{person}', 409, 'SelectionChanged', binding=binding_a)
    for path in [f'/people/{person}', f'/people/{person}/facts', f'/people/{person}/avatar',
                 f'/people/{person}/external-connections/{connections[1][1]}', f'/interactions/{interaction}',
                 audio_path + '/content', audio_path + '/transcript', '/user-profile/avatar',
                 f'/user-profile/external-connections/{connections[0][1]}']:
        check('Foreign root/child/media unavailable', 'GET', path, 404)
    check('Foreign Person update', 'PUT', f'/people/{person}', 404, body={'displayName': 'Must not save', 'relationshipContext': 0})
    check('Foreign Person deletion', 'DELETE', f'/people/{person}', 404)
    check('Foreign interaction update', 'PUT', f'/interactions/{interaction}', 404, body=interaction_body)
    check('Foreign interaction deletion', 'DELETE', f'/interactions/{interaction}', 404)
    check('Foreign explicit owner listing', 'GET', f'/people?userProfileId={owner_a}', 404)
    check('Foreign explicit owner create', 'POST', '/people', 404, body={'userProfileId': owner_a, 'displayName': 'Must not exist', 'relationshipContext': 0})
    person_b = check('Create B participant', 'POST', '/people', 201, body={'userProfileId': owner_b, 'displayName': 'Synthetic B Person', 'relationshipContext': 0})['body']['id']
    check('Foreign participant rejected', 'POST', '/interactions', 404,
          body={'userProfileId': owner_b, 'title': 'Must not exist', 'description': 'Synthetic', 'occurredAt': occurred, 'participantIds': [person]})
    interaction_b = check('Create B interaction', 'POST', '/interactions', 201,
                          body={'userProfileId': owner_b, 'title': 'Synthetic B', 'description': 'Synthetic', 'occurredAt': occurred, 'participantIds': [person_b]})['body']['id']
    for method, path, body in [('PUT', f'/people/{person_b}/facts/{fact}', {'value': 'Must not save'}),
                               ('DELETE', f'/people/{person_b}/facts/{fact}', None),
                               ('PUT', f'/people/{person_b}/external-connections/{connections[1][1]}', external),
                               ('DELETE', f'/people/{person_b}/external-connections/{connections[1][1]}', None),
                               ('PUT', f"/interactions/{interaction_b}/attachments/{audio['id']}/transcript", {'correctedText': 'Must not save'}),
                               ('DELETE', f"/interactions/{interaction_b}/attachments/{audio['id']}", None)]:
        check('Foreign child under owned parent rejected', method, path, 404, body=body)
    select(owner_a)
    check('A-B-A does not revive stale version', 'GET', '/user-profile', 409, 'SelectionChanged', binding=binding_a)
    require(check('A Person survived foreign writes', 'GET', f'/people/{person}', 200)['body']['displayName'] == 'Synthetic Alex updated', 'Foreign write changed A')
    check('A identity still owned', 'GET', f'/user-profile/external-connections/{connections[0][1]}', 200)
    check('Delete confirmed fact', 'DELETE', f'/people/{person}/facts/{fact}', 200)
    require(check('Fact removed', 'GET', f'/people/{person}/facts', 200)['body']['facts'] == [], 'Fact not deleted')
    for path, child in connections:
        check('Delete external identity', 'DELETE', f'{path}/{child}', 204)
        check('Deleted identity unavailable', 'GET', f'{path}/{child}', 404)
    for path in avatars:
        check('Delete avatar', 'DELETE', path, 204)
        check('Deleted avatar unavailable', 'GET', path, 404)
    for child in [text['id'], audio['id']] + file_ids:
        check('Delete attachment', 'DELETE', f'{prefix}/{child}', 200)
        check('Deleted attachment unavailable', 'GET', f'{prefix}/{child}', 404)
    check('Deleted audio transcript unavailable', 'GET', audio_path + '/transcript', 404)
    check('Delete interaction', 'DELETE', f'/interactions/{interaction}', 200)
    check('Deleted interaction unavailable', 'GET', f'/interactions/{interaction}', 404)
    check('Delete Person', 'DELETE', f'/people/{person}', 200)
    check('Deleted Person unavailable', 'GET', f'/people/{person}', 404)


def main():
    global BASE_URL
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--provider', choices=['Sqlite', 'PostgreSql'], required=True)
    parser.add_argument('--host', choices=['Windows', 'Linux'], required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    arguments = parser.parse_args()
    parsed = urllib.parse.urlsplit(arguments.base_url)
    require(parsed.scheme == 'http' and parsed.hostname == '127.0.0.1' and parsed.port and
            parsed.path in ('', '/') and not parsed.query and not parsed.fragment and
            not parsed.username and not parsed.password, 'Only an owned IPv4 loopback base URL is permitted')
    BASE_URL = arguments.base_url.rstrip('/')
    success = False
    try:
        run_checks()
        success = True
    except Exception:
        print('Current functionality failed at: ' + CURRENT_CHECK + '; raw exception/response withheld', flush=True)
    result = {'status': 'PASS' if success else 'FAIL', 'provider': arguments.provider, 'host': arguments.host,
              'checks': CHECKS, 'lastCheck': CURRENT_CHECK,
              'limits': 'Not all-positive route coverage. OAuth/connected catalog/model/revocation/live analysis require separate manual Ai71; Linux OAuth needs an interactive browser in the backend loopback namespace. No native recognition; AudioB is separate. In-flight race/atomic Pending guarantees remain qualified automated-regression evidence.'}
    arguments.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(f"{arguments.host} {arguments.provider}: {result['status']}; {len(CHECKS)} recorded branch outcomes", flush=True)
    return 0 if success else 1


if __name__ == '__main__':
    raise SystemExit(main())
