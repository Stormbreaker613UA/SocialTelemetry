"""Generate disposable local-language fixtures; not a product TTS dependency."""
import argparse
import hashlib
import json
import pathlib
import urllib.request
import wave

REVISION = 'c10ece1aade47bb51c153c893d14e5bf8e5b7117'
SAMPLES = [
    ('ru', 'ru_RU-denis-medium', 'В субботу мы с друзьями посмотрели фильм. После этого мы обсудили свои впечатления и договорились встретиться на следующей неделе. Это короткая проверка распознавания русской речи.'),
    ('uk', 'uk_UA-ukrainian_tts-medium', 'У суботу ми з друзями подивилися фільм. Потім ми обговорили свої враження та домовилися зустрітися наступного тижня. Це коротка перевірка розпізнавання українського мовлення.'),
    ('de', 'de_DE-thorsten-medium', 'Am Samstag haben wir mit Freunden einen Film gesehen. Danach haben wir über unsere Eindrücke gesprochen und ein Treffen für die nächste Woche vereinbart. Das ist ein kurzer Test der deutschen Spracherkennung.'),
    ('pl', 'pl_PL-gosia-medium', 'W sobotę obejrzeliśmy film z przyjaciółmi. Potem omówiliśmy nasze wrażenia i umówiliśmy się na spotkanie w przyszłym tygodniu. To jest krótki test rozpoznawania polskiej mowy.'),
    ('fr', 'fr_FR-siwis-medium', 'Samedi, nous avons regardé un film avec des amis. Ensuite, nous avons parlé de nos impressions et prévu de nous revoir la semaine prochaine. Ceci est un court test de reconnaissance de la parole en français.'),
    ('es', 'es_ES-davefx-medium', 'El sábado vimos una película con amigos. Después hablamos de nuestras impresiones y quedamos para vernos la próxima semana. Esta es una breve prueba de reconocimiento de voz en español.'),
]


def read_json(url):
    with urllib.request.urlopen(url, timeout=60) as response:
        return json.load(response)


def download_file(root, remote, expected_size, expected_hash):
    destination = root / pathlib.PurePosixPath(remote).name
    if not destination.exists():
        url = f'https://huggingface.co/rhasspy/piper-voices/resolve/{REVISION}/{remote}'
        partial = destination.with_suffix(destination.suffix + '.download')
        offset = partial.stat().st_size if partial.exists() else 0
        with partial.open('ab') as output:
            while offset < expected_size:
                end = min(offset + 8 * 1024 * 1024, expected_size) - 1
                request = urllib.request.Request(url, headers={'Range': f'bytes={offset}-{end}'})
                with urllib.request.urlopen(request, timeout=90) as response:
                    if response.status == 206:
                        if response.headers.get('Content-Range') != f'bytes {offset}-{end}/{expected_size}':
                            raise RuntimeError('Unexpected model download range')
                        data = response.read(end - offset + 2)
                        if len(data) != end - offset + 1:
                            raise RuntimeError('Incomplete model download')
                    elif response.status == 200 and offset == 0:
                        data = response.read(expected_size + 1)
                        if len(data) != expected_size:
                            raise RuntimeError('Unexpected model download size')
                    else:
                        raise RuntimeError('Cannot resume model download')
                    output.write(data)
                    offset += len(data)
        partial.rename(destination)
    with destination.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest() if hasattr(hashlib, 'file_digest') else hashlib.sha256(stream.read()).hexdigest()
    if expected_hash and digest != expected_hash:
        raise RuntimeError('Voice model SHA256 mismatch')
    return destination, digest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--directory', type=pathlib.Path, required=True)
    arguments = parser.parse_args()
    root = arguments.directory.resolve()
    root.mkdir(parents=True, exist_ok=True)
    catalog = read_json(f'https://huggingface.co/rhasspy/piper-voices/resolve/{REVISION}/voices.json')
    from piper import PiperVoice
    manifest = []
    for language, key, reference in SAMPLES:
        model_path = next(path for path in catalog[key]['files'] if path.endswith('.onnx'))
        parent = str(pathlib.PurePosixPath(model_path).parent)
        metadata = read_json(f'https://huggingface.co/api/models/rhasspy/piper-voices/tree/{REVISION}/{parent}?expand=true')
        resolved = {}
        hashes = {}
        for path in (model_path, model_path + '.json'):
            item = next(entry for entry in metadata if entry['path'] == path)
            expected = item.get('lfs', {}).get('oid')
            resolved[path], hashes[path] = download_file(root, path, item['size'], expected)
        voice = PiperVoice.load(str(resolved[model_path]))
        audio_path = root / (language + '.wav')
        with wave.open(str(audio_path), 'wb') as output:
            # The Ukrainian character-based voice omits uppercase letters from its ID map.
            spoken = reference.lower() if language == 'uk' else reference
            voice.synthesize_wav(spoken, output)
        manifest.append({'language': language, 'voice': key, 'file': str(audio_path), 'reference': reference, 'voiceRevision': REVISION, 'sha256': hashes})
        print(f'{language}: voice verified; synthetic WAV generated', flush=True)
    (root / 'fixtures.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
