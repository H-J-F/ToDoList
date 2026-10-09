"""Run WPF exit checks in isolated data and verify actual process termination."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executable', type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/lifecycle'))
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    output = args.output.resolve() / uuid.uuid4().hex[:12]
    output.mkdir(parents=True)
    scenarios = [
        ('hidden', ['--ui-lifecycle', '--tray-native'], 'lifecycle.json'),
        ('visible', ['--ui-lifecycle', '--exit-visible', '--tray-native'], 'lifecycle.json'),
        ('reduced-hidden', ['--ui-lifecycle', '--tray-native', '--reduced-motion'], 'lifecycle.json'),
        ('reduced-visible', ['--ui-lifecycle', '--tray-native', '--reduced-motion', '--exit-visible'], 'lifecycle.json'),
        ('extra-hidden', ['--ui-lifecycle', '--exit-extra-window'], 'lifecycle.json'),
        ('extra-visible', ['--ui-lifecycle', '--exit-extra-window', '--exit-visible'], 'lifecycle.json'),
        ('revision', ['--ui-revision', '--tray-native'], 'revision.json'),
        ('draft-save', ['--ui-revision', '--exit-draft-save', '--tray-native'], 'draft-exit.json'),
        ('draft-discard', ['--ui-revision', '--exit-draft-discard', '--tray-native'], 'draft-exit.json'),
        ('restart', ['--ui-lifecycle', '--tray-native'], 'lifecycle.json'),
        ('settings', ['--ui-revision', '--settings-navigation', '--tray-native'], 'settings-navigation.json'),
        ('settings-restart', ['--ui-revision', '--settings-navigation', '--settings-restart'], 'settings-navigation.json'),
    ]
    results = []
    for name, flags, report_name in scenarios:
        directory = output / {'restart': 'hidden', 'settings-restart': 'settings'}.get(name, name)
        directory.mkdir(exist_ok=True)
        report_path = directory / report_name
        if name in ('restart', 'settings-restart'):
            report_path.unlink(missing_ok=True)
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        process = subprocess.Popen([str(executable), '--data-dir', str(directory / 'Data'), *flags], cwd=executable.parent, startupinfo=startup)
        deadline = time.monotonic() + 120
        approved = None
        report = None
        while process.poll() is None and time.monotonic() < deadline:
            try:
                report = json.loads(report_path.read_text(encoding='utf-8-sig'))
                if report.get('Completed', report_name == 'draft-exit.json'):
                    approved = time.monotonic()
                    break
            except (OSError, ValueError):
                pass
            time.sleep(0.05)
        try:
            code = process.wait(timeout=5)
            report = json.loads(report_path.read_text(encoding='utf-8-sig'))
            passed = code == 0 and report.get('Passed') is True and report.get('Completed', True)
            # Retain each result even when the same data directory is reused.
            archived = output / (name + '.json')
            archived.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
            result = dict(Scenario=name, Passed=passed, ExitCode=code,
                          ExitWaitSeconds=round(time.monotonic() - approved, 3) if approved else None,
                          Report=str(archived), Checks=report.get('Checks'), Failures=report.get('Failures'))
        except (subprocess.TimeoutExpired, OSError, ValueError) as error:
            # Never kill an application to make an exit regression appear successful.
            result = dict(Scenario=name, Passed=False, ProcessId=process.pid, Error=str(error))
        results.append(result)
        print(json.dumps({k: v for k, v in result.items() if k != 'Checks'}, ensure_ascii=False), flush=True)
        if process.poll() is None:
            break
        # Let Explorer remove the previous icon/overflow popup before the next
        # process performs real shell input against its own notification icon.
        time.sleep(1)
    summary = dict(Passed=len(results) == len(scenarios) and all(r['Passed'] for r in results),
                   Executable=str(executable), Results=results)
    (output / 'process-exit.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Report: {output / "process-exit.json"}')
    return 0 if summary['Passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
