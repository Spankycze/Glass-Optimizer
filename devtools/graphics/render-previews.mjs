import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import resvg from '@resvg/resvg-js';
import pngjs from 'pngjs';
const {Resvg}=resvg;
const {PNG}=pngjs;
const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(here,'../..');
const out=path.join(here,'generated','previews');
const themes=JSON.parse(fs.readFileSync(path.join(here,'generated','themes.json'),'utf8'));
const manifest=JSON.parse(fs.readFileSync(path.join(here,'generated','manifest.json'),'utf8'));
fs.mkdirSync(out,{recursive:true});
const isOptimizer=manifest.project==='Spankyho Optimizer';
const logo=isOptimizer?'SPANKYHO OPTIMIZER':'RAM GUARD';
const submenu=isOptimizer?
 ['Overview','Performance','System Tweaks','Network','GPU','Services','Windows']:
 ['Overview','Performance','Browser Tools','ChatGPT Booster','Kick Stream','Settings'];
const safe=s=>String(s).replace(/[&<>"']/g,x=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[x]));
const files=[];
for(const [name,t] of Object.entries(themes)){
 const nav=submenu.map((label,i)=>{
  const y=145+i*62,selected=i===0;
  return '<rect x="26" y="'+(y-30)+'" width="226" height="52" rx="12" fill="'+(selected?t.raised:t.surface)+'" stroke="'+(selected?t.accent:t.surface)+'" stroke-opacity="'+(selected?'0.8':'0')+'"/>'+
    '<circle cx="47" cy="'+(y-5)+'" r="6" fill="'+(selected?t.accent:t.stroke)+'"/>'+
    '<text x="65" y="'+y+'" font-size="16" fill="'+(selected?t.text:t.muted)+'">'+safe(label)+'</text>';
 }).join('\n');
 const cards=[
  {x:300,title:'CPU',value:'28%',percent:28},
  {x:526,title:'RAM',value:'45%',percent:45},
  {x:752,title:'GPU',value:'16%',percent:16}
 ].map(({x,title,value,percent})=>{
  const w=200,z=(w-30)*percent/100;
  return '<rect x="'+x+'" y="120" width="'+w+'" height="116" rx="17" fill="'+t.raised+'" stroke="'+t.stroke+'"/>'+
  '<text x="'+(x+18)+'" y="148" font-size="13" fill="'+t.muted+'">'+title+'</text>'+
  '<text x="'+(x+18)+'" y="196" font-size="32" fill="'+t.text+'" font-weight="700">'+value+'</text>'+
  '<rect x="'+(x+18)+'" y="209" width="'+(w-35)+'" height="4" rx="2" fill="'+t.stroke+'"/>'+
  '<rect x="'+(x+18)+'" y="209" width="'+z+'" height="4" rx="2" fill="'+t.accent+'"/>';
 }).join('\n');
 const lines=Array.from({length:8},(_,i)=>{
 const x1=314+i*95;
 const y1=490+Math.sin(i*1.3)*34;
 return '<circle cx="'+x1+'" cy="'+y1+'" r="3" fill="'+t.accent2+'"/>';
 }).join('');
 const circle=Array.from({length:4},(_,i)=>{
  const r=50+i*26;
  return '<circle cx="626" cy="377" r="'+r+'" fill="none" stroke="'+(i%2?t.accent:t.accent2)+'" stroke-opacity="'+(0.68-i*.11)+'" stroke-width="'+(i===0?2:1.3)+'" stroke-dasharray="'+(i%2?'4 10':'22 13')+'"/>';
 }).join('\n');
 const svg='<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="650" viewBox="0 0 1000 650">'+
 '<rect width="1000" height="650" rx="22" fill="'+t.background+'"/>'+
 '<rect x="12" y="12" width="258" height="626" rx="18" fill="'+t.surface+'" stroke="'+t.stroke+'"/>'+
 '<rect x="282" y="12" width="706" height="626" rx="18" fill="'+t.surface+'" stroke="'+t.stroke+'"/>'+
 '<rect x="28" y="34" width="44" height="44" rx="14" fill="'+t.raised+'" stroke="'+t.accent+'"/>'+
 '<path d="M39 57 H61 M50 46 V68" stroke="'+t.accent2+'" stroke-width="2.5"/>'+
 '<text x="83" y="61" font-size="15" font-weight="700" fill="'+t.text+'">'+safe(logo)+'</text>'+
 '<text x="302" y="54" font-size="22" font-weight="700" fill="'+t.text+'">'+safe(isOptimizer?'Optimizer / System Core':'Core / Stream Control')+'</text>'+
 '<text x="302" y="77" font-size="12" fill="'+t.muted+'">GRAPHICS PIPELINE · VECTOR DESIGN PREVIEW · NOT PRODUCTION UI</text>'+
 '<circle cx="941" cy="50" r="9" fill="'+t.good+'"/>'+
 nav+cards+
 '<rect x="300" y="252" width="652" height="278" rx="17" fill="'+t.background+'" stroke="'+t.stroke+'"/>'+
 '<text x="321" y="282" font-size="13" fill="'+t.accent+'">CORE ANIMATION REGION</text>'+
 '<circle cx="626" cy="377" r="150" fill="none" stroke="'+t.stroke+'" stroke-width="1"/>'+
 circle+
 '<circle cx="626" cy="377" r="29" fill="'+t.raised+'" stroke="'+t.accent+'" stroke-width="2"/>'+
 '<circle cx="626" cy="377" r="6" fill="'+t.accent2+'"/>'+
 '<text x="323" y="482" font-size="12" fill="'+t.muted+'">ANIMATION DESIGN TARGET 30 FPS · MOTION REDUCED SUPPORT</text>'+
 lines+
 '<rect x="300" y="546" width="652" height="72" rx="14" fill="'+t.raised+'" stroke="'+t.stroke+'"/>'+
 '<circle cx="328" cy="582" r="8" fill="'+t.good+'"/>'+
 '<text x="347" y="588" font-size="16" fill="'+t.text+'">GRAPHICS WORKSPACE READY</text>'+
 '<text x="765" y="586" font-size="12" fill="'+t.accent+'">'+safe(name.toUpperCase())+'</text>'+
 '<text x="26" y="606" font-size="12" fill="'+t.muted+'">Dev only · no settings changed</text>'+
 '</svg>';
 const svgFile=path.join(out,name+'-preview.svg');
 const pngFile=path.join(out,name+'-preview.png');
 fs.writeFileSync(svgFile,svg);
 const png=new Resvg(svg,{fitTo:{mode:'original'}}).render().asPng();
 fs.writeFileSync(pngFile,png);
 const info=PNG.sync.read(fs.readFileSync(pngFile));
 if(info.width!==1000||info.height!==650)throw new Error('Bad preview dimensions '+name);
 files.push({name,svg:path.relative(root,svgFile),png:path.relative(root,pngFile),width:info.width,height:info.height,bytes:png.length});
}
fs.writeFileSync(path.join(out,'previews-manifest.json'),JSON.stringify({project:manifest.project??'RAM Guard',rendered:files.length,previews:files},null,2));
console.log('GRAPHICS_PREVIEWS_RENDERED',JSON.stringify({count:files.length,themes:files.map(f=>f.name),size:'1000x650',automaticallyRendered:true}));
