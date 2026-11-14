import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import pixelmatch from 'pixelmatch';
import pngjs from 'pngjs';
const {PNG}=pngjs;
const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(here,'../..');
const out=path.join(here,'generated');
const reports=path.join(out,'reports');
fs.mkdirSync(reports,{recursive:true});
const isOptimizer=fs.existsSync(path.join(root,'Directory.Build.props'));
const project=isOptimizer?'Spankyho Optimizer':'RAM Guard';
const sourcePaths=isOptimizer?[
 'Directory.Build.props','src/SpankyhoOptimizer/MainWindow.xaml.cs'
]:[
 'runtime.cpp','jarvis_fidelity.inc','payload/ChromeExtension/chat_booster.js',
 'payload/ChromeExtension/kick_more.js','payload/FirefoxExtension/kick_more.js'
];
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const checkHashes=()=>{
 const entries=sourcePaths.filter(file=>fs.existsSync(path.join(root,file)));
 return Object.fromEntries(entries.map(file=>[file,hash(path.join(root,file))]));
};
const before=checkHashes(),start=Date.now(),stages=[];
function run(name,file){
 const child=spawnSync(process.execPath,[path.join(here,file)],{
  cwd:root,encoding:'utf8',timeout:100000,maxBuffer:5242880,windowsHide:true
 });
 if(child.status!==0||child.error){
   console.error('GRAPHICS_STAGE_FAILED',name,child.error?.message||child.stderr||child.stdout);
   process.exit(2);
 }
 for(const line of String(child.stdout||'').trim().split('\n').filter(Boolean))console.log(line);
 stages.push(name);
}
run('icons and themes','generate-assets.mjs');
run('module smoke','smoke.mjs');
run('render theme previews','render-previews.mjs');
const compareDir=path.join(out,'screenshots');
fs.mkdirSync(compareDir,{recursive:true});
const current=isOptimizer?'artifacts/real_app_current.png':'artifacts/runtime-visible-1166.png';
const alternatives=isOptimizer?
 ['artifacts/overview_printwindow2.png','artifacts/overview-hardware-check.png']:
 ['artifacts/runtime-visible-1165.png','artifacts/runtime-visible-1164.png'];
const result={available:false,baseline:null,candidate:current,changed:null,changedPercent:null,mode:'no-screenshot'};
const currentPath=path.join(root,current);
if(fs.existsSync(currentPath)){
 const currentPNG=PNG.sync.read(fs.readFileSync(currentPath));
 let previous=null,prevPNG=null;
 for(const relative of alternatives){
  const candidate=path.join(root,relative);
  if(!fs.existsSync(candidate))continue;
  const p=PNG.sync.read(fs.readFileSync(candidate));
  if(p.width===currentPNG.width&&p.height===currentPNG.height){
   previous=relative;prevPNG=p;break;
  }
 }
 if(!prevPNG){
  previous=current;prevPNG=currentPNG;
  result.mode='pixel-engine-self-check-only';
 }else result.mode='previous-available-capture-diagnostic';
 const diff=new PNG({width:currentPNG.width,height:currentPNG.height});
 const changed=pixelmatch(prevPNG.data,currentPNG.data,diff.data,
  currentPNG.width,currentPNG.height,{threshold:.1,includeAA:false,alpha:.8});
 const dest=path.join(compareDir,'native-current-diff.png');
 fs.writeFileSync(dest,PNG.sync.write(diff));
 Object.assign(result,{
  available:true,baseline:previous,changed,
  changedPercent:+(100*changed/(currentPNG.width*currentPNG.height)).toFixed(3),
  width:currentPNG.width,height:currentPNG.height,diff:path.relative(root,dest)
 });
 console.log('GRAPHICS_SCREENSHOT_CHECK',JSON.stringify(result));
}else console.warn('GRAPHICS_SCREENSHOT_UNAVAILABLE',current);
const after=checkHashes();
if(JSON.stringify(before)!==JSON.stringify(after)){
 console.error('ERROR: production source changed during graphics pipeline');
 process.exit(3);
}
const previewManifest=JSON.parse(fs.readFileSync(path.join(out,'previews','previews-manifest.json'),'utf8'));
const iconManifest=JSON.parse(fs.readFileSync(path.join(out,'manifest.json'),'utf8'));
// Self-contained offline review gallery, generated without opening a browser.
const escapeHtml=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const galleryItems=previewManifest.previews.map(item=>{
 const src='previews/'+path.basename(item.png);
 return '<figure><img loading="lazy" alt="'+escapeHtml(item.name)+' theme" src="'+escapeHtml(src)+'"/>'+
 '<figcaption>'+escapeHtml(item.name.toUpperCase())+' / VECTOR CONCEPT</figcaption></figure>';
}).join('');
const gallery='<!doctype html><html lang="en"><head><meta charset="utf-8"/>'+
 '<meta name="viewport" content="width=device-width,initial-scale=1"/>'+
 '<title>'+escapeHtml(project)+' Graphics Studio</title>'+
 '<style>body{background:#0b1018;color:#f4f4f4;font:14px Segoe UI,system-ui,sans-serif;margin:0;padding:30px}'+
 'header{max-width:1300px;margin:auto auto 24px}h1{font-size:25px}p{color:#aab6c5}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(420px,1fr));gap:18px;max-width:1400px;margin:auto}'+
 'figure{margin:0;background:#161d28;border:1px solid #344252;border-radius:15px;overflow:hidden}'+
 'img{display:block;width:100%;height:auto;aspect-ratio:1000/650}figcaption{padding:12px 18px;color:#e7ae6f;font-weight:600;letter-spacing:.08em}'+
 'small{font-size:12px;color:#aab6c5}@media(max-width:450px){main{grid-template-columns:1fr}body{padding:12px}}'+
 '</style></head><body><header><h1>'+escapeHtml(project)+' Graphics Studio</h1>'+
 '<p>Automatically generated previews — development only; not installed UI.</p></header>'+
 '<main>'+galleryItems+'</main></body></html>';
