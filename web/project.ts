export type Vec3 = [number, number, number];
export type Rect = [number, number, number, number];
export type SourceKind = 'image'|'video'|'audio'|'model';
export const DEVICES = ['desktop','laptop','tablet','phone','floating','none','panel','title'] as const;
export const STAGES = ['studio','desk','workspace','abstract','gradient','void','aurora','atelier','pastel'] as const;
export const FINISHES = ['silver','graphite','black','white'] as const;
export const LIGHTING = ['studio','soft','dramatic','sunset','neon'] as const;
export const EASINGS = ['smooth','linear','ease-out','dramatic'] as const;
export const TITLE_LAYOUTS = ['top-left','bottom-left','center','top-center','none'] as const;
export const MOTIONS = ['hero','reveal','orbit','closeup','push','tilt','dolly','still','swoop','rise'] as const;
export const LAYER_MOTIONS = ['pop','float','slide','none'] as const;
export const HIGHLIGHT_STYLES = ['spotlight','outline','zoom'] as const;
export const TRANSITIONS = ['cut','fade'] as const;
export const TITLE_MOTIONS = ['rise','fade','pop'] as const;
export type TitleMotion = typeof TITLE_MOTIONS[number];
export type Device = typeof DEVICES[number]; export type Stage = typeof STAGES[number]; export type Finish = typeof FINISHES[number];
export type Lighting = typeof LIGHTING[number]; export type Easing = typeof EASINGS[number]; export type TitleLayout = typeof TITLE_LAYOUTS[number];
export type Motion = typeof MOTIONS[number]; export type LayerMotion = typeof LAYER_MOTIONS[number]; export type HighlightStyle = typeof HIGHLIGHT_STYLES[number]; export type Transition = typeof TRANSITIONS[number];
export interface Asset { id: string; name: string; kind: SourceKind; url: string; file: string; duration?: number; width?: number; height?: number; recordedFrames?: number; uniqueFrames?: number; recordedFps?: number }
export interface CameraKey { time: number; position: Vec3; target: Vec3; fov: number }
/** A layer either floats freely in the scene ('world') or is a section of the app's own screen ('screen'), cut out by its crop and lifted off the display. */
export interface Layer { id: string; name: string; assetId?: string; position: Vec3; rotation: Vec3; scale: number; opacity: number; crop: Rect; liveElementId?: string; attach: 'screen'|'world'; motion: LayerMotion; delay: number }
/** A label pointing at a spot on the app's screen. x and y are fractions of the screen from its top-left corner. */
export interface Callout { id: string; text: string; x: number; y: number; delay: number }
/** Draws attention to a region of the screen between start and end (seconds into the shot). */
export interface Highlight { id: string; rect: Rect; start: number; end: number; style: HighlightStyle }
export interface Shot {
  id: string; name: string; duration: number; device: Device; stage: Stage; finish: Finish; lighting: Lighting; lightIntensity: number; easing: Easing;
  assetId?: string; sourceStart: number; title: string; subtitle: string; titleLayout: TitleLayout; titleScale: number; titleColor: string; accent: string; background: string;
  transition: Transition; titleMotion: TitleMotion; explode: number;
  /** 0–1: how soft the world beyond the app gets, like a fast lens. The app itself always stays sharp. */
  depthOfField: number;
  /** 0–1: removes the colour cast a translucent (acrylic/mica) app picks up from the desktop behind it. */
  cleanTint: number;
  /** Part of the footage to show, as fractions from its top-left: frame just the clipboard instead of the whole screen. */
  crop?: Rect; camera: CameraKey[]; layers: Layer[]; callouts: Callout[]; highlights: Highlight[]; deviceRotation: Vec3; devicePosition: Vec3;
}
export interface Project { version: 1; name: string; width: number; height: number; fps: 30|60; assets: Asset[]; shots: Shot[]; audioId?: string; audioVolume: number }
export const uid = () => crypto.randomUUID();
const clamp=(v:number,min=0,max=1)=>Math.max(min,Math.min(max,v));
const lerp=(a:number,b:number,t:number)=>a+(b-a)*t;

// ── Geometry shared by the editor, presets and AI planning ─────────────────────────────────────────────────────
/** Largest size with the media's aspect ratio that fits the screen, so real UI is never stretched. */
export function containFit(mediaAspect:number,width:number,height:number):{width:number;height:number}{if(!(mediaAspect>0)||!Number.isFinite(mediaAspect))return {width,height};return mediaAspect>width/height?{width,height:width/mediaAspect}:{width:height*mediaAspect,height};}
/** Display area of each device. Frameless devices take the shape of the footage itself. */
export function deviceScreen(device:Device,aspect:number):{width:number;height:number}{
  const a=aspect>0&&Number.isFinite(aspect)?aspect:16/10;
  switch(device){case 'laptop':return {width:5.4,height:3.4};case 'tablet':return {width:5,height:3.55};case 'phone':return {width:2.05,height:4.25};
    case 'floating':{const width=Math.min(6.2,4.2*a);return {width,height:width/a};}
    case 'none':case 'panel':case 'title':{const width=Math.min(7.2,4.8*a);return {width,height:width/a};}
    default:return {width:5.9,height:3.7};}
}
/** The app's picture on a device: the device screen with the footage contain-fitted inside it. */
export function screenFit(device:Device,aspect:number){const area=deviceScreen(device,aspect);return containFit(aspect,area.width,area.height);}
/** Camera distance at which a width × height rectangle fills `fill` of the frame. */
export function frameDistance(width:number,height:number,fovDegrees:number,aspect:number,fill=.9):number{const half=Math.tan(fovDegrees*Math.PI/360);return Math.max(height/2/half,width/2/(half*aspect))/fill;}
/** Outer size of the whole device (body, bezel and stand), for framing it with its surroundings. */
export function deviceExtent(device:Device,aspect:number):{width:number;height:number}{const s=deviceScreen(device,aspect);switch(device){case 'desktop':return {width:s.width+.22,height:s.height+1.05};case 'laptop':return {width:s.width+.42,height:s.height+.7};case 'tablet':return {width:s.width+.36,height:s.height+.36};case 'phone':return {width:s.width+.22,height:s.height+.22};default:return s;}}
export function mediaAspect(asset:Asset|undefined):number{return asset?.width&&asset.height?asset.width/asset.height:16/10;}

