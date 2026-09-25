"""Version, validate and publish a completed change. Called by Build.bat and Stop."""
import argparse
from contextlib import contextmanager
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('keeper', ROOT / '.codex/项目管家/keeper.py')
keeper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(keeper)
STATE = keeper.CACHE / 'build-state.json'


def save_state(state):
    temp = STATE.with_suffix('.tmp')
    temp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding='utf-8')
    temp.replace(STATE)


def next_version(current, explicit=None, pending=None):
    version = explicit or pending or '.'.join([*current.split('.')[:2], str(int(current.split('.')[2]) + 1)])
    if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', version):
        raise ValueError('Version must be major.minor.patch, for example 2.4.0')
    if any(int(part) > 65534 for part in version.split('.')):
        raise ValueError('Version components must be <= 65534')
    return version


@contextmanager
def build_lock():
    import msvcrt
    keeper.CACHE.mkdir(parents=True, exist_ok=True)
    with (keeper.CACHE / 'build.lock').open('a+b') as lock:
        if lock.tell() == 0:
            lock.write(b'0')
            lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError('Another build is running; wait and retry') from error
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def run(*args):
    print('>', *args, flush=True)
    subprocess.run(args, cwd=ROOT, check=True)


def outputs_present(state):
    output = Path(state.get('output', ''))
    version = state.get('version', '')
    return all((output / f'ToDoList-{version}-win-x64-{kind}/ToDoList.exe').is_file()
               for kind in ('lite', 'portable'))


def build(explicit=None, force=False):
    keeper.identity()
    with build_lock():
        state = json.loads(STATE.read_text(encoding='utf-8')) if STATE.exists() else {}
        files = keeper.snapshot()
        if (not force and not state.get('pending') and state.get('files') == files
                and (explicit is None or explicit == state.get('version')) and outputs_present(state)):
            print(f'No changes; keeping {state["version"]}: {state["output"]}')
            return
        mapping = keeper.check_docs()
        print('Review affected documents:', ', '.join(keeper.affected(state.get('files', {}), files)), flush=True)
        props = ROOT / 'Directory.Build.props'
        current = ET.parse(props).findtext('./PropertyGroup/Version')
        # Recover the same version after failure, or restore missing output without bumping.
        pending = state.get('pending')
        if state.get('files') == files:
            pending = state.get('version', pending)
        version = next_version(current, explicit, pending)
        state['pending'] = version
        save_state(state)
        body = props.read_text(encoding='utf-8-sig')
        props.write_text(re.sub(r'<Version>[^<]+</Version>', f'<Version>{version}</Version>', body), encoding='utf-8')
        readme = ROOT / 'README.md'
        body = readme.read_text(encoding='utf-8-sig')
        body = re.sub(r'当前源码版本为 \d+\.\d+\.\d+', f'当前源码版本为 {version}', body)
        body = re.sub(r'Build/ToDoList-\d+\.\d+\.\d+-win-x64-', f'Build/ToDoList-{version}-win-x64-', body)
        readme.write_text(body, encoding='utf-8')
        before = keeper.snapshot()
        output = ROOT / 'Build'
        if any((output / f'ToDoList-{version}-win-x64-{kind}').exists() for kind in ('lite', 'portable')):
            output = output / f'rebuild-{version}-{uuid.uuid4().hex[:8]}'
        run(sys.executable, '-X', 'utf8', '-m', 'unittest', 'discover', '-s', 'tools/tests', '-p', 'test_*.py')
        artifacts = str(ROOT / 'artifacts/build' / uuid.uuid4().hex[:12])
        run('dotnet', 'build', 'ToDoList.sln', '-c', 'Release', '-p:RestoreLockedMode=true',
            '--artifacts-path', artifacts)
        run('dotnet', 'test', 'tests/ToDoList.Tests', '-c', 'Release', '--no-build', '--no-restore',
            '--artifacts-path', artifacts)
        run('powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            str(ROOT / 'tools/Publish.ps1'), '-Variant', 'Both', '-OutputRoot', str(output),
            '-BuildArtifactsPath', artifacts)
        run(sys.executable, '-X', 'utf8', str(ROOT / 'tools/Verify-Build.py'), str(output),
            '--allow-dirty', f'--version={version}')
        after = keeper.snapshot()
        if before != after:
            raise RuntimeError('Sources changed during build; run Build.bat again before delivery')
        keeper.refresh()
        result = {'version': version, 'output': str(output), 'files': after,
                  'source_digest': keeper.digest(after), 'documents': [m['doc'] for m in mapping]}
        # Only successful, verified outputs become the latest build.
        (ROOT / 'Build/latest.json').write_text(json.dumps(
            {k: v for k, v in result.items() if k != 'files'}, ensure_ascii=False, indent=2), encoding='utf-8')
        save_state(result)
        print(f'Published {version}: {output}', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', help='Use the version explicitly requested by the user')
    parser.add_argument('--force', action='store_true', help='Rebuild unchanged sources at the same version')
    args = parser.parse_args()
    os.chdir(ROOT)
    try:
        build(args.version, args.force)
    except Exception as error:
        print(f'Build failed: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
