const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '../..');
const ag = require(path.join(root, 'tmp/psd-tools/node_modules/ag-psd'));
const runtime = 'C:/Users/FANG Yingjie/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules';
const sharp = require(path.join(runtime, 'sharp'));
const {createCanvas, ImageData} = require(path.join(runtime, '@napi-rs/canvas'));
ag.initializeCanvas(createCanvas);
const out = path.join(root, 'output/photoshop');
fs.mkdirSync(out, {recursive:true});
const W = Math.round(148*300/25.4), H = Math.round(210*300/25.4);
const sx=W/148, sy=H/210;
const decode=s=>s.replace(/&amp;/g,'&').replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'"');
const wrap=s=>`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 148 210">${s}</svg>`;
function attrs(s) {return Object.fromEntries([...s.matchAll(/([\w-]+)="([^"]*)"/g)].map(m=>[m[1],m[2]]));}
function flatten(items) {return items.flatMap(item=>item.children?flatten(item.children):[item]);}
function group(name, children) {return {name,opened:false,blendMode:'pass through',children};}
async function bitmap(markup) {
  const {data,info}=await sharp(Buffer.from(wrap(markup))).ensureAlpha().raw().toBuffer({resolveWithObject:true});
  let l=W,t=H,r=-1,b=-1;
  for(let y=0;y<H;y++)for(let x=0;x<W;x++)if(data[(y*W+x)*4+3]) {if(x<l)l=x;if(x>r)r=x;if(y<t)t=y;if(y>b)b=y;}
  if(r<0)return null;
  const width=r-l+1,height=b-t+1,pixels=new Uint8ClampedArray(width*height*4);
  for(let y=0;y<height;y++)pixels.set(data.subarray(((y+t)*W+l)*4,((y+t)*W+l+width)*4),y*width*4);
  return {left:l,top:t,right:r+1,bottom:b+1,imageData:{width,height,data:pixels}};
}
async function build(side) {
  const svg=fs.readFileSync(path.join(root,`output/pdf/neeeva-flyer-${side}.svg`),'utf8');
  const elements=[...svg.matchAll(/<(?:rect|line|circle|path)\b[^>]*\/?>|<text\b[^>]*>[^<]*<\/text>/g)].map(m=>m[0]);
  const bg=[],art=[],hero=[],qr=[],texts=[];
  for(let i=0;i<elements.length;i++){
    const e=elements[i],a=attrs(e),layer=await bitmap(e);
    if(!layer)continue;
    if(e.startsWith('<text')){
      const value=decode(e.match(/>([^<]*)<\/text>/)[1]);
      const font=a['font-weight']==='700'?'MicrosoftYaHei-Bold':'MicrosoftYaHei';
      const rgb=a.fill.replace('#','');
      layer.name='文字｜'+value;
      layer.text={text:value,transform:[sx/sy,0,0,1,+a.x*sx,+a.y*sy],orientation:'horizontal',antiAlias:'sharp',shapeType:'point',
        style:{font:{name:font},fontSize:+a['font-size']*sy,fillColor:{r:parseInt(rgb.slice(0,2),16),g:parseInt(rgb.slice(2,4),16),b:parseInt(rgb.slice(4,6),16)}},
        paragraphStyle:{justification:a['text-anchor']==='middle'?'center':a['text-anchor']==='end'?'right':'left'}};
      if(value==='图片')hero.push(layer);else if(value==='二维码')qr.push(layer);else texts.push(layer);
    }else{
      layer.name=i===0?'底色｜暖白':`装饰 ${String(i).padStart(2,'0')}`;
      if(i===0) {layer.protected={transparency:true,position:true,composite:true};bg.push(layer);}
      else if(side==='front'&&i>=5&&i<=10)hero.push(layer);
      else if(side==='back' && ((a.y!==undefined && +a.y>=180)|| (a.y1!==undefined && +a.y1>=180)))qr.push(layer);
      else art.push(layer);
    }
  }
  const children=[...bg,group('装饰与色块',art)];
  if(hero.length)children.push(group('主图占位｜替换时隐藏此组',hero));
  if(qr.length)children.push(group('二维码占位｜替换时隐藏此组',qr));
  children.push(group('可编辑文案｜使用文字工具 T',texts));
  const composed=createCanvas(W,H),ctx=composed.getContext('2d');
  for(const layer of flatten(children)){
    const im=layer.imageData,can=createCanvas(im.width,im.height);
    can.getContext('2d').putImageData(new ImageData(im.data,im.width,im.height),0,0);
    ctx.drawImage(can,layer.left,layer.top);
  }
  const psd={width:W,height:H,colorMode:3,bitsPerChannel:8,children,canvas:composed,
    imageResources:{resolutionInfo:{horizontalResolution:300,horizontalResolutionUnit:'PPI',widthUnit:'Centimeters',verticalResolution:300,verticalResolutionUnit:'PPI',heightUnit:'Centimeters'}}};
  const filename=path.join(out,`NeEEvA-${side==='front'?'正面':'背面'}-可编辑.psd`);
  fs.writeFileSync(filename,ag.writePsdBuffer(psd,{generateThumbnail:true,trimImageData:false}));
  const read=ag.readPsd(fs.readFileSync(filename),{useImageData:true,skipThumbnail:true});
  const restored=flatten(read.children),expected=flatten(children);
  if(read.width!==W||read.height!==H||read.imageResources.resolutionInfo.horizontalResolution!==300)throw Error('Size metadata mismatch');
  if(restored.length!==expected.length)throw Error('Layer count mismatch');
  for(let i=0;i<expected.length;i++){
    if(expected[i].text?.text!==restored[i].text?.text)throw Error('Text mismatch '+i);
    if(expected[i].text&&expected[i].text.style.font.name!==restored[i].text.style.font.name)throw Error('Font mismatch');
  }
  const composite=read.imageData;
  await sharp(Buffer.from(composite.data),{raw:{width:W,height:H,channels:4}}).png().toFile(path.join(out,`${side}-preview.png`));
  console.log(JSON.stringify({file:filename,layers:restored.length,editableTextLayers:restored.filter(x=>x.text).length,width:W,height:H,ppi:300,verified:true}));
}
(async()=>{await build('front');await build('back');})().catch(e=>{console.error(e);process.exitCode=1;});
