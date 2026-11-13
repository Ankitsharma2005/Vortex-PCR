const easing='cubic-bezier(.2,.7,.2,1)';
import {get} from './store.js';
export function element(tag,cls,parent){const e=document.createElement(tag);if(cls)e.className=cls;if(parent)parent.append(e);return e;}
export function stage(box){const s=element('div','stage',box);const resize=()=>{const scale=Math.min(box.clientWidth/1920,box.clientHeight/1080);s.style.transform=`translate(-50%,-50%) scale(${scale})`;};const observer=new ResizeObserver(resize);observer.observe(box);resize();return s;}
function play(e,type,seconds,out=false){e.getAnimations().forEach(a=>a.cancel());const from={none:{},fade:{opacity:0},slideL:{transform:'translateX(-110%)',opacity:0},slideR:{transform:'translateX(110%)',opacity:0},slideU:{transform:'translateY(110%)',opacity:0},zoom:{transform:'scale(.6)',opacity:0},wipe:{clipPath:'inset(0 100% 0 0)'}}[type]??{};const to={transform:'none',opacity:1,clipPath:'none'};return e.animate(out?[to,from]:[from,to],{duration:type==='none'?0:seconds*1000,easing,fill:'both'}).finished.catch(()=>{});}
export function mount(comp,host){
 const root=element('div','cg-root',host),group=element('div','cg-root',root),band=element('div','band',group);
 Object.assign(band.style,{top:comp.band.y+'px',height:comp.band.h+'px',background:comp.band.color,opacity:comp.band.op/100});
 let alive=true;const timers=new Set(),urls=[],videos=[];let finishWait;
 const sleep=ms=>new Promise(resolve=>{finishWait=resolve;const timer=setTimeout(()=>{timers.delete(timer);resolve();},ms);timers.add(timer);});
 const dispose=()=>{alive=false;finishWait?.();timers.forEach(clearTimeout);timers.clear();root.getAnimations({subtree:true}).forEach(a=>a.cancel());videos.forEach(v=>{v.pause();v.removeAttribute('src');v.load();});urls.forEach(URL.revokeObjectURL);root.remove();};
 async function addMedia(m,parent){
  const loaded=await Promise.all(m.ids.map(get));if(!alive)return;
  const sources=loaded.filter(Boolean).map(blob=>{const url=URL.createObjectURL(blob);urls.push(url);return url;});if(!sources.length)return;
  const item=element(m.kind==='video'?'video':'img','media',parent);Object.assign(item.style,{left:m.x+'px',top:m.y+'px',width:m.w+'px'});item.src=sources[0];
  if(m.kind==='video'){item.muted=true;item.loop=true;item.autoplay=true;item.playsInline=true;videos.push(item);item.play().catch(()=>console.warn('Media codec unavailable',m.name));}
  if(m.kind==='seq'){await Promise.all(sources.map(src=>new Promise(r=>{const i=new Image();i.onload=i.onerror=r;i.src=src;})));if(!alive)return;const start=performance.now();const step=()=>{if(!alive)return;item.src=sources[Math.floor((performance.now()-start)*m.fps/1000)%sources.length];const timer=setTimeout(()=>{timers.delete(timer);step();},1000/Math.max(1,m.fps));timers.add(timer);};step();}
 }
 const mediaHost=element("div","cg-root",group); for(const m of comp.media)addMedia(m,mediaHost).catch(console.warn);
 const box=element('div','text',group),t=comp.t;Object.assign(box.style,{left:t.x+'px',top:t.y+'px',width:t.w+'px',maxHeight:Math.max(1,1080-t.y)+'px',fontFamily:`"${t.font}","Noto Sans Devanagari","Nirmala UI",Arial,sans-serif`,fontSize:t.size+'px',fontWeight:t.bold?'700':'400',fontStyle:t.italic?'italic':'normal',color:t.color,textAlign:t.align});
 (async()=>{await play(group,comp.band.in,comp.band.dur);await document.fonts.ready;if(!alive)return;const lines=comp.lines.length?comp.lines:['Text yaha aayega'];
  if(comp.type==='ticker'){box.style.height=t.size*1.3+'px';const span=element('span','crawl',box);span.textContent=lines.join(comp.sep)+comp.sep;await document.fonts.ready;if(!alive)return;const width=span.scrollWidth;span.animate([{transform:`translateX(${t.w}px)`},{transform:`translateX(${-width}px)`}],{duration:(t.w+width)/Math.max(10,comp.speed)*1000,iterations:Infinity,easing:'linear'});}
  else {let i=0;while(alive){box.textContent=lines[i++%lines.length];await play(box,t.in,t.dur);if(!alive||lines.length<2)break;await sleep(t.hold*1000);if(!alive)break;await play(box,t.out,t.dur,true);}}
 })().catch(console.warn);
 return {root,kill:dispose,async stop(){if(!alive)return;alive=false;finishWait?.();timers.forEach(clearTimeout);await play(group,comp.band.out,comp.band.dur,true);dispose();}};
}
export function renderer(host){let controllers=new Map(),logo=null,logoKey='',revision=0;return {async sync(state,port){const version=++revision;const ids=state.air[port]??[];
 for(const [id,c]of controllers)if(!ids.includes(id)){if(!state.comps.some(comp=>comp.id===id))c.controller.kill();else c.controller.stop();controllers.delete(id);}
 for(const comp of state.comps.filter(c=>ids.includes(c.id))){const key=JSON.stringify(comp),old=controllers.get(comp.id);if(old?.key===key)continue;old?.controller.kill();controllers.set(comp.id,{key,controller:mount(comp,host)});}
 const l=state.logo,key=JSON.stringify(l.m)+l.fps;
 if(!state.logoAir[port]){if(logo){const old=logo;logo=null;await play(old.root,'fade',.4,true);old.kill();}return;}
 if(!logo||key!==logoKey){logo?.kill();logoKey=key;const node=element('div','logo',host);node.style.zIndex=1000;
 if(l.m){const comp={band:{y:0,h:1,color:'#000000',op:0,in:'none',out:'none',dur:0},media:[{...l.m,x:0,y:0,w:l.w,fps:l.fps}],t:{font:'Arial',size:10,x:0,y:0,w:1,dur:0,hold:1,in:'none',out:'none'},lines:[' '],type:'headline'};const control=mount(comp,node);logo={root:node,kill(){control.kill();node.remove();}};}
 else{const badge=element('div','default-logo',node);badge.textContent='NEWS24';logo={root:node,kill(){node.remove();}};}play(node.firstElementChild??node,'fade',.4);}
 if(version!==revision||!logo)return;logo.root.style.setProperty('--logo-width',l.w+'px');Object.assign(logo.root.style,{left:l.x+'px',top:l.y+'px',width:l.w+'px',opacity:l.op/100});const image=logo.root.querySelector('.media');if(image)image.style.width=l.w+'px';
 },kill(){revision++;controllers.forEach(c=>c.controller.kill());controllers.clear();logo?.kill();logo=null;}};}

