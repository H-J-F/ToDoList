import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

PROJECT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


build = load('project_build', PROJECT / 'tools/Build.py')
hook = load('stop_hook', PROJECT / '.codex/hooks/stop.py')


class BuildFlowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        subprocess.run(['git', 'init', '-q', str(self.root)], check=True)
        (self.root / 'ToDoList.sln').write_text('fixture', encoding='utf-8')
        (self.root / 'Directory.Build.props').write_text(
            '<Project><PropertyGroup><Version>2.3.1</Version></PropertyGroup></Project>', encoding='utf-8')
        (self.root / 'README.md').write_text('当前源码版本为 2.3.1', encoding='utf-8')
        (self.root / '.codex').mkdir()
        (self.root / '.codex/doc-map.json').write_text('[]', encoding='utf-8')
        (self.root / '.gitignore').write_text('Build/\nartifacts/\n', encoding='utf-8')
        self.calls = []
        for obj, name, value in ((build, 'ROOT', self.root), (build.keeper, 'ROOT', self.root),
                                 (build.keeper, 'CACHE', self.root / 'artifacts/keeper'),
                                 (build, 'STATE', self.root / 'artifacts/keeper/build-state.json')):
            patcher = patch.object(obj, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        for patcher in (patch.object(build.keeper, 'identity', return_value=str(self.root)),
                        patch.object(build, 'run', side_effect=self.fake_run),
                        patch('sys.stdout', new_callable=io.StringIO)):
            patcher.start()
            self.addCleanup(patcher.stop)

    def fake_run(self, *args):
        self.calls.append(args)
        if '-OutputRoot' in args:
            output = Path(args[args.index('-OutputRoot') + 1])
            version = build.ET.parse(self.root / 'Directory.Build.props').findtext('./PropertyGroup/Version')
            for kind in ('lite', 'portable'):
                folder = output / f'ToDoList-{version}-win-x64-{kind}'
                folder.mkdir(parents=True)
                (folder / 'ToDoList.exe').write_bytes(b'fixture')

    def state(self):
        return json.loads(build.STATE.read_text(encoding='utf-8'))

    def test_change_bumps_once_and_read_only_repeated_build_skips(self):
        build.build()
        self.assertEqual(self.state()['version'], '2.3.2')
        count = len(self.calls)
        build.build()
        self.assertEqual(len(self.calls), count)
        (self.root / 'new.cs').write_text('class Added {}', encoding='utf-8')
        build.build()
        self.assertEqual(self.state()['version'], '2.3.3')
        (self.root / 'new.cs').unlink()
        build.build()
        self.assertEqual(self.state()['version'], '2.3.4')

    def test_failed_build_retries_same_version(self):
        with patch.object(build, 'run', side_effect=RuntimeError('test failure')):
            with self.assertRaisesRegex(RuntimeError, 'test failure'):
                build.build()
        self.assertEqual(self.state()['pending'], '2.3.2')
        self.assertFalse((self.root / 'Build/latest.json').exists())
        (self.root / 'fix.cs').write_text('class Fix {}', encoding='utf-8')
        build.build()
        self.assertEqual(self.state()['version'], '2.3.2')
        self.assertNotIn('pending', self.state())

    def test_explicit_version_and_force_preserve_existing_data(self):
        build.build('4.0.7')
        original = Path(self.state()['output']) / 'ToDoList-4.0.7-win-x64-lite'
        (original / 'Data').mkdir()
        data = original / 'Data/user.db'
        data.write_bytes(b'keep me')
        build.build(force=True)
        self.assertEqual(self.state()['version'], '4.0.7')
        self.assertNotEqual(self.state()['output'], str(self.root / 'Build'))
        self.assertEqual(data.read_bytes(), b'keep me')

    def test_modification_during_build_does_not_mark_success(self):
        def modify(*args):
            self.fake_run(*args)
            (self.root / 'racing.cs').write_text('changed during build', encoding='utf-8')
        with patch.object(build, 'run', side_effect=modify):
            with self.assertRaisesRegex(RuntimeError, 'Sources changed'):
                build.build()
        self.assertFalse((self.root / 'Build/latest.json').exists())

    def test_missing_outputs_rebuild_same_version(self):
        build.build()
        (self.root / 'Build/ToDoList-2.3.2-win-x64-lite/ToDoList.exe').unlink()
        build.build()
        self.assertEqual(self.state()['version'], '2.3.2')
        self.assertTrue(build.outputs_present(self.state()))

    def test_broken_document_entry_prevents_version_change(self):
        doc = self.root / 'doc.md'
        doc.write_text('## 职责\n## 入口\n## 契约\n## 验证\n', encoding='utf-8')
        (self.root / '.codex/doc-map.json').write_text(json.dumps([
            {'doc': 'doc.md', 'code_paths': ['src/'], 'entries': ['missing.cs']}]), encoding='utf-8')
        with self.assertRaisesRegex(RuntimeError, 'missing entry'):
            build.build()
        self.assertIn('2.3.1', (self.root / 'Directory.Build.props').read_text())

    def test_index_removes_deleted_source_and_ignores_data(self):
        source = self.root / 'unique.cs'
        source.write_text('class UniqueKeeperNeedle {}', encoding='utf-8')
        data = self.root / 'Data'
        data.mkdir()
        (data / 'secret.cs').write_text('UniqueKeeperNeedle', encoding='utf-8')
        self.assertEqual(len(build.keeper.query('UniqueKeeperNeedle')), 1)
        source.unlink()
        self.assertEqual(build.keeper.query('UniqueKeeperNeedle'), [])

    def test_invalid_explicit_version(self):
        for value in ('2.3', '2.03.1', '1.2.65535', '../bad'):
            with self.assertRaises(ValueError):
                build.next_version('2.3.1', value)


class StopTests(unittest.TestCase):
    def test_json_success_failure_and_recursion_guard(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            for code, active in ((0, False), (1, False), (1, True)):
                payload = json.dumps({'cwd': str(root), 'stop_hook_active': active})
                output = io.StringIO()
                with patch.object(hook, 'ROOT', root), patch('sys.stdin', io.StringIO(payload)), \
                        contextlib.redirect_stdout(output), patch.object(hook.subprocess, 'run',
                        return_value=subprocess.CompletedProcess([], code)):
                    hook.main()
                result = json.loads(output.getvalue())
                if code == 0:
                    self.assertEqual(result, {})
                elif active:
                    self.assertIn('systemMessage', result)
                else:
                    self.assertEqual(result['decision'], 'block')


if __name__ == '__main__':
    unittest.main()
