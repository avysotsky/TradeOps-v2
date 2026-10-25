import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

const root = new URL('../src/', import.meta.url);
const program = readdirSync(root).filter(n=>/\.(ts|tsx)$/.test(n))
  .map(n=>({name:n,text:readFileSync(new URL(n,root),'utf8')}));

test('F2-01 frontend has no exchange I/O, credential persistence or browser transport',()=>{
  for(const file of program){
    assert.doesNotMatch(file.text,/\b(fetch|WebSocket|XMLHttpRequest|EventSource|localStorage|sessionStorage)\b/,
      'Forbidden frontend browser API in '+file.name);
    assert.doesNotMatch(file.text,/\b(apiKey|privateKey|secretKey|accessToken)\b/i,
      'Credential material in '+file.name);
  }
});
test('both local ticket and spread execution button pairs are disabled',()=>{
  const workstation = program.find(f=>f.name==='workstation.tsx')?.text ?? '';
  const actionBlocks = workstation.match(/<div className="cc-actions">[\s\S]*?<\/div>/g) ?? [];
  assert.equal(actionBlocks.length,2,'Expected two execution-only action blocks');
  for(const section of actionBlocks){
    const buttons = section.match(/<button\b[^>]*>/g) ?? [];
    assert.equal(buttons.length,2);
    for(const markup of buttons) assert.match(markup,/\bdisabled(?:\s|>)/);
  }
});
