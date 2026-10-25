import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const backend = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitClockProbe.cs', import.meta.url), 'utf8');
const endpoints = readFileSync(new URL('../../TradeOps.TerminalApi/Market/MarketStreamEndpoints.cs', import.meta.url), 'utf8');
const launcher = readFileSync(new URL('../../../scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1', import.meta.url), 'utf8');

test('clock probe uses independent allowlisted public Deribit GET, not RAW stream or private credentials', () => {
  assert.match(backend, /public\/get_time/);
  assert.match(backend, /httpClientFactory.CreateClient\("deribit-terminal"\)/);
  assert.match(backend, /"mainnet" => "www.deribit.com"/);
  assert.match(backend, /"testnet" => "test.deribit.com"/);
  assert.match(backend, /RefreshInterval = TimeSpan.FromSeconds\(30\)/);
  assert.match(backend, /ClockOffsetMs, long RttMs, long SampleUnixMs/);
  assert.doesNotMatch(backend, /ClientSecret|ClientId|public\/auth|private\//);
  assert.match(endpoints, /market\/clock/);
  assert.match(endpoints, /if \(!Authorized\(ctx, cfg\)\)/);
  assert.match(endpoints, /clockOffsetMs = reading.ClockOffsetMs/);
  assert.match(endpoints, /rttMs = reading.RttMs/);
});

test('estimated correction only appears inside collapsed diagnostics with RTT and disclaimer', () => {
  const panel = ui.slice(ui.indexOf('<details className="terminal-diagnostics"'));
  assert.match(ui, /api<ExchangeClockSample>\(key, '\/market\/clock'\)/);
  assert.match(ui, /window.setInterval\(\(\) => \{ void sample\(\); \}, 30000\)/);
  assert.match(ui, /exchangeAgeMs - exchangeClock.clockOffsetMs/);
  assert.match(ui, /streamState === 'synced'/);
  assert.match(panel, /Windows − Deribit/);
  assert.match(panel, /RTT public\/get_time/);
  assert.match(panel, /Δ без смещения часов/);
  assert.match(panel, /это не точная/);
  const header = ui.slice(ui.indexOf('<div className="dom-depth-toolbar">'),
    ui.indexOf('<div className="dom-own-status"'));
  assert.doesNotMatch(header, /clockOffsetMs|adjustedDeltaMs|rttMs/);
});

test('same pinned READ-ONLY launcher keeps execution hard-disabled', () => {
  assert.match(launcher, /to-v2-51-deribit-clock-skew-diagnostics/);
  assert.match(launcher, /DeribitTerminal__ExecutionEnabled = 'false'/);
  assert.match(launcher, /DeribitTerminal__MainnetExecutionEnabled = 'false'/);
});
