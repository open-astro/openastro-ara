"""Updater input, non-mutating plan and artifact selection regressions."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'update-observatory.sh'
CLIENT_SCRIPT = SCRIPT.with_name('update-ara-client.sh')
SBC_SCRIPT = SCRIPT.with_name('update-ara-sbc.sh')

class UpdaterTests(unittest.TestCase):
    def run_script(self, *args, **kwargs):
        return subprocess.run(['bash', str(SCRIPT), *args], capture_output=True, text=True, **kwargs)

    def test_plan_is_successful_without_tools_or_network(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, OPENASTRO_UPDATE_DIR=d + '/never-created',
                       DOTNET='/missing/dotnet', FLUTTER='/missing/flutter')
            result = self.run_script('--plan', env=env)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertFalse(Path(env['OPENASTRO_UPDATE_DIR']).exists())
            self.assertIn('astro@172.24.1.1', result.stdout)

    def test_offline_plan_is_successful_without_tools_or_network(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, DOTNET='/missing/dotnet', FLUTTER='/missing/flutter')
            result = self.run_script('--offline', '--plan', '--source-root', d, env=env)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('offline: 1', result.stdout)

    def test_local_plan_is_successful_without_tools_or_network(self):
        result = self.run_script('--local', '--plan', env=dict(
            os.environ, DOTNET='/missing/dotnet', FLUTTER='/missing/flutter'))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('local: 1', result.stdout)

    def test_offline_client_commands_do_not_recheck_pub_dev(self):
        source = SCRIPT.read_text()
        self.assertIn('"$flutter" analyze --no-pub', source)
        self.assertIn('"$flutter" test --no-pub', source)
        self.assertIn('"$flutter" build linux --release --no-pub', source)

    def test_split_wrappers_select_one_target_without_tools(self):
        for script, target in ((CLIENT_SCRIPT, 'client'), (SBC_SCRIPT, 'sbc')):
            with self.subTest(script=script.name):
                result = subprocess.run(['bash', str(script), '--plan'], capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn(f'Target: {target}', result.stdout)

    def test_rejects_bad_inputs_before_network(self):
        for args in [('--jobs', '0'), ('--host', 'host;touch /tmp/bad'),
                     ('--user', '-oProxyCommand=bad'), ('--ara-ref',), ('--unknown',)]:
            with self.subTest(args=args):
                self.assertNotEqual(self.run_script('--plan', *args).returncode, 0)

    def test_password_not_in_plan(self):
        result = self.run_script('--plan', env=dict(os.environ, OPENASTRO_PASSWORD='secret-marker'))
        self.assertNotIn('secret-marker', result.stdout + result.stderr)

    def test_quote_roundtrip(self):
        code = 'source "$1"; value=$(quote_command "a b" "x;echo bad" "$(literal)"); eval "set -- $value"; printf "%s\\n" "$@"'
        # Literal command-substitution characters are data supplied as arguments.
        code = code.replace('"$(literal)"', "'$(literal)'")
        result = subprocess.run(['bash', '-c', code, '_', str(SCRIPT)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ['a b', 'x;echo bad', '$(literal)'])

    def test_package_selection_uses_metadata_and_rejects_duplicates(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            def deb(name, package, arch='arm64'):
                tree = root / name
                (tree / 'DEBIAN').mkdir(parents=True)
                (tree / 'DEBIAN/control').write_text(f'Package: {package}\nVersion: 1.0\nArchitecture: {arch}\nMaintainer: Test <test@example.org>\nDescription: test\n')
                subprocess.run(['dpkg-deb', '--build', str(tree), str(root / (name + '.deb'))], check=True, capture_output=True)
            deb('misleading-name', 'alpacabridge')
            deb('other', 'openastro-guider')
            cmd = ['bash', '-c', 'source "$1"; package_file "$2" alpacabridge', '_', str(SCRIPT), d]
            result = subprocess.run(cmd, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('misleading-name.deb', result.stdout)
            deb('duplicate', 'alpacabridge')
            self.assertNotEqual(subprocess.run(cmd, capture_output=True).returncode, 0)

if __name__ == '__main__':
    unittest.main()
