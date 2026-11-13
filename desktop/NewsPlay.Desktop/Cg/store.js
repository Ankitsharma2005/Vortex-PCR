export const clone = x => structuredClone(x);
export const uid = () => crypto.randomUUID();
export const animations = ['none','fade','slideL','slideR','slideU','zoom','wipe'];
export function sample(type='headline') {
 const ticker=type==='ticker';
 return {id:uid(),name:ticker?'News Ticker':'Breaking Headline',type,
 band:{color:ticker?'#10253f':'#c1121f',op:94,y:ticker?990:820,h:ticker?70:130,in:'slideU',out:'slideU',dur:.6},media:[],
 t:{font:'Barlow Condensed',size:ticker?40:64,color:'#ffffff',align:'left',bold:true,italic:false,x:ticker?230:120,y:ticker?1002:846,w:ticker?1650:1680,in:'slideL',out:'fade',dur:.5,hold:4},
 lines:['बड़ी खबर · News update','देश और दुनिया की खबरें','Breaking news from the newsroom','आप देख रहे हैं NewsPlay'],rows:[],col:0,hdr:false,speed:140,sep:'     ●     '};
}
export function defaults(){return {version:1,comps:[sample(),sample('ticker')],logo:{m:null,x:1640,y:40,w:240,op:100,fps:25},air:{1:[],2:[]},logoAir:{1:false,2:false},port:1};}
const number=(n,d,min,max)=>Number.isFinite(+n)?Math.min(max,Math.max(min,+n)):d;
const color=(s,d)=>/^#[0-9a-f]{6}$/i.test(s)?s:d;
export function normalize(raw){
 const d=sample(raw?.type==='ticker'?'ticker':'headline'),c={...d,...raw};
 c.id=typeof c.id==='string'?c.id:uid(); c.name=String(c.name??'').slice(0,200); c.type=d.type;
 c.band={...d.band,...raw?.band}; c.t={...d.t,...raw?.t};
 for(const [k,min,max] of [['y',-1080,2160],['h',1,1080],['op',0,100],['dur',0,30]])c.band[k]=number(c.band[k],d.band[k],min,max);
 c.band.color=color(c.band.color,d.band.color); c.t.color=color(c.t.color,d.t.color);
 for(const [k,min,max] of [['x',-1920,3840],['y',-1080,2160],['w',1,3840],['size',8,300],['dur',0,30],['hold',.1,3600]])c.t[k]=number(c.t[k],d.t[k],min,max);
 for(const group of [c.band,c.t])for(const key of ['in','out'])if(!animations.includes(group[key]))group[key]='fade';
 c.speed=number(c.speed,140,10,3000);c.sep=String(c.sep??d.sep).slice(0,100);
 c.lines=Array.isArray(c.lines)?c.lines.map(String).map(x=>x.trim()).filter(Boolean).slice(0,10000):[];
 c.rows=Array.isArray(c.rows)?c.rows.slice(0,10000).filter(Array.isArray).map(r=>r.map(String)):[];
 c.media=Array.isArray(c.media)?c.media.filter(m=>['img','seq','video'].includes(m.kind)&&Array.isArray(m.ids)).map(m=>({...m,ids:m.ids.filter(x=>typeof x==='string'),x:number(m.x,40,-1920,3840),y:number(m.y,c.band.y+10,-1080,2160),w:number(m.w,200,1,3840),fps:number(m.fps,25,1,60)})):[];
 return c;
}
export function load(){try{const s=JSON.parse(localStorage.getItem('newsplay-cg'));if(!s||!Array.isArray(s.comps)||!s.logo) return defaults();s.comps=s.comps.map(normalize);s.air??={1:[],2:[]};s.logoAir??={1:false,2:false};return s;}catch{return defaults();}}
export function save(s){localStorage.setItem('newsplay-cg',JSON.stringify(s));}
const db=new Promise((resolve,reject)=>{const req=indexedDB.open('newsplay-cg-media',1);req.onupgradeneeded=()=>req.result.createObjectStore('media');req.onsuccess=()=>resolve(req.result);req.onerror=()=>reject(req.error);});
export async function put(blob,id=uid()){const database=await db;await new Promise((resolve,reject)=>{const tx=database.transaction('media','readwrite');tx.objectStore('media').put(blob,id);tx.oncomplete=resolve;tx.onerror=()=>reject(tx.error);});return id;}
export async function get(id){const database=await db;return new Promise((resolve,reject)=>{const req=database.transaction('media').objectStore('media').get(id);req.onsuccess=()=>resolve(req.result);req.onerror=()=>reject(req.error);});}
export async function prune(state){const keep=new Set([...(state.logo.m?.ids??[]),...state.comps.flatMap(c=>c.media.flatMap(m=>m.ids))]);const database=await db;await new Promise((resolve,reject)=>{const tx=database.transaction('media','readwrite'),request=tx.objectStore('media').openCursor();request.onsuccess=()=>{const cursor=request.result;if(!cursor)return;if(!keep.has(cursor.key))cursor.delete();cursor.continue();};tx.oncomplete=resolve;tx.onerror=()=>reject(tx.error);});}
export async function media(files,y=830){
 const ordered=[...files].sort((a,b)=>a.name.localeCompare(b.name,undefined,{numeric:true}));if(!ordered.length)return null;
 if(ordered.some(f=>f.size>100*1024*1024))throw Error('Media file exceeds 100 MB');
 const video=/\.(webm|mov|mp4)$/i.test(ordered[0].name);if(ordered.length>1&&ordered.some(f=>/\.(webm|mov|mp4)$/i.test(f.name)))throw Error('Choose one video or an image sequence');
 return {kind:video?'video':ordered.length>1?'seq':'img',ids:await Promise.all(ordered.map(f=>put(f))),name:ordered[0].name,x:40,y,w:200,fps:25};
}
export async function exportBundle(state){const ids=new Set([...(state.logo.m?.ids??[]),...state.comps.flatMap(c=>c.media.flatMap(m=>m.ids))]);const blobs={};for(const id of ids){const blob=await get(id);if(blob)blobs[id]=await new Promise((r,j)=>{const f=new FileReader();f.onload=()=>r(f.result);f.onerror=j;f.readAsDataURL(blob);});}return {version:1,state,blobs};}
export async function importBundle(bundle){if(bundle.version!==1||!Array.isArray(bundle.state?.comps))throw Error('Invalid CG bundle');const mapping={};for(const [id,value] of Object.entries(bundle.blobs??{})){if(typeof value!=='string'||!/^data:(image|video)\/[\w.+-]+;base64,/.test(value))throw Error('Invalid media data');const blob=await(await fetch(value)).blob();mapping[id]=await put(blob);}const s=clone(bundle.state);s.comps=s.comps.map(normalize);for(const m of [s.logo?.m,...s.comps.flatMap(c=>c.media)].filter(Boolean))m.ids=m.ids.map(id=>mapping[id]??id);s.air={1:[],2:[]};s.logoAir={1:false,2:false};s.port=1;return s;}
