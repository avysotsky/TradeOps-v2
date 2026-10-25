import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright';

// Comparison snapshots render original VAN markup/CSS with all executable scripts
// removed, and the actual compiled React bundle. Historical VAN authentication,
// exchange requests, navigation and order handlers are NEVER executed.
const [rawDist,rawVan,rawOut,reactSha,vanSha] = process.argv.slice(2);
if (!rawDist || !rawVan || !rawOut || !/^[a-f0-9]{40}$/.test(reactSha??'') || !/^[a-f0-9]{40}$/.test(vanSha??'')) {
  console.error('Usage: node van-browser-parity.mjs <react-dist> <van-frontend-dist> <output> <40char-react-sha> <40char-van-sha>');
  process.exit(2);
}
const out=resolve(rawOut),van=resolve(rawVan),base='http://127.0.0.1:5181';
await mkdir(out,{recursive:true});
const manifest={reactSha,vanSha,engine:'Playwright Chromium',sourceMode:'VAN original HTML and inline CSS; all scripts stripped; network blocked; synthetic/manual DOM sections only',comparisonMode:'Visual/layout qualitative; numbers and expiries differ; functional VAN JavaScript not executed',screenshots:[],checks:[],errors:[]};
const check=async (label,fn)=>{try{await fn();manifest.checks.push({label,status:'PASS'});console.log('PASS '+label)}catch(e){manifest.checks.push({label,status:'FAIL',message:String(e)});console.error('FAIL '+label+' '+e)}};
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});
const viewports=[1440,768];
async function snapshot(page,filename,source,view,meta={}){
  const full=join(out,filename);
  await page.screenshot({path:full,fullPage:false,animations:'disabled'});
  manifest.screenshots.push({file:filename,source,viewport:view+'x900',...meta});
}
async function vanishScripts(html){
  return html.replace(/<script\b[^>]*>[\s\S]*?<\/script\s*>/gi,'')
    .replace(/<iframe\b[^>]*>[\s\S]*?<\/iframe\s*>/gi,'')
    .replace(/<link\b[^>]*rel=["'](?:preload|modulepreload|stylesheet)["'][^>]*>/gi,'')
    .replace(/<base\b[^>]*>/gi,'');
}
const safeDir=join(out,'_van-sanitized');
await mkdir(safeDir,{recursive:true});
for(const file of ['coincall.html','coincall-spreads.html']){
  const content=await readFile(join(van,file),'utf8');
  assert.ok(content.includes('ccSpreadsPanel'),'VAN source expected CoinCall markup');
  await writeFile(join(safeDir,file),await vanishScripts(content));
}
function vanToggle(view){
  const doc=document;
  const set=(s,show)=>{const el=doc.querySelector(s);if(!el)return;el.hidden=!show;el.classList.toggle('active',show)};
  for(const s of ['#cc-layer-trade','#cc-layer-assets','#cc-layer-accounts','#cc-layer-markets'])set(s,view!=='portfolio'&&view!=='accounts'?s==='#cc-layer-trade':s==='#cc-layer-'+(view==='portfolio'?'assets':'accounts'));
  const current=view==='portfolio'?'assets':view==='accounts'?'accounts':'trade';
  for(const tab of doc.querySelectorAll('.okx-primary-tab[data-cc-layer]'))tab.classList.toggle('active',tab.getAttribute('data-cc-layer')===current);
  for(const s of ['#ccSpreadsPanel','#ccSpotShell','#ccSpotBottomPanel','#ccFuturesShell','#ccFuturesBottomPanel','#ccOptionsPanel','#ccMarketDataPanel'])set(s,false);
  if(view==='spreads'){set('#ccSpreadsPanel',true)}
  if(view==='spot'){set('#ccSpotShell',true);set('#ccSpotBottomPanel',true)}
  if(view==='futures'){set('#ccFuturesShell',true);set('#ccFuturesBottomPanel',true)}
  if(view==='portfolio'){set('#cc-tab-overview',true)}
  if(view==='accounts'){
    const select=doc.querySelector('#ccAccountSelect');
    if(select){select.innerHTML='<option>DEMO · Alpha</option><option>DEMO · Beta</option>'}
  }
  for(const btn of doc.querySelectorAll('[data-cc-order], [data-cc-action], button.okx-nitro-action, #ccAddAccountBtn'))btn.setAttribute('disabled','');
  for(const input of doc.querySelectorAll('#ccApiKey,#ccApiSecret,#ccName')){input.value='';input.setAttribute('disabled','')}
  const status=doc.querySelector('#ccSpreadsLastUpdate');
  if(status)status.textContent='VAN STATIC MARKUP · ALL LIVE SCRIPTS DISABLED';
  if(view==='spot'||view==='futures'){
    const symbol=doc.querySelector(view==='spot'?'#ccSpotLast':'#ccFuturesLast');
    if(symbol)symbol.textContent='68,125.40 · MOCK';
    const bookHead=doc.querySelector(view==='spot'?'#ccSpotAsks':'#ccFuturesAsks');
    const bookBids=doc.querySelector(view==='spot'?'#ccSpotBids':'#ccFuturesBids');
    if(bookHead&&bookBids){bookHead.textContent='';bookBids.textContent='';
      for(let i=0;i<11;i++){for(const [t,sign] of [[bookHead,1],[bookBids,-1]]){
        const r=doc.createElement('div');r.className='okx-spot-book-row';
        const p=doc.createElement('span');p.textContent=(68125.4+sign*(i+1)*.1).toFixed(2);
        const a=doc.createElement('span');a.textContent=(.10+i*.01).toFixed(3);
        const ttl=doc.createElement('span');ttl.textContent=(.10+i*.01).toFixed(3);
        r.append(p,a,ttl);t.appendChild(r);
      }}
    }
  }
  if(view==='portfolio'){
    const balance=doc.querySelector('#ccTradingBalanceBody');
    if(balance){balance.textContent='';for(const [sym,eq] of [['USDT','85120.73'],['BTC','0.287'],['ETH','4.4']]){
      const row=doc.createElement('tr');
      for(const v of [sym,eq,eq,'0','Read-only demo']){const td=doc.createElement('td');td.textContent=v;row.appendChild(td)}balance.appendChild(row);
    }}
  }
  const footer=doc.createElement('div');
  footer.textContent='OFFLINE VAN REFERENCE — original inline CSS / HTML; JS and exchange calls disabled; synthetic data inserted only where noted.';
  footer.style.cssText='font:11px Segoe UI;color:#e2c992;background:#1a2031;border-top:1px solid #445;position:fixed;bottom:0;left:0;right:0;padding:3px 8px;z-index:9999';
  doc.body.appendChild(footer);
}
function rect(el){if(!el)return null;const r=el.getBoundingClientRect();return {x:Math.round(r.x),y:Math.round(r.y),width:Math.round(r.width),height:Math.round(r.height)}}
try{
  for(const width of viewports){
    const v=width+'x900';
    const vanContext=await browser.newContext({viewport:{width,height:900},deviceScaleFactor:1,serviceWorkers:'block'});
    const reactContext=await browser.newContext({viewport:{width,height:900},deviceScaleFactor:1,serviceWorkers:'block'});
    const vanPage=await vanContext.newPage(),reactPage=await reactContext.newPage();
    const reactOutbound=[]; const vanBlocked=[];
    for(const [ctx,denied] of [[vanContext,vanBlocked],[reactContext,reactOutbound]]){
      await ctx.route('**/*',route=>{
        const url=route.request().url();
        if(url.startsWith('file://'+safeDir+'/')||url.startsWith(base+'/'))return route.continue();
        denied.push(url);return route.abort();
      });
    }
    const reactErrors=[];
    reactPage.on('pageerror',e=>reactErrors.push(String(e)));
    await check(v+' built React loads',async()=>{const response=await reactPage.goto(base,{waitUntil:'networkidle'});assert.equal(response?.status(),200)});
    await check(v+' VAN static source renders real matrix markup',async()=>{
      await vanPage.goto(pathToFileURL(join(safeDir,'coincall-spreads.html')).href,{waitUntil:'domcontentloaded'});
      const count=await vanPage.locator('#coincallSpreadsGridHost .okx-nitro-row').count();
      assert.ok(count>=3,'Expected VAN static first-paint matrix with rows');
    });
    await check(v+' React two-axis matrix and safe quote preview',async()=>{
      assert.ok(await reactPage.locator('.cc-grid-col').count()>=3);
      assert.ok(await reactPage.locator('.cc-grid-rowhead').count()>=3);
      await reactPage.locator('.cc-grid-quote').first().click();
      assert.equal(await reactPage.locator('.cc-details .cc-actions button:disabled').count(),2);
      assert.equal(await reactPage.locator('.cc-modal-book').count(),2);
      assert.equal(await reactPage.locator('.cc-modal-actions button:disabled').count(),2);
    });
    await snapshot(vanPage,'van-spreads-'+v+'.png','VAN coincall-spreads.html',width,{state:'original static first-paint matrix, scripts removed'});

    await check(v+' VAN two-leg modal shell with harmless synthetic DOM',async()=>{
      await vanPage.evaluate(()=>{
        const modal=document.querySelector('#ccSpreadsTradeModal');
        const body=document.querySelector('#ccSpreadsTradeBody');
        const title=document.querySelector('#ccSpreadsTradeTitle');
        const subtitle=document.querySelector('#ccSpreadsTradeSubtitle');
        assertInternal(modal && body && title && subtitle,'VAN modal absent');
        modal.hidden=false;modal.style.display='flex';
        title.textContent='CoinCall Futures Spread · MOCK STRUCTURAL PREVIEW';
        subtitle.textContent='BTCUSDT-Perp / BTC-30OCT26 · synthetic legs · no executable controls';
        body.innerHTML='<div class="okx-nitro-legs-card" style="padding:10px;background:#0b1220;border:1px solid #1e293b"><div class="okx-nitro-legs-head">Legs · MOCK</div><div class="okx-nitro-quote-row"><span class="okx-nitro-side bid">BUY BTCUSDT-Perp</span><span class="okx-nitro-side ask">SELL BTC-30OCT26</span></div><p>Quote, size and execution controls disabled — fixture-only VAN modal geometry.</p><button disabled type="button">Buy spread — disabled</button><button disabled type="button">Sell spread — disabled</button></div><div class="okx-nitro-books-grid" style="padding:10px"><section class="okx-nitro-legs-card">PERP book · mock</section><section class="okx-nitro-legs-card">FUTURE book · mock</section></div>';
        function assertInternal(ok,message){if(!ok)throw Error(message)}
      });
      assert.equal(await vanPage.locator('#ccSpreadsTradeModal').isVisible(),true);
    });
    await snapshot(vanPage,'van-spread-legs-'+v+'.png','VAN modal original HTML/CSS shell',width,{state:'synthetic two-leg modal content, scripts disabled'});
    const vanModalGeometry=await vanPage.locator('#ccSpreadsTradeModal .okx-nitro-modal').evaluate(rect);
    await vanPage.locator('#ccSpreadsTradeModal').evaluate(el=>{el.hidden=true;el.style.display='none'});

    await reactPage.locator('.cc-spread-modal').scrollIntoViewIfNeeded();
    await snapshot(reactPage,'react-spreads-legs-'+v+'.png','React compiled Vite',width,{state:'selected spread legs'});
    const reactModalGeometry=await reactPage.locator('.cc-spread-modal').evaluate(rect);
    const reactLegBooks=await reactPage.locator('.cc-modal-book').evaluateAll(els=>els.map(el=>{const r=el.getBoundingClientRect();return {x:Math.round(r.x),y:Math.round(r.y),width:Math.round(r.width),height:Math.round(r.height)}}));
    await reactPage.getByRole('button',{name:'Close spread dialog'}).click();
    await reactPage.locator('.cc-grid-scroll').scrollIntoViewIfNeeded();
    await snapshot(reactPage,'react-spreads-grid-'+v+'.png','React compiled Vite',width,{state:'unselected spread matrix'});
    const geo={viewport:v,van:{spreadModal:vanModalGeometry},react:{spreadModal:reactModalGeometry,independentLegBooks:reactLegBooks}};
    geo.van.matrix=await vanPage.locator('#coincallSpreadsGridHost').evaluate(rect);
    geo.react.matrix=await reactPage.locator('.cc-grid-scroll').evaluate(rect);
    for(const mode of ['spot','futures','portfolio','accounts']){
      await vanPage.goto(pathToFileURL(join(safeDir,'coincall.html')).href,{waitUntil:'domcontentloaded'});
      await vanPage.evaluate(vanToggle,mode);
      await snapshot(vanPage,'van-'+mode+'-'+v+'.png','VAN coincall.html',width,{state:mode,scripts:'DISABLED',fixtures:'synthetic inserted only when noted'});
      const main=reactPage.getByRole('navigation',{name:'Main navigation'});
      if(mode==='spot'||mode==='futures'){
        await main.getByRole('button',{name:'Trade',exact:true}).click();
        await reactPage.getByRole('navigation',{name:'Trading products'}).getByRole('button',{name:mode==='spot'?'Spot':'Futures'}).click();
      }else if(mode==='portfolio')await main.getByRole('button',{name:'Assets'}).click();
      else await main.getByRole('button',{name:'Accounts'}).click();
      await snapshot(reactPage,'react-'+mode+'-'+v+'.png','React compiled Vite',width,{state:mode});
      if(mode==='spot'||mode==='futures'){
        geo.van[mode]=await vanPage.locator(mode==='spot'?'#ccSpotShell':'#ccFuturesShell').evaluate(rect);
        geo.react[mode]=await reactPage.locator('.cc-trading').evaluate(rect);
      }
    }
    await check(v+' account switching and mock order isolation',async()=>{
      await reactPage.getByRole('radio').nth(1).check();
      await reactPage.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Assets'}).click();
      await reactPage.getByRole('button',{name:'Order Center'}).click();
      const t=await reactPage.locator('.cc-bottom').innerText();
      assert.match(t,/MOCK-CC-B-001/);assert.doesNotMatch(t,/MOCK-CC-A-001/);
      await snapshot(reactPage,'react-orders-account-beta-'+v+'.png','React compiled Vite',width,{state:'account beta isolated orders'});
    });
    await check(v+' keyboard focus and viewport bounds',async()=>{
      await reactPage.keyboard.press('Tab');
      assert.ok(await reactPage.evaluate(()=>document.activeElement?.tagName!=='BODY'));
      assert.ok(await reactPage.locator('.cc-app').count()===1);
      if(width===768){
        assert.ok(await reactPage.locator('.cc-main').evaluate(el=>el.getBoundingClientRect().width)<=768);
      }
    });
    await check(v+' React no external requests or browser errors',async()=>{
      assert.deepEqual(reactErrors,[]);
      assert.deepEqual(reactOutbound,[]);
    });
    geo.vanBlockedResources=vanBlocked;
    await writeFile(join(out,'geometry-'+v+'.json'),JSON.stringify(geo,null,2)+'\n');
    await vanContext.close();await reactContext.close();
  }
}finally{await browser.close()}
manifest.summary={total:manifest.checks.length,passed:manifest.checks.filter(c=>c.status==='PASS').length,failed:manifest.checks.filter(c=>c.status==='FAIL').length};
manifest.status=manifest.summary.failed?'FAIL':'PASS';
await writeFile(join(out,'parity-manifest.json'),JSON.stringify(manifest,null,2)+'\n');
console.log('F2_VIS '+manifest.status+' '+manifest.summary.passed+' pass / '+manifest.summary.failed+' fail');
if(manifest.summary.failed)process.exitCode=1;
