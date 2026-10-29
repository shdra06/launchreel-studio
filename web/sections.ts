import type {Rect,Shot} from './project.ts';
import {snapToEdges} from './cutout.ts';

export type Tool='section'|'callout'|'spotlight'|'outline'|'zoom'|'crop';
type Kind='layer'|'callout'|'highlight'|'crop';
const LABEL:Record<Tool,string>={section:'Drag over the app to mark a section. It lifts off the screen in 3D.',callout:'Click the spot the label should point at.',spotlight:'Drag over the area to keep lit while the rest dims.',outline:'Drag over the area to ring with your accent colour.',zoom:'Drag over the area the camera should fly in to.',crop:'Drag around the part of the footage to show, for example just the clipboard. The edges snap to the app\'s own borders for a clean cutout; hold Alt to place them freely.'};

/**
 * Marks parts of the real app's picture: sections (cut from the app's own pixels and lifted off the screen),
 * callouts and focus regions. Everything is stored as fractions of the screen, so it follows the footage at any size.
 */
export class SectionsEditor {
  private tool:Tool='section';private picture?:HTMLCanvasElement;private drag?:{x:number;y:number;x2:number;y2:number};private pixels?:ImageData;private ctx:CanvasRenderingContext2D;
  constructor(private dialog:HTMLDialogElement,private canvas:HTMLCanvasElement,private list:HTMLElement,private hint:HTMLElement,private labelInput:HTMLInputElement,private shot:()=>Shot,
    private actions:{add:(tool:Tool,area:Rect,label:string)=>void;remove:(kind:Kind,id:string)=>void}){
    this.ctx=canvas.getContext('2d')!;
    dialog.querySelectorAll<HTMLButtonElement>('[data-tool]').forEach(button=>button.onclick=()=>{this.tool=button.dataset.tool as Tool;dialog.querySelectorAll('[data-tool]').forEach(b=>b.classList.toggle('active',b===button));hint.textContent=LABEL[this.tool];});
    const point=(e:PointerEvent)=>{const r=canvas.getBoundingClientRect();return {x:Math.max(0,Math.min(1,(e.clientX-r.left)/r.width)),y:Math.max(0,Math.min(1,(e.clientY-r.top)/r.height))};};
    canvas.addEventListener('pointerdown',e=>{if(!this.picture)return;canvas.setPointerCapture(e.pointerId);const p=point(e);this.drag={x:p.x,y:p.y,x2:p.x,y2:p.y};this.draw();});
    canvas.addEventListener('pointermove',e=>{if(!this.drag)return;const p=point(e);this.drag.x2=p.x;this.drag.y2=p.y;this.draw();});
    canvas.addEventListener('pointerup',e=>{const d=this.drag;this.drag=undefined;if(!d)return;let x=Math.min(d.x,d.x2),y=Math.min(d.y,d.y2),w=Math.abs(d.x2-d.x),h=Math.abs(d.y2-d.y);
      if((this.tool==='crop'||this.tool==='section')&&!e.altKey&&this.pixels&&w>=.015&&h>=.015)[x,y,w,h]=snapToEdges(this.pixels.data,this.pixels.width,this.pixels.height,[x,y,w,h]);
      const label=labelInput.value.trim();
      if(this.tool==='callout')this.actions.add('callout',[x,y,0,0],label||'New feature');
      else if(w>=.015&&h>=.015)this.actions.add(this.tool,[x,y,Math.min(w,1-x),Math.min(h,1-y)],label||(this.tool==='section'?`Section ${this.shot().layers.filter(l=>l.attach==='screen').length+1}`:''));
      labelInput.value='';this.refresh();});
  }
  open(picture:HTMLCanvasElement|undefined){
    this.picture=picture;this.pixels=picture?.getContext('2d',{willReadFrequently:true})?.getImageData(0,0,picture.width,picture.height);(this.dialog.querySelector('#sections-empty') as HTMLElement).hidden=!!picture;this.canvas.hidden=!picture;
    if(picture){const scale=Math.min(1,1600/picture.width);this.canvas.width=Math.round(picture.width*scale);this.canvas.height=Math.round(picture.height*scale);}
    this.hint.textContent=LABEL[this.tool];this.refresh();if(!this.dialog.open)this.dialog.showModal();
  }
  refresh(){this.draw();this.renderList();}
  private draw(){const {ctx,canvas}=this;if(!this.picture)return;const W=canvas.width,H=canvas.height,shot=this.shot();ctx.clearRect(0,0,W,H);ctx.drawImage(this.picture,0,0,W,H);
    const font=Math.max(12,Math.round(W/90));ctx.font=`600 ${font}px "Segoe UI",sans-serif`;ctx.lineWidth=Math.max(2,W/600);
    const tag=(text:string,x:number,y:number,color:string)=>{const w=ctx.measureText(text).width+font;ctx.fillStyle=color;ctx.fillRect(x,y-font*1.5,w,font*1.5);ctx.fillStyle='#111';ctx.fillText(text,x+font/2,y-font*.4);};
    if(shot.crop){const [x,y,w,h]=shot.crop;ctx.fillStyle='rgba(0,0,0,.55)';ctx.fillRect(0,0,W,y*H);ctx.fillRect(0,(y+h)*H,W,H-(y+h)*H);ctx.fillRect(0,y*H,x*W,h*H);ctx.fillRect((x+w)*W,y*H,W-(x+w)*W,h*H);ctx.setLineDash([10,6]);ctx.strokeStyle='#fff';ctx.strokeRect(x*W,y*H,w*W,h*H);ctx.setLineDash([]);tag('Framed',x*W,y*H+font*1.5,'#fff');}
    for(const h of shot.highlights){const [x,y,w,hh]=h.rect;ctx.setLineDash([8,6]);ctx.strokeStyle='#8fd3ff';ctx.strokeRect(x*W,y*H,w*W,hh*H);ctx.setLineDash([]);tag(`${h.style} ${h.start.toFixed(1)}–${h.end.toFixed(1)}s`,x*W,y*H+hh*H+font*1.5,'#8fd3ff');}
    for(const l of shot.layers.filter(l=>l.attach==='screen')){const [x,y,w,h]=l.crop;ctx.fillStyle=shot.accent+'22';ctx.fillRect(x*W,y*H,w*W,h*H);ctx.strokeStyle=shot.accent;ctx.strokeRect(x*W,y*H,w*W,h*H);tag(l.name,x*W,y*H+font*1.5,shot.accent);}
    for(const c of shot.callouts){ctx.fillStyle=shot.accent;ctx.beginPath();ctx.arc(c.x*W,c.y*H,font*.55,0,Math.PI*2);ctx.fill();tag(c.text,c.x*W+font,c.y*H+font*.75,'#f3f5f0');}
    if(this.drag){const d=this.drag;ctx.strokeStyle='#fff';ctx.setLineDash([6,4]);ctx.strokeRect(Math.min(d.x,d.x2)*W,Math.min(d.y,d.y2)*H,Math.abs(d.x2-d.x)*W,Math.abs(d.y2-d.y)*H);ctx.setLineDash([]);}}
  private renderList(){const shot=this.shot();const rows:HTMLElement[]=[];
    const row=(icon:string,text:string,kind:Kind,id:string)=>{const r=document.createElement('div');r.className='item-row';const label=document.createElement('span');label.textContent=`${icon} ${text}`;const remove=document.createElement('button');remove.className='quiet';remove.textContent='Remove';remove.type='button';remove.onclick=()=>{this.actions.remove(kind,id);this.refresh();};r.append(label,remove);rows.push(r);};
    if(shot.crop)row('✂','Framed part of the footage','crop','crop');
    shot.layers.filter(l=>l.attach==='screen').forEach(l=>row('▣',l.name,'layer',l.id));shot.callouts.forEach(c=>row('●',c.text,'callout',c.id));shot.highlights.forEach(h=>row(h.style==='zoom'?'⌕':h.style==='outline'?'▢':'◐',`${h.style} · ${h.start.toFixed(1)}–${h.end.toFixed(1)} s`,'highlight',h.id));
    this.list.replaceChildren(...rows);}
}