fs.writeFileSync(path.join(out,'index.html'),gallery);
const summary={
 schema:1,project,stages,passed:true,
 generated:new Date().toISOString(),elapsedMs:Date.now()-start,
 iconCount:iconManifest.iconCount,themes:iconManifest.themes,
 previews:previewManifest.previews,screenshot:result,
 productionSourceUntouched:true,productionVersionUnchanged:true,
 builtOrInstalledApplication:false,installedBrowserProfilesModified:false,
 penpotDesignNotAutoConnected:true,animationCapNotAppliedToProduction:true
};
fs.writeFileSync(path.join(reports,'latest.json'),JSON.stringify(summary,null,2));
const lines=[
 '# '+project+' - Automated graphics preparation','',
 '**Status: PASS.** Generated '+summary.iconCount+' SVG icons and '+summary.previews.length+' rendered theme previews.',
 '', 'Generated at '+summary.generated+'; elapsed '+summary.elapsedMs+'ms.',
 '', '## Theme previews','',
 ...summary.previews.map(({name,png})=>'- '+name+': '+png),
 '', '## Existing screenshot check','',
 '- Mode: '+result.mode,'- Baseline: '+(result.baseline??'none'),'- Candidate: '+result.candidate,
 '- Changed pixels: '+(result.changed===null?'not available':String(result.changed)+' ('+result.changedPercent+'%)'),
 '', '## Safe output contract','',
 '- Production application and browser addon files not edited.',
 '- No EXE installed, no browser launched/restarted, no Windows tweaks modified.',
 '- No Penpot account connection without user action.',
 '- Production FPS has NOT been changed to the design target of 30 FPS.',
 '- Animated regions in PNG previews are static concept mockups, not the production UI.',
 '- Different animation phases can produce expected visual differences.',
 '', 'JSON report: devtools/graphics/generated/reports/latest.json',''
];
fs.writeFileSync(path.join(reports,'latest.md'),lines.join('\n'));
console.log('GRAPHICS_AUTO_PIPELINE_PASS',JSON.stringify({
 project,assets:summary.iconCount,previews:summary.previews.length,
 screenshotMode:result.mode,elapsedMs:summary.elapsedMs,
 report:path.relative(root,path.join(reports,'latest.json')),runtimeUntouched:true
}));
