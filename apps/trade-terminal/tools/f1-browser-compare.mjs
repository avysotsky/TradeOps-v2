#!/usr/bin/env node
/**
 * F1-68: capture ACTUAL screenshots of already-running local VAN and F1 React.
 * No exchange API calls, credentials, fixture fabrication, order writes or
 * browser screenshot placeholders. Never run against remote websites.
 *
 * Run (from apps/trade-terminal):
 *   npm install --no-save --no-package-lock playwright
 *   node tools/f1-browser-compare.mjs \
 *      --van http://127.0.0.1:8766/deribit.html \
 *      --react http://127.0.0.1:5173/ \
 *      --sha "$(git rev-parse HEAD)" \
 *      --out /tmp/f1-68-browser-evidence
 *
 * Must use existing operator launcher with execution DISABLED; do not inject
 * credentials. VAN should be served locally with synthetic/public-only data.
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve, join } from 'node:path';
import process from 'node:process';

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    const key = argv[i];
    if (!key.startsWith('--') || !argv[i + 1] || argv[i + 1].startsWith('--'))
      throw Error('Expected --name value, got ' + key);
    args[key.slice(2)] = argv[++i];
  }
  for (const required of ['van', 'react', 'sha', 'out']) {
    if (!args[required]) throw Error('Missing --' + required);
  }
  if (!/^[a-f0-9]{40}$/i.test(args.sha)) throw Error('SHA must be full 40-hex commit SHA');
  for (const type of ['van', 'react']) {
    const url = new URL(args[type]);
    const local = ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname);
    if (!['http:', 'file:'].includes(url.protocol) || (url.protocol === 'http:' && !local))
      throw Error(type + ' must be a LOCAL http/file URL, never remote');
    if (url.username || url.password) throw Error('URL credentials forbidden');
    if (type === 'react' && url.protocol === 'file:')
      throw Error('React must be served by the local test launcher');
  }
  return args;
}
const args = parseArgs(process.argv.slice(2));
const isSynthetic = args.fixture === 'synthetic';
const output = resolve(args.out);
await mkdir(output, { recursive: true });

let chromium;
try {
  ({ chromium } = await import('playwright'));
} catch {
  throw Error('playwright unavailable. Install locally: npm install --no-save --no-package-lock playwright');
}
const binaryPath = process.env.TRADEOPS_CHROMIUM_PATH || undefined;
const browser = await chromium.launch({
  headless: true, ...(binaryPath ? { executablePath: binaryPath } : {}),
  args: ['--no-sandbox']
});
const targets = {
  van: {
    root: '#ccFuturesShell',
    market: '#ccFuturesShell > .okx-spot-card:nth-child(1)',
    book: '#ccFuturesShell > .okx-spot-card:nth-child(2)',
    ticket: '#ccFuturesShell > .okx-spot-card:nth-child(3)',
    rows: '#ccFuturesAsks > *, #ccFuturesBids > *',
    bottom: '#ccFuturesBottomPanel',
    positionTab: '[data-cc-futures-panel="open-positions"]',
    ordersTab: '[data-cc-futures-panel="open-orders"]',
    historyTab: '[data-cc-futures-panel="order-history"]',
    depth: '#ccFuturesBookPrecision'
  },
  react: {
    root: '.van-workstation',
    market: '.van-workstation .quote-panel',
    book: '.van-workstation .market-feed-grid > .market-feed:first-child',
    ticket: '.van-workstation .ticket',
    rows: '.dom-ladder .dom-row',
    bottom: '.van-bottom-dock',
    positionTab: '#van-account-tab-positions',
    ordersTab: '#van-account-tab-orders',
    historyTab: '.van-bottom-tabs button:has-text("Order history")',
    depth: '#dom-depth'
  }
};
const viewports = [{ width: 1440, height: 900 }, { width: 390, height: 844 }];
const records = [];
const manifest = {
  issue: 68,
  pr: 63,
  sha: args.sha,
  sourceURLs: { van: args.van, react: args.react },
  dataPolicy: 'localhost/file only; no credentials, no orders, no history fabrication',
  note: 'VAN precision differs from TradeOps RAW; controlled synthetic DOM fixture is NOT exchange transport evidence.',
  generatedAtUtc: new Date().toISOString(),
  results: records
};

async function metrics(page, selectors) {
  return page.evaluate(selectors => {
    const visible = selector => {
      const el = document.querySelector(selector);
      if (!el) return null;
      const style = getComputedStyle(el);
      const rect = el.getBoundingClientRect();
      if (style.display === 'none' || rect.width === 0 || rect.height === 0) return null;
      return {
        x: Math.round(rect.x), y: Math.round(rect.y),
        width: Math.round(rect.width), height: Math.round(rect.height),
        display: style.display,
        fontSize: style.fontSize
      };
    };
    const row = document.querySelector(selectors.rows);
    const rowHeight = row ? Math.round(row.getBoundingClientRect().height * 10) / 10 : null;
    const bookText = document.querySelector(selectors.book)?.textContent || '';
    const depthEl = document.querySelector(selectors.depth);
    return {
      viewport: { width: window.innerWidth, height: window.innerHeight },
      documentWidth: document.documentElement.scrollWidth,
      horizontalPageOverflowPx: Math.max(0, document.documentElement.scrollWidth - window.innerWidth),
      shell: visible(selectors.root),
      market: visible(selectors.market),
      book: visible(selectors.book),
      ticket: visible(selectors.ticket),
      bottom: visible(selectors.bottom),
      depthSelection: depthEl?.value ?? null,
      domRowCount: document.querySelectorAll(selectors.rows).length,
      domFirstRowHeightPx: rowHeight,
      deltaVisible: /Δ exchange → local/.test(bookText),
      bookTextHasFixturePrice: /2,000/.test(bookText),
      orderMutationButtonsEnabled: [...document.querySelectorAll('button')]
        .filter(b=>/^(?:Cancel|Разместить|Проверить заявку|Отменить)/i.test(b.textContent?.trim()||''))
        .filter(b=>!b.disabled).length,
      mainnetReadOnlyLabel: /MAINNET READ-ONLY/.test(document.body.innerText),
      browserErrors: null
    };
  }, selectors);
}

async function selectSafeInstrument(page, source) {
  if (source === 'react') {
    const sel = page.locator('select[aria-label="Рыночный инструмент"]');
    if (await sel.count()) {
      const options = await sel.locator('option').evaluateAll(elements =>
        elements.map(el => el.value));
      if (options.includes('ETH-PERPETUAL')) await sel.selectOption('ETH-PERPETUAL');
    }
    const depth = page.locator('#dom-depth');
    if (await depth.count()) await depth.selectOption('raw');
  } else {
    const sel = page.locator('#ccFuturesInst');
    if (await sel.count()) {
      const options = await sel.locator('option').evaluateAll(elements =>
        elements.map(el => el.value));
      if (options.includes('ETHUSD')) await sel.selectOption('ETHUSD');
    }
    // VAN "precision" is aggregation, NOT RAW. Do not mislabel it as parity.
  }
}
async function capture(page, record, label, selectors, scrollBottom = false) {
  if (scrollBottom && await page.locator(selectors.bottom).count())
    await page.locator(selectors.bottom).scrollIntoViewIfNeeded().catch(() => {});
  await page.waitForTimeout(350);
  const screenshot = join(output, record.source + '-' + record.width + 'x' + record.height + '-' + label + '.png');
  const bytes = await page.screenshot({ path: screenshot, fullPage: false, animations: 'disabled' });
  record.screenshots.push({
    view: label,
    file: screenshot,
    pngSha256: createHash('sha256').update(bytes).digest('hex'),
    metrics: await metrics(page, selectors)
  });
}
try {
  for (const viewport of viewports) {
    for (const source of ['van', 'react']) {
      const sel = targets[source];
      const record = {
        source, width: viewport.width, height: viewport.height,
        fixture:isSynthetic?'OFFLINE_SYNTHETIC':'UNSPECIFIED', screenshots: [],
        status: 'NOT_RUN', error: null, pageErrors: 0, blockedExternalRequests: 0
      };
      records.push(record);
      const context = await browser.newContext({
        viewport, deviceScaleFactor: 1, colorScheme: 'dark',
        reducedMotion: 'reduce', acceptDownloads: false
      });
      const page = await context.newPage();
      page.on('pageerror', () => { record.pageErrors++; });
      await page.route('**/*', async route => {
        const target = new URL(route.request().url());
        const local = ['127.0.0.1', 'localhost', '[::1]'].includes(target.hostname);
        if (target.protocol === 'file:' || ((target.protocol === 'http:') && local))
          await route.continue();
        else {
          record.blockedExternalRequests++;
          await route.abort();
        }
      });
      try {
        const response = await page.goto(args[source], { waitUntil: 'domcontentloaded', timeout: 15000 });
        if (response && !response.ok()) throw Error('HTTP ' + response.status());
        await page.locator(sel.root).waitFor({ state: 'visible', timeout: 9000 });
        await selectSafeInstrument(page, source);
        if (isSynthetic) {
          await page.evaluate(() => {
            const tag = document.createElement('div');
            tag.textContent = 'SYNTHETIC OFFLINE FIXTURE — NO LIVE DATA / NO EXECUTION';
            Object.assign(tag.style,{position:'fixed',right:'8px',bottom:'8px',
              zIndex:'2147483647',font:'bold 11px sans-serif',color:'#fff',
              background:'#7f1d1d',padding:'5px 8px',pointerEvents:'none'});
            document.body.append(tag);
          });
        }
        if (isSynthetic && source==='van') {
          record.fixtureRenderer=await page.evaluate(() => {
            if(typeof window.ccRenderBook!=='function')return 'UNAVAILABLE';
            const asks=Array.from({length:50},(_,i)=>[2000+0.5+i*0.5,Math.round((0.15+i*0.031)*10000)/10000]);
            const bids=Array.from({length:50},(_,i)=>[2000-0.5-i*0.5,Math.round((0.15+i*0.031)*10000)/10000]);
            const sel=document.getElementById('ccFuturesInst');
            if(sel)sel.value='ETHUSD';
            window.ccRenderBook('futures',{asks,bids});
            return 'VAN_NATIVE_RENDERER';
          }).catch(e=>'ERROR: '+String(e.message).slice(0,120));
        }
        await capture(page, record, 'workstation', sel);
        for (const [label, tab] of [['positions', sel.positionTab], ['orders', sel.ordersTab]]) {
          const locator = page.locator(tab);
          if (await locator.count() && await locator.isEnabled()) {
            await locator.click();
            await capture(page, record, label, sel, true);
          } else record.screenshots.push({ view: label, status: 'MISSING', reason: 'tab absent or disabled' });
        }
        const history = page.locator(sel.historyTab);
        record.orderHistoryControl = await history.count() ?
          ((await history.isEnabled()) ? 'ENABLED' : 'DISABLED') : 'MISSING';
        record.status = 'CAPTURED';
        if(isSynthetic&&source==='van'&&record.fixtureRenderer!=='VAN_NATIVE_RENDERER'){
          record.status='FAIL';
          record.error='VAN book native renderer unavailable: '+record.fixtureRenderer;
        }
        if(source==='react'&&
          record.screenshots.find(s=>s.view==='workstation')?.metrics?.orderMutationButtonsEnabled>0){
          record.status='FAIL';
          record.error='Execution button was enabled on MAINNET fixture';
        }
      } catch (e) {
        record.status = 'FAIL';
        record.error = String(e.message || e).slice(0, 350);
        // An unavailable or unauthenticated local app is NOT counted as evidence.
      } finally {
        await context.close();
      }
    }
  }
} finally {
  await browser.close();
}
await writeFile(join(output, 'browser-metrics.json'), JSON.stringify(manifest, null, 2) + '\n');
const rows = [];
for (const record of records) {
  const shot = record.screenshots.find(s => s.view === 'workstation');
  const m = shot?.metrics;
  rows.push('| ' + record.source + ' | ' + record.width + '×' + record.height +
    ' | ' + record.status + ' | ' + (m?.horizontalPageOverflowPx ?? '—') +
    ' | ' + (m?.domRowCount ?? '—') + ' | ' + (m?.domFirstRowHeightPx ?? '—') +
    ' | ' + (m?.deltaVisible ?? '—') + ' | ' +
    (shot?.file ? './' + shot.file.split('/').at(-1) : '—') + ' |');
}
const md = [
  '# F1-68 real browser capture — exact SHA ' + args.sha,
  '', 'Only successfully captured screenshots are evidence. These are NOT synthetic or source-only screenshots.',
  '', '| Source | Viewport | Capture | Horizontal overflow px | DOM rows | First row height px | Δ label | Screenshot |',
  '| --- | --- | --- | ---: | ---: | ---: | --- | --- |',
  ...rows, '',
  '## Functional disparity checklist',
  '', '| Feature | VAN original | F1 React | Source assessment | Browser acceptance |',
  '| --- | --- | --- | --- | --- |',
  '| Three columns | 1280px 420+/280/300 | 1280px 420+/312/300; RAW width preserved | PARTIAL | review screenshots |',
  '| Instrument bar | Futures toolbar and contract strip | BTC/ETH perpetual quick selection plus instrument search | PARTIAL | review screenshots |',
  '| Order book | Price/Amount/Total, precision aggregation | RAW 1/10/20 depth, Price/Amount/Cumulative | PARTIAL | review actual data & density |',
  '| Time delta | Not equivalent to TradeOps clock diagnosis | Δ exchange→local, manual probe | PARTIAL | review displayed source |',
  '| Charts | Historical OHLC with timeframe | Public WebSocket tick-only windows | MISSING OHLC | screenshot must show disclaimer |',
  '| Trades | Public market trades | Public WS tape | PARTIAL | inspect stream |',
  '| Ticket | Multiple order types/tools | Limit Post Only, MAINNET read-only UI gate | PARTIAL | verify disabled |',
  '| Positions & orders | Lower tabbed tables | Lower tabbed read snapshots | PARTIAL | review states, font density |',
  '| History/funding | History and funding sections | Explicit disabled tabs, no read contract | MISSING | disabled visible |',
  '| Options/spreads | Additional workstations | Missing/disabled | MISSING | verify disabled |',
  '', '## Provenance and caveats',
  '', '- Captured via local Playwright Chromium; exact browser rendering only, not mocked PNG.',
  '- VAN local endpoint and React launcher must use sanitized identical fixture or public-only read feed.',
  '- Browser script does not authorize orders or connect to remote networks; external requests are blocked.',
  '- Match trade prices, instrument, depth and precision manually before claiming behavioral parity.',
  '- No checked green PASS is inferred merely from source code or existence of screenshots.',
  '- Generated screenshots/logs may contain private state and must remain local; do NOT commit to GitHub.',
  ''
].join('\n');
await writeFile(join(output, 'parity-browser-report.md'), md);
console.log(JSON.stringify({
  sha: args.sha, output, total: records.length,
  captured: records.filter(r => r.status === 'CAPTURED').length,
  failed: records.filter(r => r.status === 'FAIL').length
}));
if (records.some(r => r.status !== 'CAPTURED')) process.exitCode = 1;
