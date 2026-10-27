#!/usr/bin/env python3
"""Fetch the small, pinned upstream source corpus used by readiness comparisons."""
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / '.code-arena/oss-comparison/2026-10-26-followthrough/upstream'
SOURCES = {
    'smhasher': ('aappleby/smhasher', '07bb4de10a63e8cc2e1724865454eba635742383',
                 ['src/MurmurHash3.cpp', 'src/MurmurHash3.h']),
    'faiss': ('facebookresearch/faiss', 'c919a604e621876c43f33c54dc87a6c2e6eccc97',
              ['LICENSE', 'tests/test_index.py']),
    'flink': ('apache/flink', 'dab40d31eba000411f919fe9b77afc6d77bfa1a7',
              ['LICENSE', 'flink-runtime/src/test/java/org/apache/flink/streaming/runtime/operators/windowing/TumblingEventTimeWindowsTest.java',
               'flink-runtime/src/test/java/org/apache/flink/streaming/runtime/operators/windowing/SlidingEventTimeWindowsTest.java']),
    'lucene': ('apache/lucene', 'a9779ef3c1939fcd8aaab1b9485f036311797a1d',
               ['LICENSE.txt', 'lucene/core/src/test/org/apache/lucene/search/TestPhraseQuery.java']),
}


def main():
    manifest = []
    for name, (repo, revision, paths) in SOURCES.items():
        for path in paths:
            url = f'https://raw.githubusercontent.com/{repo}/{revision}/{path}'
            data = urlopen(url, timeout=60).read()
            target = DEST / name / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            manifest.append(dict(repository=repo, revision=revision, path=path, url=url,
                                 sha256=hashlib.sha256(data).hexdigest()))
            print(name, path, len(data))
    (DEST / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    main()