// ── Camera ────────────────────────────────────────────────────────────────────────────────────────────────────
/** width/height: the app's picture on screen. device: the whole device, when it differs. */
export interface Frame { width: number; height: number; aspect: number; device?: { width: number; height: number }; headline?: TitleLayout }
/**
 * Camera moves scaled to the device. "hero" fills the frame with the app itself; every other move shows the whole
 * device in its environment and keeps the headline's corner of the frame clear.
 */
export function cameraPreset(motion:string,duration:number,frame:Frame={width:5.9,height:3.7,aspect:16/9,device:{width:6.12,height:4.75}}):CameraKey[] {
  const fov=34,portrait=frame.aspect<1,device=frame.device??frame;
  const hero=frameDistance(frame.width,frame.height,fov,frame.aspect,.92),d=frameDistance(device.width,device.height,fov,frame.aspect,portrait?.62:.58);
  // Compose against the frame, not the device: the device sits right of centre (low in portrait), leaving the
  // headline's side of the picture clear whatever the device's size.
  const visible=2*d*Math.tan(fov*Math.PI/360);
  const room=frame.headline??'top-left';
  // Make room where the headline is: beside it for left-aligned titles, under it for centred ones.
  const t:Vec3=room==='none'?[0,0,0]:portrait||room==='top-center'||room==='center'?[0,visible*(room==='center'?0:room.startsWith('bottom')?-.1:.1),0]:[-visible*frame.aspect*.17,device.height*(room==='bottom-left'?-.04:.05),0];
  const at=(x:number,y:number,z:number):Vec3=>[t[0]+x,t[1]+y,t[2]+z];
  const key=(time:number,position:Vec3,target:Vec3=t):CameraKey=>({time,position,target,fov});
  switch(motion){
    case 'hero':return [key(0,[0,hero*.02,hero*1.04],[0,0,0]),key(duration,[0,0,hero*.98],[0,0,0])];
    case 'orbit':return [key(0,at(-d*.62,d*.2,d*.98)),key(duration,at(d*.6,d*.12,d*1.0))];
    case 'closeup':{const focus:Vec3=[0,frame.height*.12,0];return [key(0,at(d*.26,d*.12,d*.9)),key(duration,[hero*.08,frame.height*.14,hero*.62],focus)];}
    case 'push':return [key(0,at(0,d*.05,d*1.25)),key(duration,at(0,d*.01,d*.84))];
    case 'tilt':return [key(0,at(0,d*.85,d*.6)),key(duration,at(0,d*.05,d))];
    case 'dolly':return [key(0,at(-d*.45,d*.04,d),[t[0]-.4,t[1],0]),key(duration,at(d*.45,d*.04,d),[t[0]+.4,t[1],0])];
    case 'still':return [key(0,at(0,d*.04,d)),key(duration,at(0,d*.04,d))];
    case 'swoop':return [key(0,at(-d*.85,-d*.12,d*.55)),key(duration*.55,at(-d*.18,d*.04,d*.92)),key(duration,at(d*.06,d*.02,d*.97))];
    case 'rise':return [key(0,at(d*.08,-d*.55,d*.85),[t[0],t[1]+.6,0]),key(duration,at(0,d*.03,d*.95))];
    default:return [key(0,at(d*.42,d*.3,d*1.25)),key(duration,at(-d*.06,d*.05,d))];
  }
}
export function ease(t:number,easing:Easing='smooth'):number{t=clamp(t);switch(easing){case 'linear':return t;case 'ease-out':return 1-(1-t)**3;case 'dramatic':return t<.5?16*t**5:1-(-2*t+2)**5/2;default:return t*t*(3-2*t);}}
export function cameraAt(keys:CameraKey[],time:number,easing:Easing='smooth'):CameraKey {
  const sorted=[...keys].sort((a,b)=>a.time-b.time);if(!sorted.length)throw Error('Camera needs a keyframe.');
  if(time<=sorted[0].time)return structuredClone(sorted[0]);
  if(time>=sorted[sorted.length-1].time)return structuredClone(sorted[sorted.length-1]);
  for(let i=1;i<sorted.length;i++){const a=sorted[i-1],b=sorted[i];if(time<=b.time){const t=ease((time-a.time)/(b.time-a.time||1),easing);return {time,position:a.position.map((v,j)=>lerp(v,b.position[j],t)) as Vec3,target:a.target.map((v,j)=>lerp(v,b.target[j],t)) as Vec3,fov:lerp(a.fov,b.fov,t)};}}
  return structuredClone(sorted[sorted.length-1]);
}

