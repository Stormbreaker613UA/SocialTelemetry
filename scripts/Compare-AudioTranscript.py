"""Compare known synthetic references, ignoring case/punctuation (and Russian ё/е)."""
import argparse
import json
import pathlib
import re
import unicodedata


def words(text):
    normalized = unicodedata.normalize('NFKC', text).casefold().replace('ё', 'е')
    return re.findall(r'\w+', normalized)


def word_distance(reference, recognized):
    previous = list(range(len(recognized) + 1))
    for index, expected in enumerate(reference, start=1):
        current = [index]
        for column, actual in enumerate(recognized, start=1):
            current.append(min(previous[column] + 1, current[-1] + 1,
                               previous[column - 1] + (expected != actual)))
        previous = current
    return previous[-1]


parser = argparse.ArgumentParser()
parser.add_argument('result', type=pathlib.Path)
arguments = parser.parse_args()
result = json.loads(arguments.result.read_text(encoding='utf-8-sig'))
for sample in result['formats']:
    reference = words(sample['reference'])
    recognized = words(sample['recognized'])
    sample['wordErrors'] = word_distance(reference, recognized)
    sample['referenceWords'] = len(reference)
    sample['wordErrorRate'] = sample['wordErrors'] / len(reference) if reference else None
    print(json.dumps(sample, ensure_ascii=True))
result['normalization'] = 'NFKC, casefold, punctuation ignored, Russian yo/e normalized; ordinary word edit distance.'
arguments.result.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
