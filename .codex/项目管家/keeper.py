"""Local, rebuildable source map; Python standard library only."""
import argparse
from contextlib import closing
import hashlib
import json
import sqlite3
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / 'artifacts/keeper'


def identity():
    git_root = Path(subprocess.check_output(
        ['git', '-C', str(ROOT), 'rev-parse', '--show-toplevel'], text=True).strip()).resolve()
    if git_root != ROOT or not (ROOT / 'ToDoList.sln').is_file():
        raise RuntimeError('Project identity mismatch')
    if not Path.cwd().resolve().is_relative_to(ROOT):
        raise RuntimeError('Run from this project or one of its subdirectories')
    return str(ROOT)


def snapshot():
    # Includes untracked sources, excludes ignored build output and user data.
    raw = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files',
                                   '-z', '--cached', '--others', '--exclude-standard'])
    result = {}
    for name in sorted(set(raw.decode('utf-8').split('\0')) - {''}):
        path = ROOT / name
        if any(p.lower() in {'build', 'artifacts', 'bin', 'obj', 'data', 'backup',
                              '__pycache__', '.git'} for p in Path(name).parts):
            continue
        if path.is_file() and not path.is_symlink():
            result[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def digest(files):
    return hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()


def refresh():
    CACHE.mkdir(parents=True, exist_ok=True)
    files = snapshot()
    with closing(sqlite3.connect(CACHE / 'index.sqlite')) as db, db:
        db.execute('CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY, hash TEXT)')
        db.execute('CREATE TABLE IF NOT EXISTS lines(path TEXT, line INTEGER, text TEXT)')
        old = dict(db.execute('SELECT path, hash FROM files'))
        for path in old.keys() - files.keys():
            db.execute('DELETE FROM files WHERE path=?', (path,))
            db.execute('DELETE FROM lines WHERE path=?', (path,))
        for path, sha in files.items():
            if old.get(path) == sha:
                continue
            db.execute('INSERT OR REPLACE INTO files VALUES (?, ?)', (path, sha))
            db.execute('DELETE FROM lines WHERE path=?', (path,))
            if not path.startswith('docs/licenses/') and Path(path).suffix.lower() in {'.cs', '.xaml', '.md', '.py', '.ps1', '.json', '.props', '.csproj', '.bat'}:
                try:
                    lines = (ROOT / path).read_text(encoding='utf-8-sig').splitlines()
                except UnicodeDecodeError:
                    continue
                db.executemany('INSERT INTO lines VALUES (?, ?, ?)',
                               [(path, n, line[:1500]) for n, line in enumerate(lines, 1) if line.strip()])
    return len(files)


def query(term, limit=3):
    refresh()
    with closing(sqlite3.connect(CACHE / 'index.sqlite')) as db:
        return db.execute('SELECT path, line, text FROM lines WHERE instr(lower(path), lower(?)) > 0 '
                          'OR instr(lower(text), lower(?)) > 0 '
                          "ORDER BY CASE WHEN path LIKE 'src/%' THEN 0 ELSE 1 END, path, line LIMIT ?",
                          (term, term, limit)).fetchall()


def check_docs():
    mapping = json.loads((ROOT / '.codex/doc-map.json').read_text(encoding='utf-8'))
    errors = []
    for item in mapping:
        path = ROOT / item['doc']
        if not path.is_file():
            errors.append(f'Missing document: {path}')
            continue
        body = path.read_text(encoding='utf-8')
        for section in ('职责', '入口', '契约', '验证'):
            if f'## {section}' not in body:
                errors.append(f'{item["doc"]}: missing section {section}')
        for entry in item['entries']:
            if not (ROOT / entry).is_file():
                errors.append(f'{item["doc"]}: missing entry {entry}')
    if errors:
        raise RuntimeError('\n'.join(errors))
    return mapping


def affected(previous, current):
    changed = {p for p in previous.keys() | current.keys() if previous.get(p) != current.get(p)}
    return [item['doc'] for item in check_docs()
            if any(p.startswith(prefix) for p in changed for prefix in item['code_paths'])]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['identity', 'refresh', 'query', 'check', 'affected'])
    parser.add_argument('keyword', nargs='?', default='')
    args = parser.parse_args()
    identity()
    if args.command == 'identity':
        print(identity())
    elif args.command == 'refresh':
        print(f'Indexed {refresh()} files')
    elif args.command == 'query':
        for path, line, text in query(args.keyword):
            print(f'{path}:{line}: {text.strip()}')
    elif args.command == 'check':
        check_docs()
        print('Document structure and entries passed')
    else:
        state = CACHE / 'build-state.json'
        previous = json.loads(state.read_text(encoding='utf-8')).get('files', {}) if state.exists() else {}
        print('\n'.join(affected(previous, snapshot())))


if __name__ == '__main__':
    main()
