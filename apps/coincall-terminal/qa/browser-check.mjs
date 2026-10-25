import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { chromium } from 'playwright';

const baseUrl = 'http://127.0.0.1:5181';
const out = resolve(process.argv[2] ?? './screenshots');
const expectedSha = process.argv[3] ?? 'UNKNOWN';
await mkdir(out, {recursive:true});
let cases = 0;
let failures = 0;
const evidence = [];
async function check(name, fn) {
  try { await fn(); cases++; evidence.push({name,status:'PASS'}); console.log('PASS ' + name); }
  catch (error) { failures++; evidence.push({name,status:'FAIL',message:String(error)}); console.error('FAIL ' + name + ' ' + error); }
}
const browser = await chromium.launch({headless:true, args:['--no-sandbox']});
try {
  for (const width of [1440,768]) {
    const page = await browser.newPage({viewport:{width,height:900},deviceScaleFactor:1});
    const outbound = [];
    const errors = [];
    page.on('pageerror',error=>errors.push(String(error)));
    await page.route('**/*',route=>{
      const u = route.request().url();
      if (!u.startsWith(baseUrl + '/')) {outbound.push(u); return route.abort();}
      return route.continue();
    });
    await check(width + ' loads compiled React entrypoint', async()=>{
      const response = await page.goto(baseUrl + '/',{waitUntil:'networkidle',timeout:30000});
      assert.equal(response?.status(),200);
      assert.match(await page.locator('body').innerText(),/TradeOps CoinCall/);
      assert.match(await page.locator('body').innerText(),/NO EXECUTION/);
      await page.getByRole('navigation',{name:'Trading products'}).getByRole('button',{name:'Spreads'}).waitFor();
    });
    await check(width + ' default spread matrix and screenshots', async()=>{
      assert.ok(await page.locator('.cc-grid-rowhead').count() >= 4);
      assert.ok(await page.locator('.cc-grid-quote').count() >= 6);
      await page.screenshot({path:join(out,'coincall-react-'+width+'x900.png'),fullPage:false});
    });
    await check(width + ' weekly and monthly expiry filters',async()=>{
      await page.getByRole('checkbox',{name:/WeeklyExp/}).check();
      assert.equal(await page.locator('.cc-grid-rowhead').count(),3);
      await page.getByRole('checkbox',{name:/WeeklyExp/}).uncheck();
      await page.getByRole('checkbox',{name:/MonthlyExp/}).check();
      assert.equal(await page.locator('.cc-grid-rowhead').count(),4);
      await page.getByRole('checkbox',{name:/MonthlyExp/}).uncheck();
    });
    await check(width + ' two-leg buttons inert',async()=>{
      await page.locator('.cc-grid-quote').first().click();
      assert.equal(await page.locator('.cc-details .cc-actions button').count(),2);
      for (const item of await page.locator('.cc-details .cc-actions button').all()) assert.equal(await item.isDisabled(),true);
      await page.getByRole('button',{name:'Table',exact:true}).click();
      assert.ok(await page.locator('table.cc-spreadtable tbody tr').count()>=5);
      await page.getByRole('button',{name:'Grid',exact:true}).click();
    });
    await check(width + ' spot and futures ticket buttons inert',async()=>{
      const trade = page.getByRole('navigation',{name:'Trading products'});
      await trade.getByRole('button',{name:'Spot'}).click();
      assert.equal(await page.locator('.cc-ticket .cc-actions button:disabled').count(),2);
      const bookRow = page.locator('.cc-bookrow.cc-buy').first();
      const selected = Number((await bookRow.locator('span').first().innerText()).replaceAll(',',''));
      await bookRow.click();
      assert.equal(Number(await page.getByRole('spinbutton',{name:'Order price'}).inputValue()),selected);
      await trade.getByRole('button',{name:'Futures'}).click();
      assert.equal(await page.locator('.cc-ticket .cc-actions button:disabled').count(),2);
    });
    await check(width + ' account isolation in Order Center',async()=>{
      await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Accounts'}).click();
      await page.getByRole('radio').nth(1).check();
      await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Assets'}).click();
      await page.getByRole('button',{name:'Order Center'}).click();
      const text = await page.locator('.cc-bottom').innerText();
      assert.match(text,/MOCK-CC-B-001/);
      assert.doesNotMatch(text,/MOCK-CC-A-001/);
    });
    await check(width + ' no page crashes or outbound HTTP',async()=>{
      assert.deepEqual(errors,[]);
      assert.deepEqual(outbound,[]);
    });
    await page.close();
  }
} finally {
  await browser.close();
}
const report = {sha:expectedSha,kind:'F2-01 compiled React browser smoke',passed:cases,failed:failures,status:failures?'FAIL':'PASS',checks:evidence};
await writeFile(join(out,'browser-check.json'),JSON.stringify(report,null,2)+'\n');
console.log('F2-01_BROWSER_STATUS ' + report.status + ' ' + cases + ' PASS ' + failures + ' FAIL, SHA=' + expectedSha);
if (failures) process.exitCode = 1;
