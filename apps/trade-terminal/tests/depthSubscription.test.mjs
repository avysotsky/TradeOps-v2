import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const ui = readFileSync(new URL('../src/Terminal.tsx', import.meta.url), 'utf8');
const endpoints = readFileSync(new URL('../../TradeOps.TerminalApi/Market/MarketStreamEndpoints.cs', import.meta.url), 'utf8');
const hub = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitRawFeedHub.cs', import.meta.url), 'utf8');
const policy = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitBookSubscription.cs', import.meta.url), 'utf8');
const book = readFileSync(new URL('../../TradeOps.TerminalApi/Market/DeribitRawBook.cs', import.meta.url), 'utf8');

test('depth 10 is the initial selection and changes recreate local WebSocket', () => {
  assert.match(ui, /useState<DomDepth>\(10\)/);
  assert.match(ui, /'&depth=' \+ domDepth/);
  assert.match(ui, /\[key, status, marketInstrument, domDepth\]/);
  assert.match(ui, /socket\.onclose = null;[\s\S]*?socket\.close\(\)/);
  assert.match(ui, /String\(payload\.depth\) !== String\(domDepth\)/);
  assert.match(ui, /'L2 ' \+ domDepth \+ ' · 100MS'/);
});

test('backend only accepts actual Deribit 1/10/20 depth and explicit RAW mode', () => {
  assert.match(endpoints, /DeribitBookSubscription\.TryParseMode\(depthValue, out var depth\)/);
  assert.match(endpoints, /hub\.Subscribe\(symbol, depth\)/);
  assert.match(policy, /depth is RawMode or 1 or 10 or 20/);
  assert.match(policy, /book\.\{instrument\}\.none\.\{depth\}\.100ms/);
  assert.match(policy, /book\.\{instrument\}\.raw/);
  assert.match(policy, /value is "1" or "10" or "20"/);
  assert.match(policy, /value == "raw"/);
});

test('depth scopes single upstream feed and validates every book against its subscribed mode', () => {
  assert.ok(hub.includes('var key = instrument + "|" + depth;'));
  assert.match(hub, /new InstrumentFeed\(instrument, depth, key, configuration, Release\)/);
  assert.match(hub, /channels = new\[\] \{[\s\S]*?_bookChannel/);
  assert.match(hub, /if \(name == _bookChannel\)/);
  assert.match(hub, /_book\.ApplyGroupedSnapshot\(data, _depth, out view\)/);
  assert.match(hub, /_book\.Apply\(data, out view\)/);
  assert.match(hub, /requestedDepth = _depth/);
  assert.match(hub, /exchangeChannel = _bookChannel/);
  assert.match(book, /ApplyGroupedSnapshot\(JsonElement update, int depth, out BookView view\)/);
  assert.match(book, /Invalidate\(\);[\s\S]*?foreach \(var level in nextBids\)/);
});
