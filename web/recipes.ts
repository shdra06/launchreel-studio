import {type Project,type Asset,type Rect} from './project.ts';

/**
 * Recipes: one click from captured footage to a complete, beat-timed ad. Each recipe is a storyboard in the same
 * validated format an AI script uses, so every shot stays fully editable afterwards. Shot lengths sit on a 120 BPM
 * grid (one bar = 2 s) so cuts land on the beat of the generated soundtrack.
 */
export interface Recipe { id: string; name: string; description: string; width: number; height: number }
export const RECIPES: Recipe[] = [
  {id:'launch',name:'Launch film',description:'Dark aurora stage, glass panel, swooping cameras. 16:9.',width:1920,height:1080},
  {id:'short',name:'Promo short',description:'Bright pastel architecture, frosted panel, punchy cards. 9:16 for Shorts, Reels and TikTok.',width:1080,height:1920},
  {id:'tour',name:'Feature tour',description:'Calm, bright walkthrough of every capture, one feature per shot. 16:9.',width:1920,height:1080},
];

type Look = {stage:string;finish:string;accent:string;background:string;titleColor:string;lighting:string;lightIntensity:number};
const LOOKS:Record<string,Look>={
  launch:{stage:'aurora',finish:'graphite',accent:'#6ea8ff',background:'#0a0e1a',titleColor:'#f4f6ff',lighting:'soft',lightIntensity:1.1},
  short:{stage:'atelier',finish:'white',accent:'#5b8def',background:'#e9eef5',titleColor:'#1a2333',lighting:'soft',lightIntensity:1.25},
  tour:{stage:'atelier',finish:'white',accent:'#5b8def',background:'#e9eef5',titleColor:'#1a2333',lighting:'soft',lightIntensity:1.2},
};
const MOVES=['swoop','push','orbit','rise','dolly','hero'] as const;

/** Builds the recipe's storyboard from the project's own image and video captures (with any crops given per asset). */
export function recipeStoryboard(recipe:Recipe,project:Project,options:{appName?:string;crops?:Record<string,Rect>}={}){
  const media=project.assets.filter((a:Asset)=>a.kind==='image'||a.kind==='video');
  if(!media.length)throw Error('Capture or import some footage of your app first. Recipes only use real footage.');
  const look=LOOKS[recipe.id]??LOOKS.launch,name=options.appName??project.name.split(/[—–-]/)[0].trim()??'Your app';
  const card=(title:string,subtitle:string,duration:number,scale=1.5)=>({...look,device:'title',duration,motion:'still',title,subtitle,titleLayout:'center',titleScale:scale,titleMotion:'rise'});
  const shots:Record<string,unknown>[]=[card(`Meet ${name}.`,recipe.id==='short'?'New':'Introducing',2,recipe.id==='short'?1.3:1.55)];
  media.forEach((asset,i)=>{
    const seconds=recipe.id==='tour'?4:asset.kind==='video'&&(asset.duration??0)>=3.5?4:3;
    const duration=asset.kind==='video'&&asset.duration?Math.min(seconds,Math.max(2,Math.floor(asset.duration/2)*2||2)):seconds;
    // A fast-lens background on every app shot; the app itself stays sharp. (Clean tint stays opt-in: it suits translucent apps only.)
    shots.push({...look,device:'panel',depthOfField:recipe.id==='tour'?.5:.75,duration,assetId:asset.id,...(options.crops?.[asset.id]?{crop:options.crops[asset.id]}:{}),
      motion:recipe.id==='tour'?(i%2?'push':'orbit'):MOVES[i%MOVES.length],easing:recipe.id==='launch'?(i%2?'ease-out':'dramatic'):'smooth',
      title:asset.name.replace(/\s*·\s*(still|recording)\s+[\d.:]+$/i,'').replace(/\.[a-z0-9]+$/i,'').replace(/[_-]+/g,' '),subtitle:name,
      titleLayout:recipe.width<recipe.height?'top-center':i%2?'bottom-left':'top-left',turn:recipe.id==='tour'?0:i%2?-12:10});
    // A beat-length card between groups keeps the rhythm (not in the calm tour).
    if(recipe.id!=='tour'&&i%3===2&&i<media.length-1)shots.push(card('And there is more.','',2,1.35));
  });
  shots.push(card(name,'Available now',4,recipe.width<recipe.height?2:2.4));
  shots.at(-1)!.titleMotion='pop';
  return {version:1 as const,shots};
}

/** Soundtrack sections for the generated track: drop on bar 1, a break before the last third, final hit on the end card. */
export function recipeMusic(storyboard:{shots:Array<Record<string,unknown>>}){
  const total=storyboard.shots.reduce((n,s)=>n+Number(s.duration),0),bars=Math.ceil(total/2),ending=Math.max(2,bars-2);
  const breakBar=Math.max(2,Math.round(bars*.6));
  return {bars,drops:[1,breakBar+1],breaks:[breakBar],ending};
}
