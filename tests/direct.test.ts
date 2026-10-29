import {test} from 'node:test';
import assert from 'node:assert/strict';
import {newProject,validateProject,applyEdits,applyScript,validateDemo,layerMotion,cameraPreset,cameraAt,frameDistance,screenFit,highlightWeight,transitionOpacity,ease,wordProgress,exportProblems,frameFor,type Asset} from '../web/project.ts';
import {parseCommand} from '../web/commands.ts';

const still:Asset={id:'hub',name:'Hub',kind:'image',file:'hub.png',url:'x',width:1600,height:1000};
const withStill=()=>{const p=newProject();p.assets.push(still);p.shots.forEach(s=>s.assetId='hub');return p;};

test('quick commands set device, stage, camera, title and format without an AI',()=>{
  const p=withStill();const {edits,notes}=parseCommand('laptop on workspace, hero camera, neon, explode, title "Copy once. Paste anywhere." portrait 60fps',p,0);
  const next=applyEdits(p,edits).project,s=next.shots[0];
  assert.equal(s.device,'laptop');assert.equal(s.stage,'workspace');assert.equal(s.lighting,'neon');assert.equal(s.explode,1);assert.equal(s.title,'Copy once. Paste anywhere.');
  assert.equal(next.width,1080);assert.equal(next.height,1920);assert.equal(next.fps,60);assert.ok(notes.length>=2);
  assert.equal(next.shots[1].device,'desktop','other shots are untouched');});
test('quick commands never read words inside a quoted headline as commands',()=>{const p=withStill();const {edits}=parseCommand('title "Works on your phone and laptop"',p,0);const s=applyEdits(p,edits).project.shots[0];assert.equal(s.device,'desktop');assert.equal(s.title,'Works on your phone and laptop');});
test('quick commands can target a shot or every shot, and report what they did not understand',()=>{const p=withStill();assert.equal(applyEdits(p,parseCommand('shot 3 phone',p,0).edits).project.shots[2].device,'phone');assert.ok(applyEdits(p,parseCommand('all shots fade',p,0).edits).project.shots.every(s=>s.transition==='fade'));const none=parseCommand('make it pop somehow',p,0);assert.equal(none.edits.length,0);assert.match(none.notes[0],/Nothing recognised/);assert.match(parseCommand('shot 9 phone',p,0).notes[0],/no shot 9/);});
test('edits add sections that sit on the screen and reject anything unsupported',()=>{const p=withStill();const r=applyEdits(p,[{op:'addSection',shot:1,name:'Sidebar',rect:[0,0,.2,1]},{op:'addCallout',shot:1,text:'Search',x:.5,y:.1},{op:'addHighlight',shot:1,rect:[.2,0,.8,.2],style:'zoom'}]).project.shots[0];
  assert.equal(r.layers[0].attach,'screen');assert.deepEqual(r.layers[0].crop,[0,0,.2,1]);assert.equal(r.callouts[0].text,'Search');assert.equal(r.highlights[0].style,'zoom');
  assert.throws(()=>applyEdits(p,[{op:'runScript',code:'x'}]),/Unsupported edit/);assert.throws(()=>applyEdits(p,[{op:'updateShot',shot:1,set:{onLoad:'x'}}]),/Unsupported shot field/);assert.throws(()=>applyEdits(p,[{op:'addSection',shot:1,rect:[.9,0,.5,1]}]),/inside the screen/);});
test('a studio script combines a storyboard, edits and a demo, and leaves the project alone when invalid',()=>{const p=withStill(),before=JSON.stringify(p);
  const s=applyScript(p,{version:1,shots:[{assetId:'hub',device:'none',stage:'gradient',motion:'hero',sections:[{name:'Cards',rect:[.25,.2,.7,.6]}]}],edits:[{op:'updateShot',shot:1,set:{title:'Real UI'}}],demo:{steps:[{do:'click',target:'Clipboard'},{do:'type',text:'invoice'}]}});
  assert.equal(s.project.shots.length,1);assert.equal(s.project.shots[0].title,'Real UI');assert.equal(s.demo!.steps.length,2);assert.equal(s.summary.length,3);
  assert.throws(()=>applyScript(p,{version:1,shots:[{assetId:'hub'}],code:'alert(1)'}),/Unsupported script field/);assert.equal(JSON.stringify(p),before);});
test('demo scripts are checked step by step',()=>{assert.equal(validateDemo({steps:[{do:'keys',keys:'Ctrl+F'},{do:'scroll'}]}).steps[1].amount,3);
  assert.throws(()=>validateDemo({steps:[{do:'click'}]}),/target/);assert.throws(()=>validateDemo({steps:[{do:'launch',target:'cmd'}]}),/action/);assert.throws(()=>validateDemo({steps:[{do:'keys',keys:'rm -rf'}]}),/keys look like/);assert.throws(()=>validateDemo({steps:Array(25).fill({do:'wait',seconds:30})}),/10 minutes/);});
