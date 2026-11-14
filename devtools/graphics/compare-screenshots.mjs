import fs from 'node:fs';
import path from 'node:path';
import pixelmatch from 'pixelmatch';
import pngjs from 'pngjs';
const {PNG}=pngjs;
const [base,current,output,threshold='0.1']=process.argv.slice(2);
if(!base||!current||!output){
 console.error('Usage: node compare-screenshots.mjs baseline.png current.png diff.png [pixel-threshold 0..1]');
 process.exit(2);
}
for(const source of [base,current])if(!fs.existsSync(source))throw new Error('No such screenshot: '+source);
const a=PNG.sync.read(fs.readFileSync(base));
const b=PNG.sync.read(fs.readFileSync(current));
if(a.width!==b.width||a.height!==b.height){
 console.error('SIZE_MISMATCH',JSON.stringify({baseline:[a.width,a.height],candidate:[b.width,b.height]}));
 process.exit(3);
}
const diff=new PNG({width:a.width,height:a.height});
const changed=pixelmatch(a.data,b.data,diff.data,a.width,a.height,{
 threshold:Number(threshold),includeAA:false,alpha:0.8,diffColor:[255,128,25]
});
fs.mkdirSync(path.dirname(path.resolve(output)),{recursive:true});
fs.writeFileSync(output,PNG.sync.write(diff));
const report={width:a.width,height:a.height,pixels:a.width*a.height,changed,changedPercent:+(changed*100/(a.width*a.height)).toFixed(4),threshold:Number(threshold)};
fs.writeFileSync(path.resolve(output)+'.json',JSON.stringify(report,null,2));
console.log('GRAPHICS_PIXEL_COMPARE',JSON.stringify(report));
process.exitCode=changed?1:0;
