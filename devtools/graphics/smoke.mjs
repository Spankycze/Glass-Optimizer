import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import pixelmatch from 'pixelmatch';
import pngjs from 'pngjs';
import lucide from 'lucide-static';
import * as THREE from 'three';
import {animate} from 'motion';
const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(here,'../..');
const pkg=JSON.parse(fs.readFileSync(path.join(root,'package.json'),'utf8'));
const required=['pixelmatch','pngjs','lucide-static','lottie-web','motion','three','@penpot/mcp'];
for(const name of required){
 if(!pkg.devDependencies?.[name])throw new Error('Missing locked devDependency '+name);
 if(!fs.existsSync(path.join(root,'node_modules',name,'package.json')))throw new Error('Not installed '+name);
}
if(!lucide.Activity)throw new Error('Lucide icon export not working');
if(!THREE.Scene||!THREE.WebGLRenderer)throw new Error('Three.js module is missing');
if(typeof animate!=='function')throw new Error('Motion animate API not available');
if(typeof pixelmatch!=='function')throw new Error('Pixelmatch unavailable');
const {PNG}=pngjs,build=path.join(here,'generated');
for(const file of ['tokens.css','themes.json','penpot-artboard.svg','manifest.json','icons/cpu.svg']){
 if(!fs.existsSync(path.join(build,file)))throw new Error('Missing generated graphics: '+file);
}
const p=new PNG({width:8,height:8});
const q=new PNG({width:8,height:8});
p.data.fill(255);q.data.fill(255);
q.data[4*(3*8+3)]=0;
const diff=new PNG({width:8,height:8});
const n=pixelmatch(p.data,q.data,diff.data,8,8,{threshold:0.05});
if(n!==1)throw new Error('Pixelmatch selfcheck wrong difference: '+n);
const source=fs.readFileSync(path.join(build,'penpot-artboard.svg'),'utf8');
if(!source.includes('xmlns="http://www.w3.org/2000/svg"'))throw new Error('Penpot SVG invalid');
const vendor=path.join(here,'vendor','WindowsAppSDK-Samples');
const penpot=path.join(root,'node_modules','@penpot','mcp','bin','mcp-local.js');
console.log('GRAPHICS_TOOLCHAIN_SMOKE_PASS',JSON.stringify({
 packages:required.length,
 icons:JSON.parse(fs.readFileSync(path.join(build,'manifest.json'),'utf8')).iconCount,
 pixelDiff:n,
 penpotMcpAvailable:fs.existsSync(penpot),
 windowsSamplesAvailable:fs.existsSync(path.join(vendor,'.git')),
 blenderPortableAvailable:fs.existsSync(path.join(here,'vendor','blender-5.2.2-windows-x64','blender.exe')),
 browserProcessesStarted:false,
 runtimeModified:false
}));