// ── Timing of animated elements (shared with the cinematic renderer where noted) ────────────────────────────────
/** Title fade in and out. Must match CinematicComposite.TitleFades in the desktop host. */
export function titleFades(duration:number){return {fadeIn:Math.min(.6,duration*.3),fadeOut:Math.min(.4,duration*.2)};}
export function titleOpacity(local:number,duration:number):number{const {fadeIn,fadeOut}=titleFades(duration);const t=clamp(local,0,duration);return clamp(Math.min(t/fadeIn,(duration-t)/fadeOut));}
const easeOutBack=(t:number)=>{const c=1.70158;return 1+(c+1)*(t-1)**3+c*(t-1)**2;};
/**
 * Where a layer is at a moment in the shot. Screen sections start flush with the app's screen, so the first frame
 * shows the untouched UI, then lift off by the shot's explode amount one after another. Mirrored in blender_render.py.
 */
export function layerMotion(local:number,duration:number,index:number,layer:Pick<Layer,'motion'|'delay'|'attach'>,explode:number):{x:number;z:number;opacity:number;scale:number}{
  const screen=layer.attach==='screen',depth=screen?explode*.75*(1+(index%4)*.18):0;
  const start=Math.min(duration*.6,layer.delay+index*.12),t=clamp((local-start)/.9),out=1-(1-t)**3;
  switch(layer.motion){
    case 'pop':return {x:0,z:depth*easeOutBack(t),opacity:1,scale:1+.04*Math.sin(t*Math.PI)};
    case 'slide':return {x:(1-out)*-.9,z:depth*out,opacity:screen?1:out,scale:1};
    case 'float':return {x:0,z:depth*out+Math.sin(local/duration*Math.PI)*.35*(screen?explode*.4:1),opacity:1,scale:1};
    default:return {x:0,z:depth,opacity:1,scale:1};
  }
}
export function calloutOpacity(local:number,duration:number,delay:number):number{return clamp(Math.min((local-delay)/.35,(duration-local)/.3));}
/** 0 → 1 → 0 across a highlight's time span, with ramps of up to half a second. */
export function highlightWeight(local:number,h:Pick<Highlight,'start'|'end'>):number{const ramp=Math.min(.5,(h.end-h.start)/3);return ramp<=0?0:ease(Math.min((local-h.start)/ramp,(h.end-local)/ramp));}
export function transitionOpacity(local:number,transition:Transition):number{return transition==='fade'?1-ease(local/.45,'ease-out'):0;}

// ── Project ───────────────────────────────────────────────────────────────────────────────────────────────────
export function newShot(name='Feature reveal'):Shot {
  return {id:uid(),name,duration:6,device:'desktop',stage:'studio',finish:'silver',lighting:'studio',lightIntensity:1,easing:'smooth',sourceStart:0,title:'Your app.\nIn its best light.',subtitle:'INTRODUCING',titleLayout:'top-left',titleScale:1,titleMotion:'rise',titleColor:'#f5f6f3',accent:'#d5f58b',background:'#191c22',transition:'cut',explode:.6,depthOfField:0,cleanTint:0,camera:cameraPreset('reveal',6),layers:[],callouts:[],highlights:[],deviceRotation:[0,0,0],devicePosition:[0,0,0]};
}
export function newProject():Project {
  const a=newShot('The introduction');a.transition='fade';
  const b=newShot('The details'); b.title='Every detail.\nExactly as it is.'; b.camera=cameraPreset('closeup',6);
  const c=newShot('The finish');c.title='Made for your flow.';c.subtitle='AVAILABLE NOW';c.camera=cameraPreset('orbit',6);c.stage='abstract';
  return {version:1,name:'Launch film',width:1920,height:1080,fps:30,assets:[],shots:[a,b,c],audioVolume:.65};
}
/** Turns a rectangle of the app's screen into a section layer that sits exactly over those pixels. */
export function sectionLayer(name:string,area:Rect,options:{motion?:LayerMotion;delay?:number;assetId?:string}={}):Layer{
  return {id:uid(),name,assetId:options.assetId,position:[0,0,0],rotation:[0,0,0],scale:1,opacity:1,crop:[...area] as Rect,attach:'screen',motion:options.motion??'pop',delay:options.delay??.2};
}
export const totalDuration=(p:Project)=>p.shots.reduce((s,x)=>s+x.duration,0);
export function shotAt(p:Project,time:number):{shot:Shot;local:number;index:number;start:number} {
  let start=0;
  for(let i=0;i<p.shots.length;i++){const shot=p.shots[i]; if(time<start+shot.duration||i===p.shots.length-1)return {shot,local:Math.max(0,Math.min(shot.duration,time-start)),index:i,start};start+=shot.duration;}
  throw Error('A project must contain at least one shot.');
}
export const shotStart=(p:Project,index:number)=>p.shots.slice(0,index).reduce((n,s)=>n+s.duration,0);
export function frameFor(shot:Shot,project:Project):Frame{const aspect=mediaAspect(project.assets.find(a=>a.id===shot.assetId))*(shot.crop?shot.crop[2]/shot.crop[3]:1);return {...screenFit(shot.device,aspect),aspect:project.width/project.height,device:deviceExtent(shot.device,aspect),headline:shot.titleLayout};}

