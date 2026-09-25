"""Stop output must be JSON; all build output is retained in the local log."""
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


def main():
    payload = json.load(sys.stdin)
    cwd = Path(payload.get('cwd', Path.cwd())).resolve()
    if not cwd.is_relative_to(ROOT):
        raise RuntimeError('Hook cwd is outside this project')
    cache = ROOT / 'artifacts/keeper'
    cache.mkdir(parents=True, exist_ok=True)
    log = cache / 'stop-build.log'
    with log.open('w', encoding='utf-8') as stream:
        completed = subprocess.run([sys.executable, '-X', 'utf8', str(ROOT / 'tools/Build.py')],
                                   cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
    if completed.returncode:
        reason = f'Build/document closure failed. Read {log}, fix the failure and execute Build.bat.'
        if payload.get('stop_hook_active'):
            # Do not create an endless continuation loop on environmental failures.
            print(json.dumps({'systemMessage': reason}))
        else:
            print(json.dumps({'decision': 'block', 'reason': reason}))
    else:
        print('{}')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps({'decision': 'block', 'reason': str(error)}))
