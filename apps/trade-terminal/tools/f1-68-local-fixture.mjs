#!/usr/bin/env node
/* F1-68: offline screenshot harness. Actual compiled React + untouched VAN.
   All data synthetic, no private credentials, API writes forbidden. */
import {createServer} from 'node:http';
import {readFile,stat} from 'node:fs/promises';
import {resolve,extname,relative,sep} from 'node:path';
import {spawn} from 'node:child_process';
import {WebSocketServer} from 'ws';
const args={};
for(let i=2;i<process.argv.length;i+=2){
 if(!process.argv[i]?.startsWith('--')||!process.argv[i+1])throw Error('Expected --key value');
 args[process.argv[i].slice(2)]=process.argv[i+1];
}
for(const k of ['react-root','van-root','out','sha'])if(!args[k])throw Error('Missing --'+k);
if(!/^[a-f0-9]{40}$/.test(args.sha))throw Error('Full SHA required');
const dirs={react:resolve(args['react-root']),van:resolve(args['van-root'])};
for(const d of Object.values(dirs))if(!(await stat(d).catch(()=>null))?.isDirectory())throw Error('Missing frontend dir: '+d);
const mime={'.html':'text/html; charset=utf-8','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png','.json':'application/json','.woff2':'font/woff2'};
const levels=side=>Array.from({length:50},(_,i)=>({
 price:2000+(side==='ask'?0.5+i*0.5:-0.5-i*0.5),
 amount:Math.round((0.15+i*0.031)*10000)/10000
}));
const book={asks:levels('ask'),bids:levels('bid')};
const tape=Array.from({length:60},(_,i)=>({
 tradeId:'synthetic-'+i,direction:i%2?'sell':'buy',
 price:2000+Math.sin(i/5)*4.5,amount:0.07+i*0.002,
 timestamp:Date.now()-(59-i)*1000
}));
const instrumentNames=['BTC-PERPETUAL','ETH-PERPETUAL'];
const instruments=instrumentNames.map(name=>({
 name,kind:'future',currency:name.slice(0,3),active:true,tickSize:0.5,
 contractSize:10,minTradeAmount:1,expirationTimestamp:null,strike:null,optionType:null
}));
const sampleAccount=(currency)=>({currency,balance:currency==='BTC'?0.5:4.25,
 equity:currency==='BTC'?0.51:4.30,availableFunds:currency==='BTC'?0.45:3.99,
 marginBalance:currency==='BTC'?0.5:4.25});
const positions=[{instrument:'ETH-PERPETUAL',kind:'future',direction:'buy',
 nativeSize:2,markPrice:2000,floatingPnl:12.5}];
const orders=[{orderId:'SYNTHETIC-0001',instrument:'ETH-PERPETUAL',
 side:'buy',state:'open',amount:1,filledAmount:0,price:1997,label:'fixture'}];
