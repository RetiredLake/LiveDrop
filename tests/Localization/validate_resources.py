"""Validate the source resource contract before packaging or accepting translations."""
from pathlib import Path
import json
import re
import string
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2] / 'src' / 'LiveDrop'

def read_resources(path):
    result = {}
    folded = set()
    for item in ET.parse(path).getroot().findall('data'):
        key = item.attrib['name']
        assert key.casefold() not in folded, f'Duplicate resource: {path}: {key}'
        folded.add(key.casefold())
        value = item.findtext('value')
        assert value and item.findtext('comment'), f'Missing value/comment: {key}'
        result[key] = value
    return result

def placeholders(value):
    return sorted((name, spec) for _, name, spec, _ in string.Formatter().parse(value) if name is not None)

english = read_resources(root / 'Strings/en-US/Resources.resw')
references = set()
for path in root.rglob('*.cs'):
    if {'bin', 'obj', 'BundleArtifacts'} & set(path.parts):
        continue
    source = '\n'.join(line for line in path.read_text(encoding='utf-8-sig').splitlines() if not line.lstrip().startswith('//'))
    references.update(re.findall(r'AppText\.(?:Get|Format)\("([^"]+)"', source))
assert references <= english.keys(), f'Missing resources: {references - english.keys()}'
for path in root.glob('*.xaml'):
    for node in ET.parse(path).getroot().iter():
        uid = node.get('{http://schemas.microsoft.com/winfx/2006/xaml}Uid')
        if uid:
            assert any(key.startswith(uid + '.') for key in english), f'Unresolved x:Uid: {uid}'
        for name in ('Text', 'Content', 'PlaceholderText'):
            value = node.get(name, '')
            assert not value or value.startswith('{'), f'Hardcoded XAML text: {path}: {value}'
for path in root.glob('Package*.appxmanifest'):
    for match in re.findall(r'ms-resource:([A-Za-z0-9]+)', path.read_text()):
        assert match in english, f'Missing manifest string: {match}'
assert english['SelectFilesButton.Content'] == 'Select Files'
assert placeholders(english['DevelopedBy']) == [('0', '')]
coverage = json.loads((Path(__file__).with_name('translation_coverage.json')).read_text())
expected = set(coverage['translated_keys'])
fallback = set(coverage['english_fallback_keys'])
assert not expected & fallback and expected | fallback == english.keys(), 'Coverage contract differs from English source'
languages = {p.parent.name for p in (root / 'Strings').glob('*/Resources.resw')} - {'en-US'}
assert languages == set(coverage['languages']), 'Translation language set differs from coverage contract'
for path in (root / 'Strings').glob('*/Resources.resw'):
    translated = read_resources(path)
    required = english.keys() if path.parent.name == 'en-US' else expected
    assert translated.keys() == required, f'Resource keys differ: {path}'
    for key, value in translated.items():
        assert placeholders(value) == placeholders(english[key]), f'Placeholder mismatch: {path}: {key}'
print(f'PASS: {len(english)} resources; code, XAML, manifest references and translation placeholders')

