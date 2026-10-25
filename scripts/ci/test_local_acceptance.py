"""CI-01 orchestration unit checks only; these do NOT certify B1/F1/F2 builds."""
import tempfile
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch
import unittest
from local_acceptance import (Runner, SPECS, git_origin_is_github, parse_ls_remote,
                              parse_counts, StaleShaError)


class LocalAcceptanceTests(unittest.TestCase):
    def test_canonical_remote_only(self):
        self.assertTrue(git_origin_is_github('git@github.com:avysotsky/TradeOps-v2.git'))
        self.assertTrue(git_origin_is_github('https://github.com/avysotsky/TradeOps-v2'))
        self.assertFalse(git_origin_is_github('https://example.com/avysotsky/TradeOps-v2'))
        self.assertFalse(git_origin_is_github('https://user:token@github.com/avysotsky/TradeOps-v2'))

    def test_pr_head_must_match_source_branch(self):
        head = 'a' * 40
        refs = {'refs/pull/62/head', 'refs/heads/' + SPECS[62][0]}
        out = ''.join(head + '\t' + ref + '\n' for ref in sorted(refs))
        self.assertEqual(set(parse_ls_remote(out, refs).values()), {head})
        with self.assertRaises(StaleShaError):
            parse_ls_remote(out.replace(head, 'b'*40, 1), refs)
        with self.assertRaises(RuntimeError):
            parse_ls_remote(head + '\trefs/pull/62/head\n', refs)

    def test_windows_git_bridge_only_after_wsl_head_lookup_fails(self):
        with tempfile.TemporaryDirectory() as t:
            runner = Runner(Path(t), Path(t))
            runner.windows_git = lambda cmd: ["git.exe", "-C", "D:\\Projects\\TradeOps-v2", *cmd]
            refs = {"refs/pull/62/head", "refs/heads/" + SPECS[62][0]}
            content = "".join("a" * 40 + "\t" + ref + "\n" for ref in sorted(refs))
            wsl = CompletedProcess(["git"], 128, "", "fatal: could not read Username")
            win = CompletedProcess(["git.exe"], 0, content, "")
            with patch.object(runner, "exec", side_effect=[wsl, win]) as exec_mock:
                self.assertEqual(runner.remote_sha(62), "a" * 40)
                self.assertEqual(runner.git_transport[62], "windows-git")
                self.assertEqual(exec_mock.call_count, 2)

    def test_windows_git_failure_reports_both_errors_without_false_pass(self):
        with tempfile.TemporaryDirectory() as t:
            runner = Runner(Path(t), Path(t))
            runner.windows_git = lambda cmd: ["git.exe", *cmd]
            with patch.object(runner, "exec", side_effect=[
                CompletedProcess(["git"], 128, "", "wsl auth unavailable"),
                CompletedProcess(["git.exe"], 128, "", "windows auth unavailable")
            ]):
                with self.assertRaisesRegex(Exception, "Windows git.exe exit 128"):
                    runner.remote_sha(62)

    def test_trx_and_node_tap_counts(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)
            (p / 'trx').mkdir()
            (p / 'trx' / 'tests.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="12" passed="10" failed="1" notExecuted="1" /></ResultSummary></TestRun>')
            dotnet = parse_counts(p, 'backend')
            self.assertEqual((dotnet['total'], dotnet['passed'], dotnet['failed'], dotnet['skipped']), (12,10,1,1))
            (p / 'npm-test.log').write_text('TAP version 13\n# tests 4\n# pass 4\n# fail 0\n')
            (p / 'van-layout.log').write_text('# tests 3\n# pass 2\n# fail 1\n')
            node = parse_counts(p, 'deribit')
            self.assertEqual((node['total'], node['passed'], node['failed']), (7,6,1))

    def test_node_24_spec_reporter_and_ansi_counts(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)
            (p / "npm-test.log").write_text(
                "> terminal@1.0 test\n\x1b[32mℹ tests 13\x1b[0m\n"
                "ℹ pass 12\nℹ fail 0\nℹ skipped 1\n", encoding="utf-8")
            (p / "van-layout.log").write_text(
                "✔ VAN layout works (0.42ms)\nℹ tests 3\nℹ pass 3\n"
                "ℹ fail 0\nℹ skipped 0\n", encoding="utf-8")
            result = parse_counts(p, "deribit")
            self.assertTrue(result["available"])
            self.assertEqual(result["suites"], 2)
            self.assertEqual((result["total"], result["passed"], result["failed"], result["skipped"]), (16,15,0,1))
            self.assertEqual(len(result["per_log"]), 2)

    def test_missing_explicit_van_summary_does_not_count_as_complete(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)
            (p / "npm-test.log").write_text("# tests 7\n# pass 7\n# fail 0\n")
            (p / "van-layout.log").write_text("build done without a test summary")
            result = parse_counts(p, "deribit")
            self.assertFalse(result["available"])
            self.assertEqual(result["suites"], 1)

    def test_spec_reporter_without_test_count_does_not_invent_success(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)
            (p / "npm-test.log").write_text("ℹ pass 4\nℹ fail 0\n")
            result = parse_counts(p, "coincall")
            self.assertFalse(result["available"])
            self.assertEqual(result["total"], 0)

    def test_docker_containers_never_expose_ports_or_pass_host_env(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)
            r = Runner(p, p)
            args = r.container_cmd('dotnet', p / 'src', p / 'tmp', ['dotnet', 'test'])
            self.assertIn('--network', args)
            self.assertIn('none', args)
            self.assertIn('--cap-drop', args)
            self.assertNotIn('--privileged', args)
            self.assertNotIn('-p', args)
            self.assertNotIn('--env-file', args)
            self.assertIn('DeribitTerminal__MainnetExecutionEnabled=false', args)
            self.assertIn('Exchange__Provider=Mock', args)

    def test_failed_step_never_claims_pass(self):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t)
            r = Runner(p, p)
            data = {'steps': []}
            self.assertFalse(r.step(data, 'known failure', ['python3', '-c', 'raise SystemExit(3)'], p, 'bad.log'))
            self.assertEqual(data['steps'][0]['status'], 'FAIL')
            self.assertEqual(data['steps'][0]['exit_code'], 3)


if __name__ == '__main__':
    unittest.main(verbosity=2)
