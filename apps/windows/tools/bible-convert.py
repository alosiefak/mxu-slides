"""Converts eBible.org VPL zips (verse per line: 'GEN 1:1 text') into the app's compact form:
one gzip'd TSV per translation (book code, chapter, verse, text) plus a manifest with the source.

Usage: python -I bible_convert.py <download-dir> <output-dir>
"""
import gzip
import hashlib
import io
import json
import os
import re
import sys
import zipfile

downloads, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)

sources = [
    # (file stem in the zip, our id, name, abbreviation, notes, clean KJV marks)
    ('eng-kjv2006', 'kjv', 'King James Version', 'KJV',
     'Public domain (outside the UK, where printing the KJV needs permission under Crown letters patent). '
     'Text from eBible.org (eng-kjv2006); the translators\' added words are shown without their [brackets], and paragraph marks are removed.', True),
    ('engwebp', 'web', 'World English Bible', 'WEB',
     'Public domain. "World English Bible" is a trademark of eBible.org; the text is unchanged. Text from eBible.org (engwebp).', False),
    ('eng-asv', 'asv', 'American Standard Version (1901)', 'ASV',
     'Public domain. Text from eBible.org (eng-asv).', False),
]

line_pattern = re.compile(r'^([1-4A-Z]{3}) (\d+):(\d+) (.*)$')
manifest = []
for stem, ident, name, abbreviation, notes, clean in sources:
    zip_path = os.path.join(downloads, stem + '_vpl.zip')
    raw = open(zip_path, 'rb').read()
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        text = archive.read(stem + '_vpl.txt').decode('utf-8-sig')
    rows, books, skipped = [], [], 0
    for line in text.splitlines():
        match = line_pattern.match(line.strip())
        if not match:
            skipped += 1
            continue
        book, chapter, verse, words = match.groups()
        if clean:
            words = words.replace('[', '').replace(']', '').replace('¶', '')
        words = re.sub(r'\s+', ' ', words).strip()
        if not words:
            continue
        if book not in books:
            books.append(book)
        rows.append(f'{book}\t{chapter}\t{verse}\t{words}')
    data = ('\n'.join(rows) + '\n').encode('utf-8')
    target = os.path.join(out, ident + '.tsv.gz')
    with open(target, 'wb') as f:
        # mtime=0 keeps the file identical on every run, so git sees no change when nothing changed.
        with gzip.GzipFile(fileobj=f, mode='wb', compresslevel=9, mtime=0) as z:
            z.write(data)
    manifest.append({
        'id': ident, 'name': name, 'abbreviation': abbreviation, 'notes': notes,
        'source': f'https://ebible.org/Scriptures/{stem}_vpl.zip',
        'sourceSha256': hashlib.sha256(raw).hexdigest(),
        'verses': len(rows), 'books': books, 'file': ident + '.tsv.gz',
    })
    print(f'{ident}: {len(rows)} verses, {len(books)} books, {skipped} lines skipped, {os.path.getsize(target) / 1e6:.2f} MB')

with open(os.path.join(out, 'bibles.json'), 'w', encoding='utf-8', newline='\n') as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write('\n')
print('books (kjv):', ' '.join(manifest[0]['books']))