test('screen sections start flush with the real UI and lift off by the explode amount',()=>{const layer={motion:'pop' as const,delay:.2,attach:'screen' as const};assert.ok(Math.abs(layerMotion(0,6,0,layer,1).z)<1e-9);assert.ok(Math.abs(layerMotion(6,6,0,layer,1).z-.75)<1e-9);assert.ok(layerMotion(6,6,1,layer,1).z>layerMotion(6,6,0,layer,1).z,'later sections rise further');assert.equal(layerMotion(6,6,0,{...layer,motion:'none'},0).z,0);});
test('the hero camera frames the app screen to fill the picture',()=>{const fit=screenFit('none',1.6);const keys=cameraPreset('hero',5,{...fit,aspect:16/9});const d=keys[1].position[2];const half=Math.tan(17*Math.PI/180);const visibleHeight=2*d*half,fill=Math.max(fit.height/visibleHeight,fit.width/(visibleHeight*16/9));assert.ok(fill>.9&&fill<1,`the app fills ${fill} of its limiting dimension`);assert.ok(Math.abs(frameDistance(2,1,34,2,1)-.5/half)<1e-9);});
test('camera easing changes the path but never the end points',()=>{const keys=cameraPreset('dolly',4);for(const e of ['smooth','linear','ease-out','dramatic'] as const){assert.deepEqual(cameraAt(keys,4,e).position,keys[1].position);assert.deepEqual(cameraAt(keys,0,e).position,keys[0].position);}assert.ok(ease(.5,'ease-out')>ease(.5,'linear'));});
test('focus and fades ramp in and out inside their time span',()=>{const h={start:1,end:4};assert.equal(highlightWeight(.5,h),0);assert.equal(highlightWeight(2.5,h),1);assert.equal(highlightWeight(4.2,h),0);assert.equal(transitionOpacity(0,'fade'),1);assert.equal(transitionOpacity(1,'fade'),0);assert.equal(transitionOpacity(0,'cut'),0);});
test('projects saved before the new controls still open with sensible defaults',()=>{const old=JSON.parse(JSON.stringify(withStill()));for(const s of old.shots){for(const k of ['finish','lighting','lightIntensity','easing','titleLayout','titleScale','titleColor','transition','explode','callouts','highlights'])delete s[k];}old.shots[0].layers=[{id:'l',name:'Old',position:[0,0,0],rotation:[0,0,0],scale:1,opacity:1,crop:[0,0,1,1]}];
  const p=validateProject(old);assert.equal(p.shots[0].finish,'silver');assert.deepEqual(p.shots[0].callouts,[]);assert.equal(p.shots[0].layers[0].attach,'world');});
test('other camera moves show the whole device and leave the headline corner clear',()=>{const keys=cameraPreset('reveal',6,{width:5.9,height:3.7,aspect:16/9,device:{width:6.12,height:4.75}});const end=keys[1];const distance=Math.hypot(...end.position.map((v,i)=>v-end.target[i]));const visibleHeight=2*distance*Math.tan(17*Math.PI/180);assert.ok(4.75/visibleHeight<.7,'the device takes well under the full height');assert.ok(end.target[0]<0&&end.target[1]>0,'the device sits right and low, away from a top-left headline');});

test('the launch look: glass panel, aurora, swoop and pop titles are one command away',()=>{const p=withStill();const s=applyEdits(p,parseCommand('glass panel on aurora, swoop, pop title',p,0).edits).project.shots[0];assert.equal(s.device,'panel');assert.equal(s.stage,'aurora');assert.equal(s.titleMotion,'pop');assert.equal(s.camera.length,3);});
test('title cards need no footage, but the cinematic renderer refuses looks it cannot draw',()=>{const p=withStill();p.shots.forEach(s=>s.assetId='hub');p.shots[0].device='title';delete p.shots[0].assetId;assert.deepEqual(exportProblems(p),[]);p.shots[1].device='panel';assert.match(exportProblems(p,'cinematic').join(' '),/glass panel/);});
test('a footage crop frames just part of the app and changes the shape the camera frames',()=>{const p=withStill();const s=p.shots[0];s.device='panel';const full=frameFor(s,p);s.crop=[0,.45,.24,.45];validateProject(p);const framed=frameFor(s,p);assert.ok(framed.width/framed.height<full.width/full.height);s.crop=[.9,0,.2,1];assert.throws(()=>validateProject(p),/inside the screen/);});
test('kinetic headlines bring words in one after another',()=>{assert.equal(wordProgress(0,0,'rise'),0);assert.ok(wordProgress(.5,0,'rise')>wordProgress(.5,3,'rise'));assert.equal(wordProgress(3,5,'rise'),1);assert.equal(wordProgress(3,9,'fade'),1);});

