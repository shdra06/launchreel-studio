import type {Edit,Project} from './project.ts';

/**
 * Quick commands: plain phrases turned into the same validated edits an AI chat would return, with no AI involved.
 * Recognised words are listed in `commandHelp`. Unrecognised text is reported, never guessed at.
 */
/** Whole words or phrases only, so "pan" never matches inside "panel". */
const words=(...phrases:string[])=>new RegExp(String.raw`\b(?:${phrases.join('|')})\b`);
const WORDS:Array<[RegExp,string,unknown]>=[
  [words('glass panel','panel','3d clipboard','just the clipboard'),'device','panel'],[words('title card','text card'),'device','title'],[words('aurora','launch look','glow stage'),'stage','aurora'],
  [words('swoop'),'motion','swoop'],[words('feature shot','product big','big product'),'motion','feature'],[words('low arc','arc shot','low angle'),'motion','arc'],[words('crane down','crane shot','top down'),'motion','crane'],[words('macro','macro pass','detail pass'),'motion','macro'],[words('pull back','pullback','reveal from detail'),'motion','pullback'],[words('crane up','rise up'),'motion','rise'],[words('pop title','pop text','pop headline'),'titleMotion','pop'],[words('words rise','rising title','rising text'),'titleMotion','rise'],
  [words('laptop','notebook'),'device','laptop'],[words('monitor','desktop','imac','display'),'device','desktop'],[words('tablet','ipad'),'device','tablet'],[words('phone','mobile','android','iphone'),'device','phone'],
  [words('floating screen','floating'),'device','floating'],[words('bare','frameless','no device','just the app','ui only'),'device','none'],
  [words('workspace','pc setup','office','desk setup','battlestation','setup'),'stage','workspace'],[words('on a desk','on the desk','desk'),'stage','desk'],[words('studio'),'stage','studio'],
  [words('abstract','rings'),'stage','abstract'],[words('gradient','backdrop'),'stage','gradient'],[words('void','dark stage','black stage'),'stage','void'],
  [words('soft light','soft lighting','soft'),'lighting','soft'],[words('dramatic light','dramatic lighting','moody'),'lighting','dramatic'],[words('sunset','golden hour','warm light'),'lighting','sunset'],[words('neon','cyberpunk','rgb light'),'lighting','neon'],
  [words('silver','aluminium','aluminum'),'finish','silver'],[words('graphite','space gr[ae]y','grey finish','gray finish'),'finish','graphite'],[words('black finish','black device','black frame'),'finish','black'],[words('white finish','white device','white frame'),'finish','white'],
  [words('hero','fill','full view','max(?:imum)? view','full screen','fullscreen'),'motion','hero'],[words('orbit','turntable','circle'),'motion','orbit'],[words('reveal'),'motion','reveal'],[words('close[- ]?up','zoom in'),'motion','closeup'],
  [words('push in','push'),'motion','push'],[words('tilt'),'motion','tilt'],[words('dolly','pan','slide across'),'motion','dolly'],[words('still','static','locked'),'motion','still'],
  [words('linear'),'easing','linear'],[words('ease[- ]out'),'easing','ease-out'],[words('snappy','dramatic move','dramatic motion','dramatic easing'),'easing','dramatic'],
  [words('fade in','fade'),'transition','fade'],[words('hard cut','cut'),'transition','cut'],
  [words('title (?:at the )?bottom'),'titleLayout','bottom-left'],[words('title (?:in the )?(?:center|centre|middle)'),'titleLayout','center'],[words('title (?:at the )?top'),'titleLayout','top-left'],[words('no title','hide (?:the )?title'),'titleLayout','none'],
  [words('no explode','flat','unexplode'),'explode',0],
  [words('sharp background','no depth of field','no bokeh','no blur'),'depthOfField',0],[words('depth of field','bokeh','blur(?:red)? background','shallow focus','cinematic lens'),'depthOfField',.7],
  [words('clean tint','clean glass','neutral glass','remove tint'),'cleanTint',1],[words('original tint','keep tint'),'cleanTint',0],
];
const FORMATS:Array<[RegExp,Record<string,number>]>=[[words('4k portrait','portrait 4k'),{width:2160,height:3840}],[words('4k square','square 4k'),{width:2160,height:2160}],[words('4k'),{width:3840,height:2160}],[words('portrait','vertical','reels?','shorts','tiktok'),{width:1080,height:1920}],[words('square'),{width:1080,height:1080}],[words('landscape','1080p','widescreen'),{width:1920,height:1080}]];

