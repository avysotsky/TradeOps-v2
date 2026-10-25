"""CI-01 orchestration unit checks only; these do NOT certify B1/F1/F2 builds."""
import tempfile
from pathlib import Path
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
