import {clone,normalize,media,animations,sample} from './store.js';
import {element,stage,mount} from './engine.js';
import {readRows,linesFromRows} from './parse.js';
export const fonts=['Barlow Condensed','Anton','Noto Sans Devanagari','Arial','Georgia'];
export const accepted='.png,.apng,.webp,.gif,.svg,.jpg,.jpeg,.webm,.mov,.mp4';
export function button(parent,text,click,cls=''){const b=element('button',cls,parent);b.type='button';b.textContent=text;b.onclick=click;return b;}
export function field(parent,label,value,change,options={}){const wrap=element('label','field',parent);element('span','',wrap).textContent=label;const input=element(options.values?'select':'input','',wrap);
 if(options.values){for(const value of options.values){const option=element('option','',input);option.value=value;option.textContent=value;}input.value=value;}
 else{input.type=options.type??(typeof value==='number'?'number':'text');if(input.type==='checkbox')input.checked=value;else input.value=value;input.step=options.step??'any';if(options.min!==undefined)input.min=options.min;if(options.max!==undefined)input.max=options.max;}
 input.oninput=()=>{if(input.type==='number'&&input.value==='')return;change(input.type==='checkbox'?input.checked:input.type==='number'?+input.value:input.value);};return input;
}
export function designer(original,onSave){
 let comp=clone(original),controller,timer;const overlay=element('section','designer',document.body),left=element('div','designer-left',overlay),box=element('div','preview-box',left),host=stage(box),actions=element('div','actions',left),error=element('p','error',left),right=element('div','properties',overlay);
 const close=()=>{clearTimeout(timer);controller?.kill();overlay.remove();};
 const refresh=()=>{clearTimeout(timer);timer=setTimeout(()=>{controller?.kill();controller=mount(normalize(comp),host);},200);};
 button(actions,'Replay animation',refresh);button(actions,'Save comp',()=>{if(!comp.name.trim()){error.textContent='Comp ka naam likho';return;}try{onSave(normalize(comp));close();}catch(e){error.textContent=e.message;}},'primary');button(actions,'Close',close);
 const section=title=>{const s=element('fieldset','',right);element('legend','',s).textContent=title;return s;};
 function build(){right.replaceChildren();const general=section('Composition');field(general,'Name',comp.name,v=>{comp.name=v;});field(general,'Type',comp.type,v=>{comp.type=v;if(!comp.rows.length&&comp.lines.length<4)comp.lines=sample(v).lines;build();refresh();},{values:['headline','ticker']});
 const band=section('Band / background');for(const [key,label]of [['color','Colour'],['op','Opacity %'],['y','Y'],['h','Height'],['in','Animation in'],['out','Animation out'],['dur','Duration seconds']])field(band,label,comp.band[key],v=>{comp.band[key]=v;refresh();},{type:key==='color'?'color':undefined,values:['in','out'].includes(key)?animations:undefined});
 const layers=section('Media layers');const files=element('input','',layers);files.type='file';files.multiple=true;files.accept=accepted;files.onchange=async()=>{try{const m=await media(files.files,comp.band.y+10);if(m)comp.media.push(m);build();refresh();}catch(e){error.textContent=e.message;}};
 for(const [index,m]of comp.media.entries()){const card=element('div','layer',layers);element('strong','',card).textContent=m.name+' · '+m.kind;for(const key of ['x','y','w','fps'])field(card,key.toUpperCase(),m[key],v=>{m[key]=v;refresh();});button(card,'Remove',()=>{comp.media.splice(index,1);build();refresh();});}
 const text=section('Text');for(const [key,label]of [['font','Font'],['size','Size'],['color','Colour'],['align','Alignment'],['bold','Bold'],['italic','Italic'],['x','X'],['y','Y'],['w','Box width'],['in','Animation in'],['out','Animation out'],['dur','Animation seconds']])field(text,label,comp.t[key],v=>{comp.t[key]=v;refresh();},{type:['bold','italic'].includes(key)?'checkbox':key==='color'?'color':undefined,values:key==='font'?fonts:key==='align'?['left','center','right']:['in','out'].includes(key)?animations:undefined});
 if(comp.type==='headline')field(text,'Hold seconds',comp.t.hold,v=>{comp.t.hold=v;refresh();});else{field(text,'Speed px/sec',comp.speed,v=>{comp.speed=v;refresh();});field(text,'Separator',comp.sep,v=>{comp.sep=v;refresh();});}
 const source=section('Text source');const load=element('input','',source);load.type='file';load.accept='.txt,.csv,.xlsx,.xls';load.onchange=async()=>{try{comp.rows=await readRows(load.files[0]);comp.lines=linesFromRows(comp.rows,comp.col,comp.hdr);build();refresh();}catch(e){error.textContent='File read nahi hui: '+e.message;}};
 const columns=Math.max(0,...comp.rows.map(r=>r.length));field(source,'Column',String(comp.col),v=>{comp.col=+v;comp.lines=linesFromRows(comp.rows,comp.col,comp.hdr);build();refresh();},{values:Array.from({length:columns+1},(_,i)=>String(i))});field(source,'First row is header',comp.hdr,v=>{comp.hdr=v;comp.lines=linesFromRows(comp.rows,comp.col,comp.hdr);build();refresh();},{type:'checkbox'});
 const lines=element('textarea','lines',source);lines.value=comp.lines.join('\n');lines.placeholder='One headline / ticker item per line';lines.oninput=()=>{comp.lines=lines.value.split('\n').map(x=>x.trim()).filter(Boolean);refresh();};
 }build();refresh();return close;
}