// ── Validation ────────────────────────────────────────────────────────────────────────────────────────────────
function object(value:unknown):Record<string,unknown>{if(!value||typeof value!=='object'||Array.isArray(value))throw Error('Expected a JSON object.');return value as Record<string,unknown>;}
function finite(value:unknown,min:number,max:number,label:string):number {if(typeof value!=='number'||!Number.isFinite(value)||value<min||value>max)throw Error(`${label} must be ${min}–${max}.`);return value;}
function str(value:unknown,max:number,label:string):string {if(typeof value!=='string'||value.length>max)throw Error(`${label} must be text up to ${max} characters.`);return value;}
function color(value:unknown):string {if(typeof value!=='string'||!/^#[0-9a-f]{6}$/i.test(value))throw Error('Colors must use #RRGGBB.');return value;}
function vector(value:unknown):Vec3 {if(!Array.isArray(value)||value.length!==3)throw Error('Expected a 3D vector.');return value.map(v=>finite(v,-10000,10000,'Coordinate')) as Vec3;}
function choice<T extends readonly string[]>(value:unknown,options:T,label:string):T[number]{if(typeof value!=='string'||!options.includes(value))throw Error(`${label} must be one of: ${options.join(', ')}.`);return value as T[number];}
function area(value:unknown,label:string):Rect{if(!Array.isArray(value)||value.length!==4)throw Error(`${label} must be [x, y, width, height] as fractions.`);const [x,y,w,h]=value.map(v=>finite(v,0,1,label));if(w<=0||h<=0||x+w>1.00001||y+h>1.00001)throw Error(`${label} must stay inside the screen.`);return [x,y,w,h];}
/** Fills fields added after a project was saved, so older projects keep opening. */
function withDefaults(raw:Record<string,unknown>){
  const base=newShot() as unknown as Record<string,unknown>;
  for(const key of ['finish','lighting','lightIntensity','easing','titleLayout','titleScale','titleColor','transition','titleMotion','explode','depthOfField','cleanTint'])if(raw[key]===undefined)raw[key]=base[key];
  for(const key of ['callouts','highlights'])if(raw[key]===undefined)raw[key]=[];
  for(const layer of Array.isArray(raw.layers)?raw.layers:[])if(layer&&typeof layer==='object'){const l=layer as Record<string,unknown>;l.attach??='world';l.motion??='float';l.delay??=0;}
}
export function validateProject(input:unknown):Project {
  const p=structuredClone(object(input));if(p.version!==1)throw Error('Unsupported project version.');str(p.name,200,'Project name');
  finite(p.width,256,4096,'Width');finite(p.height,256,4096,'Height');if(!Number.isInteger(p.width)||!Number.isInteger(p.height)||Number(p.width)%2||Number(p.height)%2)throw Error('Dimensions must be even whole numbers.');
  if(p.fps!==30&&p.fps!==60)throw Error('Frame rate must be 30 or 60.');finite(p.audioVolume,0,1,'Audio volume');
  if(!Array.isArray(p.assets)||p.assets.length>200)throw Error('Too many assets.');
  const ids=new Set<string>();for(const raw of p.assets){const a=object(raw);str(a.id,100,'Asset ID');if(ids.has(String(a.id)))throw Error('Duplicate asset ID.');ids.add(String(a.id));str(a.name,300,'Asset name');str(a.file,500,'Asset file');str(a.url,2000,'Asset URL');if(!['image','video','audio','model'].includes(String(a.kind)))throw Error('Unknown asset kind.');if(a.duration!==undefined)finite(a.duration,0,86400,'Asset duration');for(const key of ['width','height'])if(a[key]!==undefined)finite(a[key],1,65536,'Asset size');for(const key of ['recordedFrames','uniqueFrames','recordedFps'])if(a[key]!==undefined)finite(a[key],0,1e9,'Recording detail');}
  if(!Array.isArray(p.shots)||!p.shots.length||p.shots.length>100)throw Error('A project needs 1–100 shots.');
  const shotIds=new Set<string>();for(const raw of p.shots){const s=object(raw);withDefaults(s);str(s.id,100,'Shot ID');if(shotIds.has(String(s.id)))throw Error('Duplicate shot ID.');shotIds.add(String(s.id));str(s.name,120,'Shot name');str(s.title,500,'Title');str(s.subtitle,300,'Subtitle');const duration=finite(s.duration,.25,300,'Shot duration');finite(s.sourceStart,0,86400,'Media start');color(s.accent);color(s.background);color(s.titleColor);
    choice(s.device,DEVICES,'Device');choice(s.stage,STAGES,'Stage');choice(s.finish,FINISHES,'Finish');choice(s.lighting,LIGHTING,'Lighting');choice(s.easing,EASINGS,'Camera easing');choice(s.titleLayout,TITLE_LAYOUTS,'Title layout');choice(s.transition,TRANSITIONS,'Transition');choice(s.titleMotion,TITLE_MOTIONS,'Title motion');if(s.crop!==undefined)area(s.crop,'Footage crop');
    finite(s.lightIntensity,.2,3,'Light intensity');finite(s.titleScale,.4,2.5,'Title size');finite(s.explode,0,2,'Explode amount');finite(s.depthOfField,0,1,'Depth of field');finite(s.cleanTint,0,1,'Clean tint');
    if(s.assetId!==undefined&&!ids.has(String(s.assetId)))throw Error('Shot references a missing asset.');vector(s.deviceRotation);vector(s.devicePosition);
    if(!Array.isArray(s.camera)||!s.camera.length||s.camera.length>100)throw Error('Invalid camera keyframes.');const times=new Set<number>();for(const rawKey of s.camera){const k=object(rawKey);const t=finite(k.time,0,duration,'Keyframe time');if(times.has(t))throw Error('Duplicate camera keyframe time.');times.add(t);vector(k.position);vector(k.target);finite(k.fov,10,110,'Camera FOV');}
    if(!Array.isArray(s.layers)||s.layers.length>50)throw Error('Too many layers.');const layerIds=new Set<string>();for(const rawLayer of s.layers){const l=object(rawLayer);str(l.id,100,'Layer ID');if(layerIds.has(String(l.id)))throw Error('Duplicate layer ID.');layerIds.add(String(l.id));str(l.name,120,'Layer name');vector(l.position);vector(l.rotation);finite(l.scale,.01,100,'Layer scale');finite(l.opacity,0,1,'Layer opacity');if(!Array.isArray(l.crop)||l.crop.length!==4)throw Error('Invalid layer crop.');const [x,y,w,h]=l.crop.map(v=>finite(v,0,1,'Crop'));if(w<=0||h<=0||x+w>1.00001||y+h>1.00001)throw Error('Crop exceeds image bounds.');if(l.assetId!==undefined&&!ids.has(String(l.assetId)))throw Error('Layer references a missing asset.');if(l.liveElementId!==undefined)str(l.liveElementId,100,'Live element ID');choice(l.attach,['screen','world'] as const,'Layer attachment');choice(l.motion,LAYER_MOTIONS,'Layer motion');finite(l.delay,0,300,'Layer delay');}
    if(!Array.isArray(s.callouts)||s.callouts.length>20)throw Error('A shot can have up to 20 callouts.');for(const rawCallout of s.callouts){const c=object(rawCallout);str(c.id,100,'Callout ID');str(c.text,120,'Callout text');finite(c.x,0,1,'Callout position');finite(c.y,0,1,'Callout position');finite(c.delay,0,300,'Callout delay');}
    if(!Array.isArray(s.highlights)||s.highlights.length>10)throw Error('A shot can have up to 10 highlights.');for(const rawHighlight of s.highlights){const h=object(rawHighlight);str(h.id,100,'Highlight ID');area(h.rect,'Highlight area');const start=finite(h.start,0,duration,'Highlight start');finite(h.end,start,duration,'Highlight end');choice(h.style,HIGHLIGHT_STYLES,'Highlight style');}
  }
  if(p.audioId!==undefined&&!(p.assets as Asset[]).some(a=>a.id===p.audioId&&a.kind==='audio'))throw Error('Missing soundtrack.');
  const result=p as unknown as Project;if(totalDuration(result)>1800)throw Error('Projects are limited to 30 minutes.');return result;
}

// ── AI and prompt control: plain, validated data only ─────────────────────────────────────────────────────────
const SHOT_FIELDS=['name','duration','device','stage','finish','lighting','lightIntensity','easing','assetId','sourceStart','title','subtitle','titleMotion','crop','titleLayout','titleScale','titleColor','accent','background','transition','explode','depthOfField','cleanTint'] as const;
const PLAN_FIELDS=new Set<string>([...SHOT_FIELDS,'motion','sections','callouts','highlights','turn','tilt']);
/** Applies the editable fields an AI or a quick command may set on a shot. Unknown fields are rejected, never ignored. */
function applyShotFields(shot:Shot,fields:Record<string,unknown>,project:Project){
  for(const key of Object.keys(fields))if(!PLAN_FIELDS.has(key))throw Error(`Unsupported shot field: ${key}`);
  const oldDuration=shot.duration;
  for(const key of SHOT_FIELDS)if(fields[key]!==undefined)(shot as unknown as Record<string,unknown>)[key]=fields[key];
  if(fields.turn!==undefined)shot.deviceRotation[1]=finite(fields.turn,-90,90,'Turn')*Math.PI/180;
  if(fields.tilt!==undefined)shot.deviceRotation[0]=finite(fields.tilt,-60,60,'Tilt')*Math.PI/180;
  if(fields.sections!==undefined){if(!Array.isArray(fields.sections)||fields.sections.length>20)throw Error('Use up to 20 sections per shot.');shot.layers=shot.layers.filter(l=>l.attach!=='screen');fields.sections.forEach((raw,i)=>{const s=object(raw);shot.layers.push(sectionLayer(str(s.name??`Section ${i+1}`,120,'Section name'),area(s.rect,'Section area'),{motion:s.motion===undefined?'pop':choice(s.motion,LAYER_MOTIONS,'Section motion'),delay:s.delay===undefined?.2+i*.15:finite(s.delay,0,300,'Section delay')}));});}
  if(fields.callouts!==undefined){if(!Array.isArray(fields.callouts)||fields.callouts.length>20)throw Error('Use up to 20 callouts per shot.');shot.callouts=fields.callouts.map((raw,i)=>{const c=object(raw);return {id:uid(),text:str(c.text,120,'Callout text'),x:finite(c.x,0,1,'Callout x'),y:finite(c.y,0,1,'Callout y'),delay:c.delay===undefined?.8+i*.4:finite(c.delay,0,300,'Callout delay')};});}
  if(fields.highlights!==undefined){if(!Array.isArray(fields.highlights)||fields.highlights.length>10)throw Error('Use up to 10 highlights per shot.');shot.highlights=fields.highlights.map(raw=>{const h=object(raw);return {id:uid(),rect:area(h.rect,'Highlight area'),start:h.start===undefined?Math.min(.8,shot.duration/4):finite(h.start,0,300,'Highlight start'),end:h.end===undefined?Math.max(shot.duration/2,shot.duration-.6):finite(h.end,0,300,'Highlight end'),style:h.style===undefined?'spotlight':choice(h.style,HIGHLIGHT_STYLES,'Highlight style')};});}
  const motion=fields.motion===undefined?undefined:choice(fields.motion,MOTIONS,'Camera motion');
  if(motion||fields.device!==undefined||fields.assetId!==undefined)shot.camera=cameraPreset(motion??'reveal',shot.duration,frameFor(shot,project));
  else if(shot.duration!==oldDuration)shot.camera.forEach(k=>k.time=k.time/oldDuration*shot.duration);
}
function findShot(p:Project,ref:unknown):number{if(typeof ref==='number'&&Number.isInteger(ref)&&ref>=1&&ref<=p.shots.length)return ref-1;const i=p.shots.findIndex(s=>s.id===ref);if(i<0)throw Error(`No shot ${JSON.stringify(ref)}. Shots are numbered from 1.`);return i;}
export function applyStoryboard(project:Project,input:unknown):Project {
  const board=object(input);if(board.version!==1||!Array.isArray(board.shots)||!board.shots.length||board.shots.length>100)throw Error('Storyboard needs version: 1 and 1–100 shots.');
  const next=structuredClone(project);next.shots=board.shots.map((raw,i)=>{const fields={...object(raw)};const shot=newShot(`Shot ${i+1}`);if(fields.motion===undefined)fields.motion='reveal';applyShotFields(shot,fields,next);return shot;});return validateProject(next);
}
export type Edit = Record<string,unknown> & { op: string };
const PROJECT_FIELDS=new Set(['name','width','height','fps','audioVolume']);
/** Applies edit operations from an AI or a quick command. Nothing is executed; every result is validated. */
export function applyEdits(project:Project,edits:unknown):{project:Project;summary:string[]}{
  if(!Array.isArray(edits)||edits.length>200)throw Error('Edits must be a list of up to 200 operations.');
  const next=structuredClone(project),summary:string[]=[];
  for(const raw of edits){const e=object(raw);const op=str(e.op,40,'Edit operation');
    switch(op){
      case 'setProject':{const set=object(e.set);for(const k of Object.keys(set)){if(!PROJECT_FIELDS.has(k))throw Error(`Unsupported project field: ${k}`);(next as unknown as Record<string,unknown>)[k]=set[k];}summary.push(`Project: ${Object.keys(set).join(', ')}`);break;}
      case 'updateShot':{const i=findShot(next,e.shot);const set=object(e.set);applyShotFields(next.shots[i],set,next);summary.push(`Shot ${i+1}: ${Object.keys(set).join(', ')}`);break;}
      case 'addShot':{const after=e.after===undefined?next.shots.length-1:findShot(next,e.after);const shot=newShot(`Shot ${next.shots.length+1}`);shot.assetId=next.shots[after]?.assetId;const set={...(e.set===undefined?{}:object(e.set))};if(set.motion===undefined)set.motion='reveal';applyShotFields(shot,set,next);next.shots.splice(after+1,0,shot);summary.push(`New shot after shot ${after+1}`);break;}
      case 'removeShot':{const i=findShot(next,e.shot);if(next.shots.length===1)throw Error('Keep at least one shot.');next.shots.splice(i,1);summary.push(`Removed shot ${i+1}`);break;}
      case 'moveShot':{const i=findShot(next,e.shot),to=findShot(next,e.to);const [moved]=next.shots.splice(i,1);next.shots.splice(to,0,moved);summary.push(`Moved shot ${i+1} to ${to+1}`);break;}
      case 'addSection':{const i=findShot(next,e.shot);const name=str(e.name??'Section',120,'Section name');next.shots[i].layers.push(sectionLayer(name,area(e.rect,'Section area'),{motion:e.motion===undefined?'pop':choice(e.motion,LAYER_MOTIONS,'Section motion'),delay:e.delay===undefined?.3:finite(e.delay,0,300,'Section delay')}));summary.push(`Shot ${i+1}: section “${name}”`);break;}
      case 'clearSections':{const i=findShot(next,e.shot);next.shots[i].layers=next.shots[i].layers.filter(l=>l.attach!=='screen');summary.push(`Shot ${i+1}: sections cleared`);break;}
      case 'addCallout':{const i=findShot(next,e.shot);const text=str(e.text,120,'Callout text');next.shots[i].callouts.push({id:uid(),text,x:finite(e.x,0,1,'Callout x'),y:finite(e.y,0,1,'Callout y'),delay:e.delay===undefined?.8:finite(e.delay,0,300,'Callout delay')});summary.push(`Shot ${i+1}: callout “${text}”`);break;}
      case 'addHighlight':{const i=findShot(next,e.shot);const s=next.shots[i];const style=e.style===undefined?'spotlight':choice(e.style,HIGHLIGHT_STYLES,'Highlight style');s.highlights.push({id:uid(),rect:area(e.rect,'Highlight area'),start:e.start===undefined?Math.min(.8,s.duration/4):finite(e.start,0,s.duration,'Highlight start'),end:e.end===undefined?Math.max(s.duration/2,s.duration-.6):finite(e.end,0,s.duration,'Highlight end'),style});summary.push(`Shot ${i+1}: ${style}`);break;}
      default:throw Error(`Unsupported edit operation: ${op}`);
    }
  }
  return {project:validateProject(next),summary};
}

// ── Demos the real app performs while Studio records. Validated here for review and again by the host. ─────────────
export const DEMO_ACTIONS=['click','doubleClick','rightClick','hover','type','keys','scroll','wait'] as const;
export interface DemoStep { do: typeof DEMO_ACTIONS[number]; target?: string; at?: [number,number]; text?: string; keys?: string; amount?: number; seconds?: number }
export interface Demo { steps: DemoStep[]; record: boolean; pace: 'natural'|'quick' }
export function validateDemo(input:unknown):Demo{
  const d=object(input);if(!Array.isArray(d.steps)||!d.steps.length||d.steps.length>200)throw Error('A demo needs 1–200 steps.');
  for(const key of Object.keys(d))if(!['steps','record','pace'].includes(key))throw Error(`Unsupported demo field: ${key}`);
  let seconds=0;const steps=d.steps.map((raw,i)=>{const s=object(raw);const label=`Demo step ${i+1}`;const action=choice(s.do,DEMO_ACTIONS,`${label} action`);const step:DemoStep={do:action};
    for(const key of Object.keys(s))if(!['do','target','at','text','keys','amount','seconds'].includes(key))throw Error(`${label}: unsupported field ${key}`);
    if(['click','doubleClick','rightClick','hover','scroll'].includes(action)){if(s.target!==undefined)step.target=str(s.target,200,`${label} target`);if(s.at!==undefined){if(!Array.isArray(s.at)||s.at.length!==2)throw Error(`${label}: at must be [x, y] fractions of the window.`);step.at=[finite(s.at[0],0,1,`${label} x`),finite(s.at[1],0,1,`${label} y`)];}if(action!=='scroll'&&!step.target&&!step.at)throw Error(`${label}: give a target (a control's name) or at: [x, y].`);}
    if(action==='type')step.text=str(s.text,500,`${label} text`);
    if(action==='keys'){step.keys=str(s.keys,60,`${label} keys`);if(!/^[A-Za-z0-9]+( ?\+ ?[A-Za-z0-9]+)*$/.test(step.keys))throw Error(`${label}: keys look like "Ctrl+F" or "Enter".`);
      const k=step.keys.toLowerCase().split('+').map(x=>x.trim());const has=(...names:string[])=>names.some(n=>k.includes(n));
      if(k.some(x=>x==='win'||x==='windows')||(has('alt')&&has('f4','tab','esc','escape','space'))||(has('ctrl','control')&&has('q','w','f4','esc','escape')))throw Error(`${label}: demos cannot close, quit or switch apps.`);}
    if(action==='scroll')step.amount=Math.round(finite(s.amount??3,-50,50,`${label} amount`));
    if(action==='wait'){step.seconds=finite(s.seconds,0,30,`${label} seconds`);seconds+=step.seconds;}else seconds+=action==='type'?step.text!.length*.06:.8;
    return step;});
  if(seconds>600)throw Error('Keep demos under 10 minutes.');
  return {steps,record:d.record!==false,pace:d.pace==='quick'?'quick':'natural'};
}
export interface StudioScript { project: Project; summary: string[]; demo?: Demo }
/** Reads what an AI chat returned: a storyboard (shots), edits, a demo to perform on the real app, or any mix. */
export function applyScript(project:Project,input:unknown):StudioScript{
  const s=object(input);if(s.version!==1)throw Error('Studio scripts need "version": 1.');
  for(const key of Object.keys(s))if(!['version','shots','edits','demo','notes'].includes(key))throw Error(`Unsupported script field: ${key}`);
  if(s.shots===undefined&&s.edits===undefined&&s.demo===undefined)throw Error('The script has no shots, edits or demo.');
  let next=project;const summary:string[]=[];
  if(s.shots!==undefined){next=applyStoryboard(next,{version:1,shots:s.shots});summary.push(`${next.shots.length} new shots, ${totalDuration(next).toFixed(1)} s (replaces the current story)`);}
  if(s.edits!==undefined){const result=applyEdits(next,s.edits);next=result.project;summary.push(...result.summary);}
  const demo=s.demo===undefined?undefined:validateDemo(s.demo);if(demo)summary.push(`Demo on your real app: ${demo.steps.length} steps${demo.record?', recorded':''}`);
  return {project:next,summary,demo};
}
export interface UiElement { name: string; type: string; x: number; y: number; width: number; height: number }
export function studioPrompt(p:Project,options:{goal?:string;app?:string;elements?:UiElement[]}={}):string{
  const media=p.assets.filter(a=>a.kind==='image'||a.kind==='video').map(({id,name,kind,duration,width,height})=>({id,name,kind,...(duration?{seconds:Math.round(duration*10)/10}:{}),...(width?{size:`${width}x${height}`}:{})}));
  const shots=p.shots.map((s,i)=>({shot:i+1,name:s.name,seconds:s.duration,assetId:s.assetId,device:s.device,stage:s.stage,title:s.title,sections:s.layers.filter(l=>l.attach==='screen').map(l=>l.name)}));
  const controls=(options.elements??[]).slice(0,160).map(e=>`${e.type}: "${e.name}" at [${e.x.toFixed(2)}, ${e.y.toFixed(2)}]`);
  return [`You are directing a truthful advertisement called "${p.name}" in LaunchReel Studio. Each asset is real footage or a real screenshot of the app; describe only what that footage shows. Never invent results, numbers or features. Ask me for feature facts before claiming anything the footage does not show.`,
    options.goal?`What I want: ${options.goal}`:'',
    'Return one JSON object only, no prose and no code. Format: {"version":1, "shots":[…], "edits":[…], "demo":{…}}. Include only the parts you need.',
    `"shots" replaces the whole story. Each shot may set: name, duration (0.25–300), assetId, sourceStart, title (use \\n for line breaks), subtitle, titleLayout (${TITLE_LAYOUTS.join('/')}), titleScale (0.4–2.5), titleColor/accent/background (#RRGGBB), device (${DEVICES.join('/')}; "none" shows the bare app UI), stage (${STAGES.join('/')}; "workspace" is a desk with a PC), finish (${FINISHES.join('/')}), lighting (${LIGHTING.join('/')}), lightIntensity (0.2–3), motion (${MOTIONS.join('/')}; "hero" fills the frame with the app), easing (${EASINGS.join('/')}), transition (${TRANSITIONS.join('/')}), turn and tilt (degrees), explode (0–2, how far sections lift off the screen), depthOfField (0–1, a fast-lens blur of everything behind the app; the app stays sharp), cleanTint (0–1, removes the desktop colour cast from a translucent app's background; its icons, images and text keep their colours), sections [{name, rect:[x,y,w,h], motion:${LAYER_MOTIONS.join('/')}, delay}], callouts [{text, x, y, delay}], highlights [{rect, start, end, style:${HIGHLIGHT_STYLES.join('/')}}]. All rects and x/y are fractions (0–1) of the app's screen from its top-left corner. Every shot must use an assetId of kind image or video from this list:\n${JSON.stringify(media,null,2)}\nFor video, sourceStart plus duration must stay within its seconds.`,
    `"edits" changes the current story instead. Operations: {"op":"updateShot","shot":N,"set":{…shot fields…}}, {"op":"addShot","after":N,"set":{…}}, {"op":"removeShot","shot":N}, {"op":"moveShot","shot":N,"to":M}, {"op":"addSection","shot":N,"name":"…","rect":[x,y,w,h],"motion":"pop"}, {"op":"clearSections","shot":N}, {"op":"addCallout","shot":N,"text":"…","x":0.3,"y":0.2}, {"op":"addHighlight","shot":N,"rect":[x,y,w,h],"style":"zoom","start":1,"end":4}, {"op":"setProject","set":{"name":"…","width":1920,"height":1080,"fps":60}}. Shots are numbered from 1. Current story:\n${JSON.stringify(shots,null,2)}`,
    options.app?`"demo" makes my real app "${options.app}" perform actions while Studio records it: {"record":true,"pace":"natural","steps":[{"do":"click","target":"Clipboard"},{"do":"type","text":"invoice"},{"do":"keys","keys":"Enter"},{"do":"scroll","amount":3},{"do":"wait","seconds":1.5}]}. Actions: ${DEMO_ACTIONS.join(', ')}. Point at controls by their exact name from this list (or with "at":[x,y] fractions of the window). Use only these controls; do not guess others:\n${controls.length?controls.join('\n'):'(No control names were readable. Use "at" positions.)'}\nKeep each demo short and natural, with short waits so viewers can follow. Never delete, send, pay, sign out or change settings.`:'Leave out "demo": no app is connected right now.',
    `Use 3–6 readable shots for a full film. Example:\n${JSON.stringify({version:1,shots:[{name:'Introduction',duration:5,assetId:media[0]?.id??'ASSET_ID',device:'laptop',stage:'workspace',motion:'hero',title:'Your clipboard.\nA little more powerful.',subtitle:p.name.toUpperCase(),explode:.6,sections:[{name:'Sidebar',rect:[0,0,.22,1]}],callouts:[{text:'Every copy, sorted',x:.5,y:.3}]}]},null,2)}`].filter(Boolean).join('\n\n');
}
/** The AI director's "plan a film" prompt. */
export function storyboardPrompt(p:Project):string{return studioPrompt(p);}

/** Problems that would put placeholder or live-only content into an exported film. */
export function exportProblems(p:Project,mode:'studio'|'cinematic'='studio'):string[]{
  const problems:string[]=[];const media=new Map(p.assets.map(a=>[a.id,a]));
  p.shots.forEach((s,i)=>{const label=`Shot ${i+1} “${s.name}”`;const asset=s.assetId?media.get(s.assetId):undefined;
    if(s.device==='title'){}
    else if(!asset||(asset.kind!=='image'&&asset.kind!=='video'))problems.push(`${label} has no app footage. Capture a still or a recording, then use it in this shot.`);
    else if(asset.kind==='video'&&asset.duration!==undefined&&s.sourceStart>=asset.duration)problems.push(`${label} starts after the end of “${asset.name}”. Lower “Media in”.`);
    if(s.layers.some(l=>l.liveElementId))problems.push(`${label} has live layers. Use “Freeze live layers to assets” first.`);
    if(mode==='cinematic'&&(s.device==='panel'||s.device==='title'||['aurora','atelier','pastel'].includes(s.stage)||s.crop))problems.push(`${label} uses the glass panel, a title card, the aurora stage or a footage crop. The cinematic renderer does not draw these yet; use Studio render.`);
    if(mode==='cinematic'&&s.cleanTint>0)problems.push(`${label} uses Clean glass tint. The cinematic renderer does not apply it yet; use Studio render or set it to 0.`);
    if(mode==='cinematic'&&(s.callouts.length||s.highlights.length||s.transition==='fade'))problems.push(`${label} uses callouts, spotlights, zooms or a fade. The cinematic renderer does not draw these yet; use Studio render or remove them.`);});
  return problems;
}
export function sourceUV(uv:{x:number;y:number},crop:[number,number,number,number],width:number,height:number):{x:number;y:number}{return {x:Math.min(width-1,Math.max(0,Math.floor((crop[0]+uv.x*crop[2])*width))),y:Math.min(height-1,Math.max(0,Math.floor((crop[1]+(1-uv.y)*crop[3])*height)))};}

/** Per-word entrance for kinetic headlines: 0 → 1 for word `index` at `local` seconds into the shot. */
export function wordProgress(local:number,index:number,motion:TitleMotion):number{if(motion==='fade')return ease((local-.15)/.6,'smooth');return ease((local-.12-index*.07)/.55,'ease-out');}
