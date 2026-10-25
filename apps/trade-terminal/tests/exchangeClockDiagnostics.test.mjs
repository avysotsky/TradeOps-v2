import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const backend = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitClockProbe.cs', import.meta.url), 'utf8');
const endpoints = readFileSync(new URL('../../TradeOps.TerminalApi/Market/MarketStreamEndpoints.cs', import.meta.url), 'utf8');
const launcher = readFileSync(new URL('../../../scripts/deribit/Start-MainnetReactTerminalReadOnly.ps1', import.meta.url), 'utf8');

test('public Deribit time probe is strictly operator-initiated, no cache or background traffic', () => {
  assert.match(backend, /public\/get_time/);
  assert.match(backend, /httpClientFactory.CreateClient\("deribit-terminal"\)/);
  assert.match(backend, /"mainnet" => "www.deribit.com"/);
  assert.match(backend, /"testnet" => "test.deribit.com"/);
  assert.match(backend, /ClockOffsetMs, long RttMs, long SampleUnixMs/);
  assert.doesNotMatch(backend, /RefreshInterval|FailureRetryDelay|_nextRequestUtc|_latest|Task.Delay/);
  assert.doesNotMatch(backend, /ClientSecret|ClientId|public\/auth|private\//);
  assert.match(endpoints, /market\/clock/);
  assert.match(endpoints, /if \(!Authorized\(ctx, cfg\)\)/);
  assert.match(endpoints, /clockOffsetMs = reading.ClockOffsetMs/);
  assert.match(endpoints, /rttMs = reading.RttMs/);
});

test('manual check button is the only trigger and measures a fixed RAW delta snapshot', () => {
  const checkFn = ui.slice(ui.indexOf('const probeDeribitClock = async'),
    ui.indexOf('// A fixed one-shot sample'));
  const panel = ui.slice(ui.indexOf('<details className="terminal-diagnostics"'));
  assert.ok(checkFn.length > 200);
  assert.match(checkFn, /api<ExchangeClockSample>\(key, '\/market\/clock'\)/);
  assert.match(checkFn, /rawAtClick = streamState === 'synced' \? exchangeAgeMs : null/);
  assert.match(checkFn, /setExchangeClock\(\{ \.\.\.result, rawDeltaAtCheckMs: rawAtClick \}\)/);
  assert.doesNotMatch(checkFn, /setInterval|useEffect|setTimeout/);
  assert.match(panel, /onClick=\{\(\) => \{ void probeDeribitClock\(\); \}\}/);
  assert.match(panel, /disabled=\{clockProbePending \|\| !status\}/);
  assert.match(ui, /exchangeClock.rawDeltaAtCheckMs - exchangeClock.clockOffsetMs/);
  assert.match(panel, /Windows − Deribit/);
  assert.match(panel, /RTT public\/get_time/);
  assert.match(panel, /Δ при запуске проверки/);
  assert.match(panel, /Δ без смещения часов/);
  assert.match(panel, /не обновляются автоматически/);
  assert.match(panel, /это не точная/);
  const toolbar = ui.slice(ui.indexOf('<div className="dom-depth-toolbar">'),
    ui.indexOf('<div className="dom-own-status"'));
  assert.doesNotMatch(toolbar, /clockOffsetMs|adjustedDeltaMs|rttMs|probeDeribitClock/);
});

test('same pinned READ-ONLY launcher keeps execution hard-disabled', () => {
  assert.match(launcher, /to-v2-51-deribit-clock-skew-diagnostics/);
  assert.match(launcher, /DeribitTerminal__ExecutionEnabled = 'false'/);
  assert.match(launcher, /DeribitTerminal__MainnetExecutionEnabled = 'false'/);
});