import {RECIPES,recipeStoryboard,recipeMusic} from '../web/recipes.ts';
test('recipes turn real captures into a complete, editable, beat-timed film',()=>{const p=withStill();p.assets.push({id:'rec',name:'FlyShelf · recording 09.12.33',kind:'video',file:'r.mp4',url:'x',duration:12,width:1920,height:1080});
  for(const recipe of RECIPES){const board=recipeStoryboard(recipe,p,{appName:'FlyShelf'});const s=applyScript(p,board).project;assert.equal(s.shots[0].device,'title');assert.equal(s.shots.at(-1)!.title,'FlyShelf');assert.ok(s.shots.some(x=>x.assetId==='rec'&&x.title==='FlyShelf'));assert.ok(s.shots.filter(x=>x.device==='panel').every(x=>x.depthOfField>0&&x.cleanTint===0),'lens look on, tint untouched');
    assert.ok(s.shots.every(x=>x.duration%1===0&&x.duration>=2),'every shot sits on the 2-second bar grid');const m=recipeMusic(board);assert.ok(m.bars*2>=s.shots.reduce((n,x)=>n+x.duration,0));}
  const empty=newProject();assert.throws(()=>recipeStoryboard(RECIPES[0],empty),/real footage/);});

import {snapToEdges} from '../web/cutout.ts';
test('crops snap to the app window\'s own borders, not to the desktop or a card inside the app',()=>{
  const W=200,H=120,px=new Uint8ClampedArray(W*H*4);
  const fill=(x0:number,y0:number,x1:number,y1:number,v:number)=>{for(let y=y0;y<y1;y++)for(let x=x0;x<x1;x++){const i=(y*W+x)*4;px[i]=px[i+1]=px[i+2]=v;px[i+3]=255;}};
  fill(0,0,W,H,30);for(let i=0;i<W*H;i+=7)px[i*4]+=3;            // dark desktop with faint noise
  fill(170,10,190,20,220);                                        // a neighbouring window's text, outside the app
  fill(40,30,160,100,90);fill(48,38,152,92,150);fill(49,39,151,91,90); // the app window, with a bordered card inside
  const f=(x:number,y:number,w:number,h:number)=>[x/W,y/H,w/W,h/H] as [number,number,number,number];
  const px2=(r:number[])=>[Math.round(r[0]*W),Math.round(r[1]*H),Math.round(r[2]*W),Math.round(r[3]*H)];
  assert.deepEqual(px2(snapToEdges(px,W,H,f(35,26,130,77))),[40,30,120,70],'a loose crop tightens onto the window');
  assert.deepEqual(px2(snapToEdges(px,W,H,f(44,34,112,62))),[40,30,120,70],'the window frame beats the card inside it');
  assert.deepEqual(px2(snapToEdges(px,W,H,f(2,2,20,20))),[2,2,20,20],'no edges, no snapping');
});
test('in portrait the app moves away from the headline: down for top titles, up for bottom titles',()=>{
  const frame=(headline:'top-left'|'bottom-left')=>({width:5.9,height:3.7,aspect:9/16,device:{width:6.12,height:4.75},headline});
  assert.ok(cameraPreset('push',4,frame('top-left'))[0].target[1]>0,'looking above the app leaves room on top');
  assert.ok(cameraPreset('push',4,frame('bottom-left'))[0].target[1]<0,'looking below the app leaves room at the bottom');
});
test('lens and glass commands set depth of field and the clean tint, and bad values are refused',()=>{
  const p=withStill();const s=applyEdits(p,parseCommand('glass panel, bokeh, clean tint',p,0).edits).project.shots[0];
  assert.equal(s.depthOfField,.7);assert.equal(s.cleanTint,1);
  const off=applyEdits(p,parseCommand('sharp background, keep tint',p,0).edits).project.shots[0];assert.equal(off.depthOfField,0);assert.equal(off.cleanTint,0);
  const bad=structuredClone(p);(bad.shots[0] as {depthOfField:number}).depthOfField=3;assert.throws(()=>validateProject(bad),/Depth of field/);
  const cine=structuredClone(p);cine.shots[0].cleanTint=.5;assert.ok(exportProblems(cine,'cinematic').some(x=>/Clean glass tint/.test(x)));
});

test('demos can never close, quit or switch the app',()=>{
  for(const keys of ['Alt+F4','Ctrl+W','ctrl + q','Ctrl+F4','Alt+Space','Alt+Tab','Win+D'])assert.throws(()=>validateDemo({steps:[{do:'keys',keys}]}),/cannot|Windows/,keys);
  assert.equal(validateDemo({steps:[{do:'keys',keys:'Ctrl+F'}]}).steps[0].keys,'Ctrl+F');
});
