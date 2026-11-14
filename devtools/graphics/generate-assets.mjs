import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(here,'../..');
const dst=path.join(here,'generated');
const icons=path.join(dst,'icons');
fs.mkdirSync(icons,{recursive:true});
const iconNames=[
 'activity','app-window','arrow-down-right','arrow-up-right','badge-check','bell',
 'bolt','brain-circuit','chart-no-axes-combined','check','chevron-down','chevron-right',
 'circle-check','circle-power','circle-stop','copy','cpu','database','download','expand',
 'eye','file-code','flame','gauge','hard-drive','history','house','layers','memory-stick',
 'message-square','minimize-2','minus','monitor','moon','more-horizontal','pause',
 'play','power','radio','refresh-cw','settings','shield','shield-check','sparkles',
 'square','square-check','square-x','terminal','timer','users','volume-2','wifi','x'
];
const source=path.join(root,'node_modules','lucide-static','icons');
const failures=[];
for(const name of iconNames){
 const input=path.join(source,name+'.svg');
 if(!fs.existsSync(input)){failures.push(name);continue;}
 const svg=fs.readFileSync(input,'utf8');
 if(!svg.includes('<svg')||!svg.includes('</svg>'))throw new Error('Malformed SVG '+name);
 fs.writeFileSync(path.join(icons,name+'.svg'),svg,'utf8');
}
if(failures.length)throw new Error('Missing Lucide icons: '+failures.join(', '));
const themes={
 optimizer:{background:'#0b1018',surface:'#141c27',raised:'#1d2835',stroke:'#384655',accent:'#ffb65d',accent2:'#ffda9c',text:'#f7f5f0',muted:'#a3b0be',good:'#65dca5'},
 light:{background:'#f4f6fa',surface:'#ffffff',raised:'#e8ecf2',stroke:'#b8c2d2',accent:'#b36a1c',accent2:'#d18b32',text:'#172131',muted:'#556575',good:'#17875d'},
 graphite:{background:'#080b10',surface:'#101722',raised:'#1c2531',stroke:'#344252',accent:'#e4a261',accent2:'#ffd6a4',text:'#f4f4f4',muted:'#a2acba',good:'#67dfad'}
};
const tokens=[];
for(const [key,theme] of Object.entries(themes)){
 const sel=key==='optimizer'?':root':`[data-rg-theme="${key}"]`;
 tokens.push(sel+'{'+Object.entries(theme).map(([k,v])=>`--rg-${k}:${v};`).join('')+'}');
}
tokens.push(`
:root{--rg-motion-fast:140ms;--rg-motion-default:240ms;--rg-motion-slow:380ms;--rg-motion-ease:cubic-bezier(.2,.75,.22,1)}
@keyframes rg-energy-breathe{0%,100%{opacity:.65;transform:scale(.985)}50%{opacity:1;transform:scale(1.015)}}
@keyframes rg-signal-scan{from{transform:translateX(-110%)}to{transform:translateX(110%)}}
@keyframes rg-appear{from{opacity:0;transform:translateY(7px)}to{opacity:1;transform:translateY(0)}}
@keyframes rg-orbit{to{transform:rotate(360deg)}}
@keyframes rg-live-dot{0%,100%{opacity:1}50%{opacity:.42}}
@media(prefers-reduced-motion:reduce){:root{--rg-motion-fast:0ms;--rg-motion-default:0ms;--rg-motion-slow:0ms}}
`);
fs.writeFileSync(path.join(dst,'tokens.css'),tokens.join('\n'));
fs.writeFileSync(path.join(dst,'themes.json'),JSON.stringify(themes,null,2));
// Penpot accepts native SVG; keep designs editable as vector shapes.
const c=themes.optimizer;
const penpot=`<svg xmlns="http://www.w3.org/2000/svg" width="960" height="675" viewBox="0 0 960 675" role="img" aria-label="RAM Guard vector artboard">
<rect width="960" height="675" rx="16" fill="${c.background}"/>
<rect x="12" y="12" width="204" height="651" rx="12" fill="${c.surface}" stroke="${c.stroke}"/>
<rect x="232" y="12" width="716" height="651" rx="12" fill="${c.surface}" stroke="${c.stroke}"/>
<circle cx="114" cy="91" r="32" fill="none" stroke="${c.accent}" stroke-width="2"/>
<circle cx="114" cy="91" r="21" fill="none" stroke="${c.accent2}" stroke-width="1"/>
<text x="29" y="156" fill="${c.text}" font-family="Segoe UI" font-weight="700" font-size="16">SPANKYHO OPTIMIZER</text>
<text x="254" y="67" fill="${c.accent}" font-family="Segoe UI" font-size="26" font-weight="700">OPTIMIZER DESIGN LAB</text>
<text x="254" y="92" fill="${c.muted}" font-family="Segoe UI" font-size="12">VECTOR DESIGN WORKSPACE · IMPORT INTO PENPOT</text>
<circle cx="605" cy="316" r="138" stroke="${c.stroke}" stroke-width="1" fill="none"/>
<circle cx="605" cy="316" r="115" stroke="${c.accent}" stroke-width="2" fill="none" stroke-dasharray="15 11"/>
<circle cx="605" cy="316" r="80" stroke="${c.accent2}" stroke-width="1.5" fill="none" stroke-dasharray="3 8"/>
<circle cx="605" cy="316" r="40" stroke="${c.accent}" fill="${c.raised}" stroke-width="2"/>
<rect x="254" y="523" width="672" height="111" rx="12" fill="${c.raised}" stroke="${c.stroke}"/>
<text x="279" y="556" fill="${c.muted}" font-family="Segoe UI" font-size="12">WINUI 3 / C# / WINDOWS COMPOSITION</text>
<text x="279" y="596" fill="${c.text}" font-family="Segoe UI" font-size="20">Native WinUI 3 application stays unchanged during design</text>
</svg>`;
fs.writeFileSync(path.join(dst,'penpot-artboard.svg'),penpot);
const report={version:1,project:'Spankyho Optimizer',iconCount:iconNames.length,themes:Object.keys(themes),artboard:path.relative(root,path.join(dst,'penpot-artboard.svg')),fpsTarget:30,generatedOnly:true};
fs.writeFileSync(path.join(dst,'manifest.json'),JSON.stringify(report,null,2));
console.log('GRAPHICS_ASSETS_GENERATED',JSON.stringify(report));