export const commandHelp='Try: “glass panel on aurora, swoop, title "Copy once. Paste anywhere."” · “laptop on workspace, hero camera, explode” · “shot 2 phone, orbit, neon” · “all shots fade” · “portrait 60fps” · “add shot” · “duration 4”';

export function parseCommand(text:string,project:Project,currentShot:number):{edits:Edit[];notes:string[]}{
  const edits:Edit[]=[],notes:string[]=[];
  // Quoted text first, so words inside a headline are never read as commands.
  const quoted:Record<string,string>={};let rest=text.replace(/\b(title|headline|eyebrow|subtitle|tagline|callout)\s*[:=]?\s*["“']([^"”']{0,300})["”']/gi,(_,key:string,value:string)=>{const k=key.toLowerCase();quoted[k==='headline'?'title':k==='tagline'||k==='eyebrow'?'subtitle':k]=value.replace(/\\n/g,'\n');return ' ';});
  rest=` ${rest.toLowerCase()} `;
  const shots:number[]=[];
  if(/\ball shots|every shot|whole film|everything\b/.test(rest))shots.push(...project.shots.map((_,i)=>i+1));
  else{const m=rest.match(/\bshot (\d{1,3})\b/);const n=m?Number(m[1]):currentShot+1;if(n<1||n>project.shots.length){notes.push(`There is no shot ${n}.`);return {edits,notes};}shots.push(n);}
  if(/\badd (a )?(new )?shot\b/.test(rest)){edits.push({op:'addShot',after:shots[shots.length-1]});notes.push('Add a shot');}
  if(/\b(delete|remove) (this |the )?shot\b/.test(rest)){edits.push({op:'removeShot',shot:shots[0]});notes.push(`Remove shot ${shots[0]}`);}
  if(/\bclear sections|remove sections\b/.test(rest))for(const shot of shots){edits.push({op:'clearSections',shot});notes.push(`Clear sections in shot ${shot}`);}
  const set:Record<string,unknown>={};
  for(const [pattern,key,value] of WORDS)if(set[key]===undefined&&pattern.test(rest))set[key]=value;
  const explode=rest.match(/\bexplode(?:d)?(?: (\d+(?:\.\d+)?))?\b/);if(explode&&set.explode===undefined)set.explode=Math.min(2,explode[1]?Number(explode[1]):1);
  const duration=rest.match(/\b(?:duration|for|lasts?)?\s*(\d+(?:\.\d+)?)\s*(?:s|sec|secs|seconds)\b/);if(duration)set.duration=Math.max(.25,Math.min(300,Number(duration[1])));
  const turn=rest.match(/\bturn(?:ed)? (-?\d{1,2})\b/);if(turn)set.turn=Number(turn[1]);
  const tilt=rest.match(/\btilt(?:ed)? (-?\d{1,2})\b/);if(tilt){set.tilt=Number(tilt[1]);if(set.motion==='tilt')delete set.motion;}
  if(/\bbright(er)?\b/.test(rest))set.lightIntensity=1.6;if(/\bdim(mer)?\b|\bdarker\b/.test(rest))set.lightIntensity=.6;
  const hex=rest.match(/\b(accent|background|title colou?r)\s*(#[0-9a-f]{6})\b/);if(hex)set[hex[1].startsWith('title')?'titleColor':hex[1]]=hex[2];
  if(quoted.title!==undefined)set.title=quoted.title;if(quoted.subtitle!==undefined)set.subtitle=quoted.subtitle;
  if(Object.keys(set).length)for(const shot of shots){edits.push({op:'updateShot',shot,set:{...set}});}
  if(Object.keys(set).length)notes.push(`${shots.length>1?'All shots':`Shot ${shots[0]}`}: ${Object.entries(set).map(([k,v])=>`${k} ${typeof v==='string'?`“${v.replace(/\n/g,' / ')}”`:v}`).join(', ')}`);
  if(quoted.callout!==undefined)for(const shot of shots){edits.push({op:'addCallout',shot,text:quoted.callout,x:.5,y:.35});notes.push(`Callout “${quoted.callout}” in shot ${shot} (drag it in Sections to place it)`);}
  const project_:Record<string,unknown>={};for(const [pattern,size] of FORMATS)if(pattern.test(rest)){Object.assign(project_,size);break;}
  const fps=rest.match(/\b(30|60) ?fps\b/);if(fps)project_.fps=Number(fps[1]);
  if(Object.keys(project_).length){edits.push({op:'setProject',set:project_});notes.push(`Format: ${Object.entries(project_).map(([k,v])=>`${k} ${v}`).join(', ')}`);}
  if(!edits.length)notes.push(`Nothing recognised. ${commandHelp}`);
  return {edits,notes};
}
