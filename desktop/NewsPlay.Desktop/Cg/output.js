import {load} from './store.js';import {stage,renderer} from './engine.js';
const port=+(new URLSearchParams(location.search).get('port')??1),host=stage(document.querySelector('#preview')),render=renderer(host);
const sync=()=>render.sync(load(),port);window.addEventListener('storage',e=>{if(e.key==='newsplay-cg')sync();});window.addEventListener('pagehide',()=>render.kill());sync();