const respond=(res,status,payload)=>{
 res.writeHead(status,{'content-type':'application/json; charset=utf-8','cache-control':'no-store','x-f1-synthetic':'yes'});
 res.end(JSON.stringify(payload));
};
async function staticFile(root,req,res){
 const u=new URL(req.url,'http://localhost');
 let name;
 try {name=decodeURIComponent(u.pathname);}catch{res.writeHead(400);res.end();return;}
 if(name.includes('\0')){res.writeHead(400);res.end();return;}
 const file=resolve(root,'.'+(name==='/'?'/index.html':name));
 const rel=relative(root,file);
 if(rel==='..'||rel.startsWith('..'+sep)){res.writeHead(403);res.end();return;}
 try{
  if(!(await stat(file)).isFile())throw Error('nonfile');
  res.writeHead(200,{'content-type':mime[extname(file)]||'application/octet-stream','cache-control':'no-store'});
  res.end(await readFile(file));
 }catch{res.writeHead(404);res.end('Not found');}
}
function create(kind){
 const root=dirs[kind],sockets=new Set();
 const server=createServer((req,res)=>{
  if(!['GET','HEAD'].includes(req.method)){
   return respond(res,405,{error:'OFFLINE SYNTHETIC READ-ONLY: order mutations forbidden'});
  }
  const u=new URL(req.url,'http://localhost');
  if(kind==='react'&&u.pathname.startsWith('/api/deribit-terminal/')){
   const route=u.pathname.slice('/api/deribit-terminal'.length);
   if(route==='/status')return respond(res,200,{
    environment:'mainnet',exchange:'Deribit',credentialsConfigured:true,
    tradingEnabled:false,maxContracts:100,instruments:instrumentNames,localOnly:true
   });
   if(route==='/account')return respond(res,200,sampleAccount(u.searchParams.get('currency')||'ETH'));
   if(route==='/positions')return respond(res,200,positions);
   if(route==='/orders')return respond(res,200,orders);
   if(route==='/market/clock')return respond(res,200,{
    status:'ok',clockOffsetMs:0,rttMs:40,sampleUnixMs:Date.now()
   });
   if(route==='/market/instruments')return respond(res,200,{
    currency:'ETH',kind:'future',available:2,items:instruments,cachedUntilUtc:new Date().toISOString()
   });
   if(route==='/market/instrument')return respond(res,200,{
    name:u.searchParams.get('instrument')||'ETH-PERPETUAL',tickSize:0.5,tickSizeSteps:[],minimumTradeAmount:1
   });
   return respond(res,404,{error:'No fixture contract for '+route});
  }
  if(kind==='van'&&u.pathname.startsWith('/api/')){
   return respond(res,200,{success:true,data:[],rows:[],items:[],total:0});
  }
  return void staticFile(root,req,res);
 });
 if(kind==='react'){
  const wss=new WebSocketServer({noServer:true});
  server.on('upgrade',(req,socket,head)=>{
   const u=new URL(req.url,'http://localhost');
   if(u.pathname!=='/api/deribit-terminal/market/stream'){socket.destroy();return;}
   const instrument=u.searchParams.get('instrument')||'ETH-PERPETUAL';
   const depth=u.searchParams.get('depth')||'raw';
   wss.handleUpgrade(req,socket,head,ws=>{
    sockets.add(ws);ws.on('close',()=>sockets.delete(ws));
    const msg=o=>ws.send(JSON.stringify({instrument,...o}));
    msg({type:'status',depth,state:'synced',diagnostics:{
     publicL2State:'synced',privateOrdersState:'unavailable',
     requestedDepth:depth==='raw'?50:Number(depth),
     exchangeChannel:'synthetic-only',exchangeMode:'synthetic',
     reasonCode:null,failureAtUtc:null,expectedChangeId:null,
     prevChangeId:null,changeId:1234567,reconnectAttempts:0,counters:{}
    }});
    const now=Date.now();
    msg({type:'book',depth,state:'synced',messages:1,exchangeAgeMs:465,
     book:{instrument,
      bids:depth==='raw'?book.bids:book.bids.slice(0,Number(depth)),
      asks:depth==='raw'?book.asks:book.asks.slice(0,Number(depth)),
      changeId:1234567,exchangeTimestamp:now-465,localReceivedAtUnixMs:now}});
    msg({type:'ticker',bestBidPrice:1999.5,bestAskPrice:2000.5,
     lastPrice:2000,markPrice:2000.1,indexPrice:1999.8,timestampUtc:new Date().toISOString()});
    msg({type:'trades',trades:tape,receivedAtUtc:new Date().toISOString()});
   });
  });
  server.closeSynthetic=()=>{for(const s of sockets)s.terminate();wss.close();};
 }
 return server;
}
const react=create('react'),van=create('van');
const listen=s=>new Promise((ok,fail)=>{s.once('error',fail);s.listen(0,'127.0.0.1',()=>ok(s.address().port));});
let code=1;
try{
 const reactPort=await listen(react),vanPort=await listen(van);
 const child=spawn(process.execPath,[
  resolve(import.meta.dirname,'f1-browser-compare.mjs'),
  '--react','http://127.0.0.1:'+reactPort+'/',
  '--van','http://127.0.0.1:'+vanPort+'/deribit.html',
  '--sha',args.sha,'--out',resolve(args.out),'--fixture','synthetic'
 ],{stdio:'inherit'});
 code=await new Promise((ok,fail)=>{child.on('error',fail);child.on('exit',(n,sig)=>ok(sig?1:n??1));});
}finally{
 react.closeSynthetic?.();
 await Promise.all([new Promise(ok=>react.close(ok)),new Promise(ok=>van.close(ok))]);
}
process.exitCode=code;
