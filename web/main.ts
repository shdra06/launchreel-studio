import {StudioScene} from './scene.ts';
import {SectionsEditor,type Tool} from './sections.ts';
import {parseCommand,commandHelp} from './commands.ts';
import {RECIPES,recipeStoryboard,recipeMusic} from './recipes.ts';
import {newProject,newShot,uid,validateProject,totalDuration,shotAt,shotStart,cameraPreset,frameFor,applyEdits,applyScript,studioPrompt,exportProblems,sectionLayer,type Project,type Asset,type Shot,type Layer,type Rect,type Demo,type UiElement} from './project.ts';

declare global { interface Window {chrome?:{webview:{postMessage:(message:unknown)=>void;addEventListener:(name:string,callback:(event:MessageEvent)=>void)=>void}};studioTest?:Record<string,unknown> } }
type WindowInfo={handle:string;title:string;pid:number;app:string;path?:string;role:'main'|'floating';width:number;height:number;minimized:boolean};
type Waiting={name:string;path:string;alreadyRunning:boolean;reconnect?:string|null;candidates:WindowInfo[]};
type Connection={title:string;app:string;role:'main'|'floating';dockable:boolean;bridge?:unknown;appView?:boolean};
type RecentApp={path:string;name:string;running:boolean;kind?:'desktop'|'store'|'android'};
type InstalledApp={id:string;name:string;kind:'desktop'|'store'};
type AndroidStatus={adb?:string|null;scrcpy?:string|null;emulator?:string|null;problem?:string|null;devices:Array<{serial:string;state:string;model:string;emulator:boolean}>;virtualDevices:string[]};
const $=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
const input=(id:string)=>$<HTMLInputElement>(id);
const select=(id:string)=>$<HTMLSelectElement>(id);
let project=newProject(),time=0,playing=false,rendering=false,manualCamera=false,exporting=false,cancelExport=false,recording=false,demoRunning=false;
let selectedAsset='',selectedLayer='',activeBridge=false,layerPolling=false,pendingScript:unknown,sliderBase:Project|undefined;
let history:Project[]=[],future:Project[]=[],autosaveTimer:ReturnType<typeof setTimeout>,toastTimer:ReturnType<typeof setTimeout>;
let tools:{ffmpeg?:string;blender?:string;adb?:string;scrcpy?:string}={};
let appView=false,connection:Connection|undefined,viewportSending=false,viewportAgain=false;
/** Frames still to draw. The preview renders only after a change (plus a few frames for textures and video seeks to land), never continuously while idle. */
let settle=12;const redraw=()=>{settle=12;};
const pending=new Map<string,{resolve:(value:any)=>void;reject:(reason:unknown)=>void}>();
const native=!!window.chrome?.webview;
async function host<T=any>(method:string,data:Record<string,unknown>={}):Promise<T>{
  if(!native){if(method==='ready')return {tools:{}} as T;if(method==='project.autosave'){localStorage.setItem('launchreel-studio',JSON.stringify(data.project));return null as T;}throw Error('This action needs the Windows desktop studio.');}
  const id=uid();return new Promise((resolve,reject)=>{pending.set(id,{resolve,reject});window.chrome!.webview.postMessage({id,method,data});});
}
function toast(message:string,error=false){const t=$('toast');t.textContent=message;t.style.borderColor=error?'#ad6b64':'#536449';t.classList.add('visible');clearTimeout(toastTimer);toastTimer=setTimeout(()=>t.classList.remove('visible'),error?8000:4500);$('status').textContent=message;}
async function action(work:()=>Promise<unknown>){try{await work();}catch(e){toast(e instanceof Error?e.message:String(e),true);}}
function current(){return shotAt(project,time).shot;}
function commit(change:(draft:Project)=>void,rebuild=true){if(exporting)return;const next=structuredClone(project);change(next);let valid:Project;try{valid=validateProject(next);}catch(e){toast((e as Error).message,true);refresh();return;}history.push(sliderBase??structuredClone(project));sliderBase=undefined;if(history.length>80)history.shift();future=[];project=valid;time=Math.min(time,totalDuration(project)-.0001);if(rebuild)scene.invalidate();refresh();saveSoon();}
/** Edits the shot under the playhead. */
function editShot(change:(shot:Shot,draft:Project)=>void,rebuild=true){const id=current().id;commit(p=>change(p.shots.find(s=>s.id===id)!,p),rebuild);}
function replace(next:Project){project=validateProject(next);history=[];future=[];time=0;manualCamera=false;selectedLayer='';scene.invalidate();refresh();saveSoon();}
function saveSoon(){clearTimeout(autosaveTimer);$('save-status').textContent='Saving…';autosaveTimer=setTimeout(()=>action(async()=>{await host('project.autosave',{project});$('save-status').textContent='Saved on this device';}),600);}
function undo(redo=false){if(exporting)return;const source=redo?future:history,target=redo?history:future;if(!source.length)return;target.push(structuredClone(project));project=source.pop()!;time=Math.min(time,totalDuration(project)-.0001);scene.invalidate();refresh();saveSoon();}
const scene=new StudioScene($<HTMLCanvasElement>('viewport'));scene.onError=e=>toast((e as Error).message,true);
scene.onCameraChange=()=>{if(!playing&&!exporting)manualCamera=true;redraw();};
let inputChain=Promise.resolve(),pendingMove:Record<string,unknown>|undefined;
function sendInput(data:Record<string,unknown>){inputChain=inputChain.then(()=>host('input',data)).catch(e=>{setMode(false);toast(e.message,true);});}
scene.onInput=data=>{if(data.action==='pointerMove'){if(pendingMove){pendingMove=data;return;}pendingMove=data;inputChain=inputChain.then(()=>{const move=pendingMove!;pendingMove=undefined;return host('input',move);}).catch(()=>{});return;}sendInput(data);};
scene.onExit=()=>setMode(false);
const fmt=(t:number)=>`${String(Math.floor(t/60)).padStart(2,'0')}:${String(Math.floor(t%60)).padStart(2,'0')}`;
function setTime(next:number){time=Math.max(0,Math.min(totalDuration(project)-.0001,next));manualCamera=false;refresh(false);}
/** Live 3D: the real app, mirrored on the 3D panel and operated through it. The camera rests on the shot's final framing. */
function setMode(interact:boolean){if(interact&&!connection&&!activeBridge){toast('Launch or choose your app in the Capture tab first.',true);return;}
  if(interact){stopPlayback();scene.live=true;const at=shotAt(project,time);setTime(at.start+at.shot.duration-.02);manualCamera=false;scene.canvas.focus();}
  scene.setInteractive(interact);$('mode-compose').classList.toggle('active',!interact&&!appView);$('mode-interact').classList.toggle('active',interact);
  $('preview-label').textContent=interact?'LIVE 3D · YOUR APP RESPONDS HERE · SHIFT+ESC TO LEAVE':'3D STUDIO · PREVIEW';redraw();}
function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string):HTMLElementTagNameMap[K]{const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=text;return element;}
function stopPlayback(){playing=false;$('play').textContent='▶';}

// ── The real app ──────────────────────────────────────────────────────────────────────────────────────────────
function appLayout(visible:boolean){appView=visible;document.body.classList.toggle('app-view',visible);$('native-app-slot').hidden=!visible;$('mode-app').classList.toggle('active',visible);$('mode-compose').classList.toggle('active',!visible);if(visible){stopPlayback();scene.setInteractive(false);$('slot-message').textContent='Bringing your app into App view…';$('slot-restore').hidden=true;}requestAnimationFrame(()=>{resize();void sendViewport();});}
async function showAppView(visible:boolean){if(exporting)return;await host('app.view',{mode:visible?'app':'3d'});appLayout(visible);}
/** Studio's own dialogs would sit underneath the app window, so App view steps aside while they are open. */
async function leaveAppView(){if(appView)await showAppView(false);}
async function sendViewport(){if(!appView||!connection)return;if(viewportSending){viewportAgain=true;return;}viewportSending=true;try{do{viewportAgain=false;const r=$('native-app-slot').getBoundingClientRect();await host('app.viewport',{x:r.x,y:r.y,width:r.width,height:r.height});}while(viewportAgain&&appView);}catch(e){toast((e as Error).message,true);appLayout(false);}finally{viewportSending=false;}}
new ResizeObserver(()=>{void sendViewport();}).observe($('native-app-slot'));
window.addEventListener('resize',()=>{void sendViewport();});
function captureState(state:'idle'|'waiting'|'connected'){$('capture-idle').hidden=state!=='idle';$('capture-waiting').hidden=state!=='waiting';$('capture-controls').hidden=state!=='connected';$<HTMLButtonElement>('mode-app').disabled=!(state==='connected'&&connection?.dockable);$('live-toggle').hidden=state!=='connected';}
const mirrorOnly=()=>input('mirror-only').checked;
function windowCard(w:WindowInfo){const card=el('button',`window-card ${w.role}`);card.append(el('strong','',w.title),el('small','',`${w.app} · ${w.role==='main'?'main window':'floating window'}${w.minimized?' · minimized':''} · ${w.width} × ${w.height}`));card.onclick=()=>void action(async()=>{await host('capture.start',{handle:w.handle,mirror:mirrorOnly()});});return card;}
function connectedApplication(result:Connection){connection=result;scene.live=true;activeBridge=!!result.bridge;$('mode-interact').hidden=false;$('capture-name').textContent=result.title;$('capture-detail').textContent=result.app==='Screen'?'Everything on this display, pop-ups included':result.dockable?`${result.app} · hosted in App view`:`${result.app} · floating window, captured where the app shows it`;$('bridge-section').hidden=!activeBridge;$('bridge-status').textContent=activeBridge?'Registered live elements are available.':'';captureState('connected');openTab('capture');refresh();appLayout(result.appView===true);void action(refreshBridge);
  toast(result.dockable?`${result.title} is running in App view. Use it exactly as you normally would.`:result.app==='Screen'?'Recording the whole screen, pop-ups included.':`Mirroring ${result.title} live. It stays where it is; use Live 3D to operate it from the 3D view.`);}
function disconnected(message?:string,error=false){connection=undefined;if(appView)appLayout(false);scene.live=false;activeBridge=false;setMode(false);$('mode-interact').hidden=true;captureState('idle');refresh();if(message)toast(message,error);void action(refreshRecent);}
function showWaiting(state:Waiting){captureState('waiting');openTab('capture');
  $('waiting-title').textContent=state.reconnect?`Waiting for “${state.reconnect}”`:`Waiting for ${state.name}`;
  $('waiting-text').textContent=state.reconnect?`Open that window again from ${state.name}. Studio reconnects automatically.`:state.alreadyRunning?`${state.name} is already running, so Studio did not start a second copy. Open the window you want from ${state.name} itself. For apps in the notification area, use their tray icon.`:`${state.name} has started. If it opened to the notification area, open the window you want from its tray icon.`;
  const list=$('waiting-candidates');list.replaceChildren();if(state.candidates.length){list.append(el('p','list-label',state.candidates.length===1?'Or use this open window:':'It has several windows open. Choose one:'),...state.candidates.map(windowCard));}}
async function refreshWindows(){if(!native)return;const windows=await host<WindowInfo[]>('windows.list');$('window-list').replaceChildren(...(windows.length?windows.map(windowCard):[el('p','small-note','No open windows found.')]));}
async function refreshRecent(){if(!native)return;renderRecent(await host<RecentApp[]>('apps.recent'));}
function renderRecent(apps:RecentApp[]){$('recent-apps').replaceChildren(...(apps.length?[el('p','list-label','Recent apps')]:[]),...apps.map(app=>{const b=el('button','recent-app');b.append(el('strong','',app.name),el('small','',`${app.running?'Running · ':''}${app.kind==='store'?'Store app':app.kind==='android'?'Android app':app.path}`));b.onclick=()=>void action(()=>launchApp(app.path));return b;}));}
async function launchApp(path?:string){await startLaunch(path?'app.launchRecent':'app.launch',path?{path}:{});}
/** Every way of starting an app ends the same way: Studio connects, or waits for the app to open its window. */
async function startLaunch(method:string,data:Record<string,unknown>){if(exporting)return;const buttons=[$<HTMLButtonElement>('launch-app'),$<HTMLButtonElement>('launch-app-top'),$<HTMLButtonElement>('launch-android')];buttons.forEach(b=>b.disabled=true);try{openTab('capture');const result=await host<{state:string}&Partial<Waiting>|null>(method,{...data,mirror:mirrorOnly()});if(result?.state==='waiting')showWaiting(result as Waiting);}finally{buttons.forEach(b=>b.disabled=false);void action(refreshRecent);}}
function pickerList(items:HTMLElement[],empty:string){const list=el('div','picker-list');list.append(...(items.length?items:[el('p','small-note',empty)]));return list;}
function pickerItem(title:string,detail:string,run:()=>Promise<unknown>){const b=el('button','window-card picker-item');b.type='button';b.append(el('strong','',title),el('small','',detail));b.onclick=()=>void action(async()=>{$<HTMLDialogElement>('modal').close();await run();});return b;}
/** One place to start anything: installed desktop and Store apps, a program file, or an Android app. */
async function openLauncher(){if(exporting)return;await leaveAppView();const content=$('modal-content');$('modal-eyebrow').textContent='LAUNCH';$('modal-title').textContent='Which app are we filming?';
  const search=el('input','picker-search');search.placeholder='Search running and installed apps…';search.setAttribute('aria-label','Search apps');const holder=el('div');holder.append(el('p','small-note','Finding your apps…'));
  const footer=el('div','button-pair');const browse=el('button','',  'Browse for an .exe or shortcut…');browse.type='button';browse.onclick=()=>void action(async()=>{$<HTMLDialogElement>('modal').close();await launchApp();});const droid=el('button','','Android app…');droid.type='button';droid.onclick=()=>void action(openAndroid);footer.append(browse,droid);
  content.replaceChildren(search,holder,footer);if(!$<HTMLDialogElement>('modal').open)$<HTMLDialogElement>('modal').showModal();search.focus();
  // Apps that are already open come first: choosing one connects to its window without starting anything.
  let apps:InstalledApp[]=[],running:WindowInfo[]=[];const render=()=>{const q=search.value.trim().toLowerCase();const match=(...t:string[])=>!q||t.some(x=>x.toLowerCase().includes(q));
    const open=running.filter(w=>match(w.title,w.app)).map(w=>pickerItem(w.title,`${w.app} · running · ${w.role==='main'?'main window':'floating window'}${w.minimized?' · minimized':''}`,async()=>{openTab('capture');await host('capture.start',{handle:w.handle,mirror:mirrorOnly()});}));
    const installed=apps.filter(a=>match(a.name)).slice(0,150).map(a=>pickerItem(a.name,a.kind==='store'?'Store app':'Desktop app',()=>startLaunch('app.launchInstalled',{id:a.id})));
    holder.replaceChildren(...(open.length?[el('p','list-label','Running now'),pickerList(open,'')]:[]),el('p','list-label','Installed apps'),pickerList(installed,q?'No installed app matches.':'No apps found. Use Browse instead.'));};
  search.oninput=render;const [list,windows]=await Promise.allSettled([host<InstalledApp[]>('apps.installed'),host<WindowInfo[]>('windows.list')]);
  if(list.status==='fulfilled')apps=list.value;if(windows.status==='fulfilled')running=windows.value;if(list.status==='rejected'&&!running.length){holder.replaceChildren(el('p','small-note',(list.reason as Error).message));return;}render();}
/** Android phones (USB or Wi-Fi debugging) and emulators, through adb and a scrcpy live mirror. */
async function openAndroid(){await leaveAppView();const content=$('modal-content');$('modal-eyebrow').textContent='ANDROID';$('modal-title').textContent='Film an Android app';content.replaceChildren(el('p','small-note','Looking for devices…'));if(!$<HTMLDialogElement>('modal').open)$<HTMLDialogElement>('modal').showModal();
  const status=await host<AndroidStatus>('android.status');const parts:HTMLElement[]=[];
  const refresh=el('button','quiet','Refresh');refresh.type='button';refresh.onclick=()=>void action(openAndroid);
  if(!status.adb){parts.push(el('p','','Studio needs adb, Android’s own connection tool. Install scrcpy, which includes it, then press Refresh:'),el('pre','','winget install --id Genymobile.scrcpy'),el('p','small-note','Android Studio users already have adb; Studio finds it automatically.'),refresh);content.replaceChildren(...parts);return;}
  if(status.problem)parts.push(el('p','small-note',status.problem));
  const ready=status.devices.filter(d=>d.state==='device');
  for(const d of status.devices.filter(d=>d.state!=='device'))parts.push(el('p','small-note',`${d.model}: ${d.state==='unauthorized'?'accept “Allow USB debugging” on the device, then Refresh.':d.state}`));
  if(!ready.length){parts.push(el('p','','No device is connected. Plug in a phone with USB debugging on (Settings → Developer options), pair one over Wi-Fi, or start an emulator:'));
    parts.push(pickerList(status.virtualDevices.map(avd=>{const b=el('button','window-card picker-item');b.type='button';b.append(el('strong','',avd.replace(/_/g,' ')),el('small','','Start this emulator'));b.onclick=()=>void action(async()=>{await host('android.emulator',{avd});toast(`Starting ${avd}. Press Refresh when its home screen shows.`);});return b;}),status.emulator?'No virtual devices yet. Create one in Android Studio’s Device Manager.':'Android Studio’s emulator is not installed.'),refresh);content.replaceChildren(...parts);return;}
  if(!status.scrcpy){const choose=el('button','','Choose scrcpy.exe…');choose.type='button';choose.onclick=()=>void action(async()=>{await host('tools.scrcpy');await openAndroid();});parts.push(el('p','small-note','For a live mirror, install scrcpy (winget install --id Genymobile.scrcpy) or choose it. Without it Studio can still start apps, and emulator windows can be chosen under Open windows.'),choose);}
  const device=el('select','picker-search');device.setAttribute('aria-label','Android device');for(const d of ready){const o=el('option','',`${d.model}${d.emulator?' · emulator':''}`);o.value=d.serial;device.append(o);}
  const search=el('input','picker-search');search.placeholder='Search apps on the device…';search.setAttribute('aria-label','Search Android apps');const holder=el('div');
  let apps:string[]=[];const render=()=>{const q=search.value.trim().toLowerCase();const d=ready.find(x=>x.serial===device.value)!;holder.replaceChildren(pickerList(apps.filter(a=>!q||a.toLowerCase().includes(q)).slice(0,150).map(a=>pickerItem(a.split('.').at(-1)!.replace(/^./,c=>c.toUpperCase()),a,()=>startLaunch('android.launch',{serial:d.serial,package:a,model:d.model}))),'No app matches.'));};
  const load=async()=>{holder.replaceChildren(el('p','small-note','Reading the apps on the device…'));apps=await host<string[]>('android.apps',{serial:device.value});render();};
  device.onchange=()=>void action(load);search.oninput=render;parts.push(device,search,holder,refresh);content.replaceChildren(...parts);search.focus();await load();}
$('launch-app').onclick=()=>void action(openLauncher);$('launch-android').onclick=()=>void action(openAndroid);
$('capture-screen').onclick=()=>void action(async()=>{await host('capture.start',{handle:'screen'});});$('launch-app-top').onclick=()=>void action(openLauncher);
$('cancel-wait').onclick=()=>void action(async()=>{await host('app.cancelWait');captureState('idle');});
$('refresh-windows').onclick=()=>void action(refreshWindows);
$('slot-restore').onclick=()=>void action(()=>host('app.unminimize'));
$('live-toggle').onclick=()=>{scene.live=!scene.live;refresh(false);};
$('disconnect').onclick=()=>void action(async()=>{if(recording)await finishRecording();await host('capture.stop');});
/** Adds captured media without leaving App view. An empty shot uses it straight away. */
async function addCapture(asset:Asset){commit(p=>{p.assets.push(asset);const shot=p.shots.find(s=>s.id===current().id)!;if(!shot.assetId)shot.assetId=asset.id;});selectedAsset=asset.id;await scene.prepare(project);}
$('capture-still').onclick=()=>void action(async()=>{const asset=await host<Asset>('capture.snapshot');await addCapture(asset);toast(`Still saved to your assets${current().assetId===asset.id?' and used in this shot':''}.`);});
function describeTake(asset:Asset){const seconds=(asset.recordedFrames??0)/(asset.recordedFps??30);$('record-status').textContent=`Last take: ${seconds.toFixed(1)} s · ${asset.uniqueFrames} of ${asset.recordedFrames} frames show a change; the rest hold the screen steady.`;return seconds;}
async function finishRecording(){$('record').textContent='Saving…';try{const asset=await host<Asset>('capture.finish');await addCapture(asset);toast(`Recording saved · ${describeTake(asset).toFixed(1)} s at ${asset.recordedFps} fps.`);}finally{recording=false;$('record').textContent='● Record';$('record').classList.remove('recording');}}
$('record').onclick=()=>void action(async()=>{if(demoRunning)throw Error('A demo is recording right now.');if(recording)await finishRecording();else{await host('capture.record',{fps:project.fps});recording=true;$('record').textContent='■ Finish';$('record').classList.add('recording');$('record-status').textContent=`Recording at ${project.fps} fps. Studio records the picture only, not sound.`;toast('Recording. Use your app normally, then press Finish.');}});
async function refreshBridge(){if(!activeBridge){$('bridge-layers').replaceChildren();return;}const result=await host<{layers:Array<{id:string;name:string}>}>('bridge.layers');$('bridge-layers').replaceChildren(...result.layers.map(item=>{const button=el('button','window-card','＋ '+item.name);button.onclick=()=>{selectedLayer=uid();const layer:Layer={id:selectedLayer,name:item.name,position:[2,.6,1],rotation:[0,-.2,0],scale:1,opacity:1,crop:[0,0,1,1],liveElementId:item.id,attach:'world',motion:'float',delay:0};editShot(s=>s.layers.push(layer));};return button;}));}
setInterval(()=>{if(!activeBridge||layerPolling||exporting)return;const layers=current().layers.filter(l=>l.liveElementId);if(!layers.length)return;layerPolling=true;void action(async()=>{try{for(const l of layers){const data=await host('bridge.frame',{elementId:l.liveElementId});await scene.updateLive(data.png,data.width,data.height,l.liveElementId);}redraw();}finally{layerPolling=false;}});},250);

// ── Editor ────────────────────────────────────────────────────────────────────────────────────────────────────
const deg=(radians:number)=>Math.round(radians*180/Math.PI);
function refresh(full=true){redraw();const duration=totalDuration(project),active=shotAt(project,time),shot=active.shot;
  $('shot-breadcrumb').textContent=appView&&connection?connection.title:shot.name;$('shot-count').textContent=`${project.shots.length} shots`;$('timecode').replaceChildren(document.createTextNode(fmt(time)+'.'+String(Math.floor((time%1)*100)).padStart(2,'0')+' '),el('em','',`/ ${fmt(duration)}`));input('scrubber').max=String(duration);input('scrubber').value=String(time);$('end-time').textContent=fmt(duration);$('resolution-label').textContent=`${project.width} × ${project.height} · ${project.fps} fps`;
  const asset=project.assets.find(a=>a.id===shot.assetId);$('source-note').textContent=scene.live&&connection?`LIVE · ${connection.title}`:asset?`SHOT MEDIA · ${asset.name}`:'NO FOOTAGE IN THIS SHOT · Take a still or record your app';$('live-toggle').textContent=scene.live?'Show shot media':'Show live app';
  if(!full){document.querySelectorAll('.shot-card').forEach((n,i)=>n.classList.toggle('active',i===active.index));return;}
  input('project-name').value=project.name;input('shot-name').value=shot.name;input('shot-duration').value=String(shot.duration);input('source-start').value=String(shot.sourceStart);select('transition').value=shot.transition;
  select('device').value=shot.device;select('finish').value=shot.finish;select('lighting').value=shot.lighting;input('light-intensity').value=String(shot.lightIntensity);input('background').value=shot.background;input('accent').value=shot.accent;
  input('pose-turn').value=String(deg(shot.deviceRotation[1]));input('pose-tilt').value=String(deg(shot.deviceRotation[0]));input('pose-lift').value=String(shot.devicePosition[1]);input('pose-shift').value=String(shot.devicePosition[0]);
  $<HTMLTextAreaElement>('title').value=shot.title;input('subtitle').value=shot.subtitle;select('title-layout').value=shot.titleLayout;select('title-motion').value=shot.titleMotion;input('title-scale').value=String(shot.titleScale);input('title-color').value=shot.titleColor;
  select('easing').value=shot.easing;select('camera-preset').value='';input('explode').value=String(shot.explode);input('depth-of-field').value=String(shot.depthOfField);input('clean-tint').value=String(shot.cleanTint);
  select('format').value=`${project.width}x${project.height}`;select('fps').value=String(project.fps);input('volume').value=String(project.audioVolume);
  document.querySelectorAll<HTMLButtonElement>('[data-stage]').forEach(b=>b.classList.toggle('active',b.dataset.stage===shot.stage));
  $('camera-keys').textContent=`${shot.camera.length} keyframes · ${shot.camera.map(k=>k.time.toFixed(1)+'s').join(' / ')}`;$<HTMLButtonElement>('undo').disabled=!history.length;$<HTMLButtonElement>('redo').disabled=!future.length;
  $('shot-list').replaceChildren(...project.shots.map((s,i)=>{const card=el('button',`shot-card${i===active.index?' active':''}`);card.draggable=true;const extras=[s.layers.some(l=>l.attach==='screen')?'exploded':'',s.callouts.length?`${s.callouts.length} callout${s.callouts.length>1?'s':''}`:'',s.assetId?'':'no footage'].filter(Boolean).join(' · ');card.append(el('span','shot-number',String(i+1).padStart(2,'0')),el('span','shot-name',s.name),el('span','shot-meta',`${s.duration.toFixed(1)}s · ${s.device==='none'?'app only':s.device} · ${s.stage}${extras?' · '+extras:''}`),el('i','shot-art'));card.onclick=()=>{stopPlayback();setTime(shotStart(project,i));refresh();};card.ondragstart=e=>e.dataTransfer?.setData('application/shot',s.id);card.ondragover=e=>e.preventDefault();card.ondrop=e=>{e.preventDefault();const id=e.dataTransfer?.getData('application/shot');if(id)commit(p=>{const from=p.shots.findIndex(x=>x.id===id),[moved]=p.shots.splice(from,1);p.shots.splice(i,0,moved);});};return card;}));
  $('asset-count').textContent=String(project.assets.length);$('asset-list').replaceChildren(...project.assets.map(asset=>{const card=el('div',`asset-card${selectedAsset===asset.id?' active':''}`);card.draggable=true;card.ondragstart=e=>e.dataTransfer?.setData('application/asset',asset.id);const thumb=asset.kind==='image'?el('img','asset-thumb') as HTMLImageElement:el('div','asset-thumb',asset.kind==='audio'?'♫':asset.kind==='model'?'◇':'▶');if(thumb instanceof HTMLImageElement){thumb.src=asset.url;thumb.alt=asset.name;}const desc=el('div');desc.append(el('strong','',asset.name),el('small','',asset.kind.toUpperCase()+(asset.duration?` · ${fmt(asset.duration)}`:'')+(asset.width?` · ${asset.width}×${asset.height}`:'')));const buttons=el('div','asset-actions');const use=el('button','',asset.kind==='audio'?'Use soundtrack':asset.kind==='model'?'In scene':'Use in shot');use.onclick=e=>{e.stopPropagation();useAsset(asset.id);};const relink=el('button','quiet','Relink');relink.onclick=e=>{e.stopPropagation();void action(async()=>{const replacement=await host<Asset|null>('assets.relink');if(replacement)commit(p=>{const index=p.assets.findIndex(a=>a.id===asset.id);replacement.id=asset.id;p.assets[index]=replacement;});});};buttons.append(use,relink);card.append(thumb,desc,buttons);card.onclick=()=>{selectedAsset=asset.id;refresh();};return card;}));
  $('audio-name').textContent=project.assets.find(a=>a.id===project.audioId)?.name??'Add a soundtrack from your assets';$('remove-audio').hidden=!project.audioId;
  $('layer-list').replaceChildren(...shot.layers.map(l=>{const row=el('div',`layer-item${l.id===selectedLayer?' active':''}`,`${l.liveElementId?'◉':l.attach==='screen'?'▣':'▧'} ${l.name}`);row.append(el('small','',l.attach==='screen'?l.motion:'free'));row.onclick=()=>{selectedLayer=selectedLayer===l.id?'':l.id;refresh();};return row;}));
  $('freeze-layers').hidden=!shot.layers.some(l=>l.liveElementId);
  const layer=shot.layers.find(l=>l.id===selectedLayer);$('layer-editor').hidden=!layer;if(layer){input('layer-name').value=layer.name;select('layer-motion').value=layer.motion;input('layer-delay').value=String(layer.delay);['x','y','z'].forEach((key,i)=>input('layer-'+key).value=String(layer.position[i]));input('layer-scale').value=String(layer.scale);input('layer-opacity').value=String(layer.opacity);['x','y','w','h'].forEach((key,i)=>input('crop-'+key).value=String(layer.crop[i]));}
  renderFocus(shot);resize();
}
/** Callouts and focus regions, editable in place. */
function renderFocus(shot:Shot){
  const number=(value:number,apply:(v:number)=>void,max=shot.duration)=>{const n=el('input');n.type='number';n.min='0';n.max=String(max);n.step='0.1';n.value=String(value);n.title='Seconds into the shot';n.onchange=()=>apply(Number(n.value));return n;};
  const remove=(run:()=>void)=>{const b=el('button','quiet','✕');b.title='Remove';b.onclick=run;return b;};
  $('callout-list').replaceChildren(...shot.callouts.map(c=>{const row=el('div','item-row');const text=el('input');text.value=c.text;text.style.width='auto';text.style.flex='1';text.onchange=()=>editShot(s=>{s.callouts.find(x=>x.id===c.id)!.text=text.value;});row.append(el('b','','●'),text,number(c.delay,v=>editShot(s=>{s.callouts.find(x=>x.id===c.id)!.delay=v;}),300),remove(()=>editShot(s=>{s.callouts=s.callouts.filter(x=>x.id!==c.id);})));return row;}));
  $('highlight-list').replaceChildren(...shot.highlights.map(h=>{const row=el('div','item-row');const style=el('select');for(const v of ['spotlight','outline','zoom']){const o=el('option','',v);o.value=v;style.append(o);}style.value=h.style;style.onchange=()=>editShot(s=>{s.highlights.find(x=>x.id===h.id)!.style=style.value as typeof h.style;});
    row.append(style,number(h.start,v=>editShot(s=>{const x=s.highlights.find(x=>x.id===h.id)!;x.start=Math.min(v,x.end);})),number(h.end,v=>editShot(s=>{const x=s.highlights.find(x=>x.id===h.id)!;x.end=Math.max(v,x.start);})),remove(()=>editShot(s=>{s.highlights=s.highlights.filter(x=>x.id!==h.id);})));return row;}));
  $('focus-empty').hidden=!!(shot.callouts.length||shot.highlights.length);
}
function useAsset(id:string){const asset=project.assets.find(a=>a.id===id);if(!asset)return;selectedAsset=id;commit(p=>{if(asset.kind==='audio')p.audioId=id;else if(asset.kind!=='model'){const s=p.shots.find(s=>s.id===current().id)!;s.assetId=id;} });if(asset.kind==='image'||asset.kind==='video'){scene.live=false;refresh(false);if(appView)void action(leaveAppView);}}
function resize(){if(exporting||appView)return;const container=$('canvas-container'),r=container.getBoundingClientRect();const ratio=project.width/project.height;const width=Math.max(1,Math.floor(Math.min(r.width,r.height*ratio)));scene.canvas.style.width=width+'px';scene.canvas.style.height=Math.floor(width/ratio)+'px';const pixels=Math.min(1920,width*devicePixelRatio);scene.resize(pixels,pixels/ratio);redraw();}
new ResizeObserver(resize).observe($('canvas-container'));
function field(id:string,apply:(s:Shot,value:string)=>void,rebuild=true,event='change'){$(id).addEventListener(event,()=>{const value=($<HTMLInputElement>(id)).value;editShot(s=>apply(s,value),rebuild);});}
field('shot-name',(s,v)=>s.name=v,false);field('shot-duration',(s,v)=>{const next=Number(v);s.camera.forEach(k=>k.time=k.time/s.duration*next);s.highlights.forEach(h=>{h.start=Math.min(h.start,next);h.end=Math.min(h.end,next);});s.duration=next;});field('source-start',(s,v)=>s.sourceStart=Number(v),false);
field('transition',(s,v)=>s.transition=v as Shot['transition'],false);field('device',(s,v)=>s.device=v as Shot['device']);field('finish',(s,v)=>s.finish=v as Shot['finish']);field('lighting',(s,v)=>s.lighting=v as Shot['lighting'],false);field('light-intensity',(s,v)=>s.lightIntensity=Number(v),false);
field('title',(s,v)=>s.title=v,false);field('subtitle',(s,v)=>s.subtitle=v,false);field('title-layout',(s,v)=>s.titleLayout=v as Shot['titleLayout'],false);field('title-motion',(s,v)=>s.titleMotion=v as Shot['titleMotion'],false);field('title-scale',(s,v)=>s.titleScale=Number(v),false);field('title-color',(s,v)=>s.titleColor=v,false);
field('accent',(s,v)=>s.accent=v);field('background',(s,v)=>s.background=v);field('easing',(s,v)=>s.easing=v as Shot['easing'],false);field('explode',(s,v)=>s.explode=Number(v),false);field('depth-of-field',(s,v)=>s.depthOfField=Number(v),false);field('clean-tint',(s,v)=>s.cleanTint=Number(v),false);
field('pose-turn',(s,v)=>s.deviceRotation[1]=Number(v)*Math.PI/180,false);field('pose-tilt',(s,v)=>s.deviceRotation[0]=Number(v)*Math.PI/180,false);field('pose-lift',(s,v)=>s.devicePosition[1]=Number(v),false);field('pose-shift',(s,v)=>s.devicePosition[0]=Number(v),false);
// Sliders preview while dragging and are committed once on release, so undo steps stay meaningful.
for(const [id,apply] of [['pose-turn',(s:Shot,v:number)=>s.deviceRotation[1]=v*Math.PI/180],['pose-tilt',(s:Shot,v:number)=>s.deviceRotation[0]=v*Math.PI/180],['pose-lift',(s:Shot,v:number)=>s.devicePosition[1]=v],['pose-shift',(s:Shot,v:number)=>s.devicePosition[0]=v],['explode',(s:Shot,v:number)=>s.explode=v],['light-intensity',(s:Shot,v:number)=>s.lightIntensity=v],['title-scale',(s:Shot,v:number)=>s.titleScale=v]] as const)
  input(id).addEventListener('input',()=>{sliderBase??=structuredClone(project);apply(current(),Number(input(id).value));});
$('reset-pose').onclick=()=>editShot(s=>{s.deviceRotation=[0,0,0];s.devicePosition=[0,0,0];},false);
input('project-name').onchange=()=>commit(p=>p.name=input('project-name').value,false);
select('format').onchange=()=>commit(p=>{const [w,h]=select('format').value.split('x').map(Number);p.width=w;p.height=h;});select('fps').onchange=()=>commit(p=>p.fps=Number(select('fps').value) as 30|60,false);input('volume').onchange=()=>commit(p=>p.audioVolume=Number(input('volume').value),false);
document.querySelectorAll<HTMLButtonElement>('[data-stage]').forEach(b=>b.onclick=()=>editShot(s=>s.stage=b.dataset.stage as Shot['stage']));
function openTab(name:string){document.querySelectorAll<HTMLButtonElement>('[data-tab]').forEach(x=>x.classList.toggle('active',x.dataset.tab===name));for(const tab of ['assets','capture','ideas'])$('tab-'+tab).hidden=tab!==name;if(name==='capture')void action(refreshWindows);}
document.querySelectorAll<HTMLButtonElement>('[data-tab]').forEach(b=>b.onclick=()=>openTab(b.dataset.tab!));
$('new-project').onclick=()=>void action(async()=>{await leaveAppView();modal('A fresh start','Your current project is autosaved. Save a project file first if you want to keep it.',[{label:'Save project',run:async()=>{await host('project.save',{project});}},{label:'Start a new project',run:()=>replace(newProject())}]);});
$('open-project').onclick=()=>void action(async()=>{await leaveAppView();const p=await host<Project|null>('project.open');if(p){replace(p);await scene.prepare(project);toast('Project opened.');}});
$('save-project').onclick=()=>void action(async()=>{const result=await host('project.save',{project});if(result?.path)toast('Project and media saved together.');});
$('import-assets').onclick=()=>void action(async()=>{const assets=await host<Asset[]>('assets.import');if(!assets.length)return;commit(p=>p.assets.push(...assets));await scene.prepare(project);useAsset(assets[0].id);toast(`${assets.length} asset${assets.length===1?'':'s'} imported.`);});
$('canvas-container').ondragover=e=>e.preventDefault();$('canvas-container').ondrop=e=>{e.preventDefault();const id=e.dataTransfer?.getData('application/asset');if(id)useAsset(id);};
$('add-shot').onclick=()=>{const s=newShot(`Shot ${project.shots.length+1}`);s.assetId=current().assetId;commit(p=>{s.camera=cameraPreset('reveal',s.duration,frameFor(s,p));p.shots.push(s);});setTime(totalDuration(project)-s.duration);refresh();};
$('duplicate-shot').onclick=()=>{const shot=structuredClone(current());shot.id=uid();shot.layers.forEach(l=>l.id=uid());shot.callouts.forEach(c=>c.id=uid());shot.highlights.forEach(h=>h.id=uid());shot.name+=' copy';commit(p=>p.shots.splice(p.shots.findIndex(s=>s.id===current().id)+1,0,shot));};
$('delete-shot').onclick=()=>{if(project.shots.length===1){toast('Keep at least one shot.');return;}const id=current().id;commit(p=>p.shots=p.shots.filter(s=>s.id!==id));};
$('play').onclick=()=>{playing=!playing;manualCamera=false;$('play').textContent=playing?'Ⅱ':'▶';};input('scrubber').oninput=()=>{stopPlayback();setTime(Number(input('scrubber').value));};
$('previous').onclick=()=>{const at=shotAt(project,time);setTime(shotStart(project,Math.max(0,at.index-1)));refresh();};$('next').onclick=()=>{const at=shotAt(project,time);setTime(shotStart(project,Math.min(project.shots.length-1,at.index+1)));refresh();};
$('undo').onclick=()=>undo();$('redo').onclick=()=>undo(true);$('remove-audio').onclick=()=>commit(p=>delete p.audioId,false);
function applyPreset(motion:string){manualCamera=false;editShot((s,p)=>{s.camera=cameraPreset(motion,s.duration,frameFor(s,p));});}
select('camera-preset').onchange=()=>{if(select('camera-preset').value)applyPreset(select('camera-preset').value);};
$('fit-app').onclick=()=>{applyPreset('hero');toast('The camera now frames your app as large as the format allows.');};
function cameraKey(at:number){const state=scene.cameraState(at);editShot(s=>{s.camera=s.camera.filter(k=>Math.abs(k.time-at)>.001);s.camera.push(state);s.camera.sort((a,b)=>a.time-b.time);},false);manualCamera=false;toast('Camera keyframe saved.');}
$('camera-start').onclick=()=>cameraKey(0);$('camera-end').onclick=()=>cameraKey(current().duration);$('camera-key').onclick=()=>cameraKey(shotAt(project,time).local);$('reset-camera').onclick=()=>{manualCamera=false;};$('mode-app').onclick=()=>void action(()=>showAppView(true));$('mode-compose').onclick=()=>void action(async()=>{await showAppView(false);setMode(false);});$('mode-interact').onclick=()=>void action(async()=>{await showAppView(false);setMode(true);});
$('focus-mode').onclick=()=>{document.body.classList.toggle('focus-mode');requestAnimationFrame(resize);};
$('add-layer').onclick=()=>{const asset=project.assets.find(a=>a.id===selectedAsset&&['image','video'].includes(a.kind));const layer:Layer={id:uid(),name:'Floating detail',assetId:asset?.id??current().assetId,position:[2,.5,1.1],rotation:[0,-.2,0],scale:1,opacity:1,crop:[0,0,1,1],attach:'world',motion:'float',delay:0};selectedLayer=layer.id;editShot(s=>s.layers.push(layer));};
$('freeze-layers').onclick=()=>void action(async()=>{const snapshots:Array<{id:string;asset:Asset}>=[];for(const layer of current().layers.filter(l=>l.liveElementId)){const asset=await host<Asset>('bridge.freeze',{elementId:layer.liveElementId,name:layer.name});snapshots.push({id:layer.id,asset});}if(!snapshots.length){toast('This shot has no live layers.');return;}commit(p=>{const shot=p.shots.find(s=>s.id===current().id)!;for(const {id,asset}of snapshots){p.assets.push(asset);const layer=shot.layers.find(l=>l.id===id)!;layer.assetId=asset.id;delete layer.liveElementId;}});await scene.prepare(project);toast('Live layers frozen into real captured assets.');});
function layerField(id:string,apply:(layer:Layer,value:string)=>void){$(id).onchange=()=>editShot(s=>{const layer=s.layers.find(l=>l.id===selectedLayer);if(layer)apply(layer,($<HTMLInputElement>(id)).value);});}
layerField('layer-name',(l,v)=>l.name=v);layerField('layer-motion',(l,v)=>l.motion=v as Layer['motion']);layerField('layer-delay',(l,v)=>l.delay=Number(v));['x','y','z'].forEach((key,i)=>layerField('layer-'+key,(l,v)=>l.position[i]=Number(v)));layerField('layer-scale',(l,v)=>l.scale=Number(v));layerField('layer-opacity',(l,v)=>l.opacity=Number(v));['x','y','w','h'].forEach((key,i)=>layerField('crop-'+key,(l,v)=>l.crop[i]=Number(v)));$('remove-layer').onclick=()=>{const id=selectedLayer;selectedLayer='';editShot(s=>{s.layers=s.layers.filter(l=>l.id!==id);});};

// ── Sections, callouts and focus picked on the app's own picture ─────────────────────────────────────────────────
const sections=new SectionsEditor($<HTMLDialogElement>('sections-dialog'),$<HTMLCanvasElement>('sections-canvas'),$('section-items'),$('section-hint'),input('section-label'),()=>current(),{
  add:(tool:Tool,area:Rect,label:string)=>editShot(s=>{
    if(tool==='section'){const layer=sectionLayer(label,area,{delay:.2+s.layers.filter(l=>l.attach==='screen').length*.15});s.layers.push(layer);if(s.explode===0)s.explode=.6;}
    else if(tool==='crop'){s.crop=area;}
    else if(tool==='callout')s.callouts.push({id:uid(),text:label,x:area[0],y:area[1],delay:.8+s.callouts.length*.4});
    else s.highlights.push({id:uid(),rect:area,start:Math.min(.8,s.duration/4),end:Math.max(s.duration/2,s.duration-.6),style:tool});}),
  remove:(kind,id)=>editShot(s=>{if(kind==='crop')delete s.crop;else if(kind==='layer')s.layers=s.layers.filter(l=>l.id!==id);else if(kind==='callout')s.callouts=s.callouts.filter(c=>c.id!==id);else s.highlights=s.highlights.filter(h=>h.id!==id);}),
});
$('open-sections').onclick=()=>void action(async()=>{await leaveAppView();stopPlayback();sections.open(await scene.picture(project,current()));});
$('sections-done').onclick=$('sections-close').onclick=()=>$<HTMLDialogElement>('sections-dialog').close();

// ── Direct: quick commands, AI scripts and demos the real app performs ────────────────────────────────────────────
$('run-command').onclick=()=>void action(async()=>{const text=$<HTMLTextAreaElement>('command').value.trim();if(!text){$('command-result').textContent=commandHelp;return;}
  const {edits,notes}=parseCommand(text,project,shotAt(project,time).index);$('command-result').textContent=notes.join(' · ');if(!edits.length)return;
  const result=applyEdits(project,edits);manualCamera=false;commit(p=>Object.assign(p,result.project));toast('Done. Undo with Ctrl+Z.');});
$('command').addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();$('run-command').click();}});
$('copy-prompt').onclick=()=>void action(async()=>{const elements=connection?await host<{elements:UiElement[]}>('automation.elements').then(r=>r.elements).catch(()=>undefined):undefined;
  await host('clipboard.copy',{text:studioPrompt(project,{goal:$<HTMLTextAreaElement>('goal').value.trim()||undefined,app:connection?.title,elements})});toast(connection?`Prompt copied with ${elements?.length??0} controls from ${connection.title}. Paste it into your AI chat.`:'Prompt copied. Paste it into your AI chat.');});
function readScript(){const text=$<HTMLTextAreaElement>('storyboard').value.trim().replace(/^```(?:json)?\s*/,'').replace(/\s*```$/,'');return JSON.parse(text.slice(text.indexOf('{'),text.lastIndexOf('}')+1));}
$('preview-storyboard').onclick=()=>void action(async()=>{pendingScript=readScript();const script=applyScript(project,pendingScript);$('storyboard-summary').replaceChildren(...script.summary.map(s=>el('li','',s)));$('storyboard-review').hidden=false;$('apply-storyboard').hidden=!script.summary.some(s=>!s.startsWith('Demo'));$('run-demo').hidden=!script.demo;if(script.demo&&!connection)$('storyboard-summary').append(el('li','','Launch your app in the Capture tab before running the demo.'));});
function applyPending(){const script=applyScript(project,pendingScript);if(script.project!==project){manualCamera=false;commit(p=>Object.assign(p,script.project));setTime(Math.min(time,totalDuration(project)-.0001));}return script;}
$('apply-storyboard').onclick=()=>void action(async()=>{applyPending();$('storyboard-review').hidden=true;toast('Applied. Every change is editable and can be undone.');});
$('run-demo').onclick=()=>void action(async()=>{const script=applyPending();if(script.demo)await runDemo(script.demo);$('storyboard-review').hidden=true;});
async function runDemo(demo:Demo){
  if(!connection)throw Error('Launch your app in the Capture tab first.');if(recording)throw Error('Finish the current recording first.');
  if(connection.dockable&&!appView)await showAppView(true);
  demoRunning=true;$('demo-status').hidden=false;$('demo-step').textContent='Getting ready…';
  try{const result=await host<{asset?:Asset;completed:boolean;message?:string}>('automation.run',{demo,fps:project.fps});
    if(result.asset){await addCapture(result.asset);describeTake(result.asset);}
    toast(result.message??(result.asset?'Demo recorded and added to your assets.':'Demo finished.'),!result.completed);}
  finally{demoRunning=false;$('demo-status').hidden=true;}
}
$('stop-demo').onclick=()=>void action(()=>host('automation.stop'));
// Recipes: a complete ad from the captures in one click, in the same validated format as an AI script.
$('recipes').replaceChildren(...RECIPES.map(recipe=>{const b=el('button','recipe');b.append(el('strong','',recipe.name),el('small','',recipe.description));
  b.onclick=()=>void action(async()=>{const board=recipeStoryboard(recipe,project,{appName:connection?.app});const script=applyScript(project,{version:1,shots:board.shots,edits:[{op:'setProject',set:{width:recipe.width,height:recipe.height}}]});
    manualCamera=false;commit(p=>Object.assign(p,script.project));setTime(0);toast(`${recipe.name}: ${script.project.shots.length} shots, ${totalDuration(script.project).toFixed(0)} s. Generate a soundtrack next, then preview with Space.`);});return b;}));
$('make-music').onclick=()=>void action(async()=>{const plan=recipeMusic({shots:project.shots as unknown as Array<Record<string,unknown>>});toast('Composing a soundtrack on this PC…');
  const asset=await host<Asset>('music.generate',plan);commit(p=>{p.assets.push(asset);p.audioId=asset.id;},false);toast(`Soundtrack ready: ${plan.bars*2} s at 120 BPM, cut to your shots.`);});

// ── Dialogs and export ────────────────────────────────────────────────────────────────────────────────────────
function modal(title:string,description:string,buttons:Array<{label:string;run:()=>unknown}>){$('modal-title').textContent=title;const content=$('modal-content');content.replaceChildren(el('p','',description));for(const item of buttons){const button=el('button','full',item.label);button.type='button';button.onclick=()=>void action(async()=>{await item.run();$<HTMLDialogElement>('modal').close();});content.append(button);}$<HTMLDialogElement>('modal').showModal();}
$('settings').onclick=()=>void action(async()=>{await leaveAppView();const content=$('modal-content');$('modal-title').textContent='Your local studio';content.replaceChildren(el('p','','Capture, media, and rendering stay on this computer.'));
  const describe=()=>`FFmpeg: ${tools.ffmpeg??'Not found'}\nBlender: ${tools.blender??'Not found'}\nadb (Android): ${tools.adb??'Not found'}\nscrcpy (Android mirror): ${tools.scrcpy??'Not found'}`;const pre=el('pre','',describe());content.append(pre);const button=el('button','full','Choose Blender executable…');button.type='button';button.onclick=()=>void action(async()=>{const result=await host('tools.blender');tools.blender=result.path;pre.textContent=describe();});const scrcpy=el('button','full','Choose scrcpy executable…');scrcpy.type='button';scrcpy.onclick=()=>void action(async()=>{const result=await host('tools.scrcpy');tools.scrcpy=result.path;pre.textContent=describe();});
  const toggle=el('label','check');const box=el('input');box.type='checkbox';box.checked=advanced();box.onchange=()=>setAdvanced(box.checked);toggle.append(box,' Show advanced controls everywhere');content.append(button,scrcpy,toggle);$('modal-eyebrow').textContent='STUDIO';$<HTMLDialogElement>('modal').showModal();});
/** Simple mode keeps the essentials on screen; advanced controls (pose, lens, keyframes, layer maths…) are one switch away. */
function advanced(){try{return localStorage.getItem('studio-advanced')==='1';}catch{return false;}}
function setAdvanced(on:boolean){document.body.classList.toggle('advanced',on);try{localStorage.setItem('studio-advanced',on?'1':'0');}catch{}document.querySelectorAll<HTMLButtonElement>('[data-detail]').forEach(b=>b.classList.toggle('active',(b.dataset.detail==='advanced')===on));}
document.querySelectorAll<HTMLButtonElement>('[data-detail]').forEach(b=>b.onclick=()=>setAdvanced(b.dataset.detail==='advanced'));setAdvanced(advanced());
$('snapshot').onclick=()=>void action(async()=>{await leaveAppView();stopPlayback();const live=scene.live&&!!connection;if(!live&&!current().assetId)throw Error('This shot has no app footage yet. Take a still or record your app first.');const old={w:scene.width,h:scene.height};scene.resize(project.width,project.height);try{await scene.render(project,time,{camera:!manualCamera,export:!live});await host('snapshot.save',{png:scene.png(),name:project.name});toast('Screenshot saved.');}finally{scene.resize(old.w,old.h);resize();}});
function showProblems(problems:string[]){const content=$('modal-content');$('modal-title').textContent='Almost ready.';const list=el('ul');list.id='problem-list';list.append(...problems.map(p=>el('li','',p)));content.replaceChildren(el('p','','Exports use only real captured media. Fix these first:'),list);if(!$<HTMLDialogElement>('modal').open)$<HTMLDialogElement>('modal').showModal();}
$('export').onclick=()=>void action(async()=>{if(exporting)return;await leaveAppView();const content=$('modal-content');const problems=exportProblems(project);
  if(recording||demoRunning)problems.unshift('Recording is still running. Finish it first.');
  if(problems.length){showProblems(problems);return;}
  $('modal-title').textContent='Ready for the spotlight.';content.replaceChildren(el('p','',`${project.width} × ${project.height} · ${project.fps} fps · ${totalDuration(project).toFixed(1)} seconds.`));for(const [mode,title,description]of [['fast','Studio render','Everything you see in the preview: sections, callouts, spotlights and zooms, frame by frame.'],['cinematic','Cinematic render (Blender)','Path-traced lighting, depth of field and camera motion blur. Much slower; needs Blender.']]){const b=el('button','export-choice');b.type='button';b.append(el('strong','',title),el('p','',description));b.onclick=()=>void action(async()=>{if(mode==='cinematic'){const issues=exportProblems(project,'cinematic');if(issues.length){showProblems(issues);return;}}await runExport(mode);});content.append(b);}$<HTMLDialogElement>('modal').showModal();});
async function runExport(mode:string){
  // The dialog stays open while exporting: it holds the progress and the only Cancel button.
  const closeX=document.querySelector<HTMLButtonElement>('#modal .modal-head button')!;closeX.disabled=true;
  stopPlayback();exporting=true;cancelExport=false;const frozen=structuredClone(project),oldTime=time;const content=$('modal-content');content.replaceChildren(el('p','',mode==='cinematic'?'Rendering with Blender…':'Preparing your media…'));const progress=el('progress') as HTMLProgressElement;progress.max=1;progress.value=0;const label=el('p','','');const cancel=el('button','full','Cancel export');cancel.type='button';cancel.onclick=()=>{cancelExport=true;void host('export.cancel').catch(()=>{});};content.append(progress,label,cancel);
  try{
    if(mode==='cinematic'){const titles=frozen.shots.map(s=>scene.titlePng(s,frozen.width,frozen.height));const result=await host('export.cinematic',{project:frozen,titles});if(result?.path){toast('Cinematic film exported.');content.replaceChildren(el('p','',`Saved: ${result.path}`));}else $<HTMLDialogElement>('modal').close();return;}
    const result=await host<{cancelled?:boolean;count:number;decoded:Record<string,{url:string;fps:number;count:number}>}>('export.begin',{project:frozen});if(result.cancelled){$<HTMLDialogElement>('modal').close();return;}
    scene.resize(frozen.width,frozen.height);for(let i=0;i<result.count;i++){if(cancelExport)throw Error('Export cancelled.');await scene.render(frozen,i/frozen.fps,{export:true,decoded:result.decoded});await host('export.frame',{index:i,png:scene.png()});progress.value=(i+1)/result.count;label.textContent=`${i+1} / ${result.count} frames · ${Math.round(progress.value*100)}%`;}
    const complete=await host('export.finish');content.replaceChildren(el('p','',`Saved: ${complete.path}`));toast('Your film is exported.');
  }catch(e){await host('export.cancel').catch(()=>{});content.replaceChildren(el('p','',(e as Error).message));throw e;}finally{exporting=false;closeX.disabled=false;time=oldTime;scene.invalidate();resize();refresh();}
}
$<HTMLDialogElement>('modal').addEventListener('cancel',event=>{if(exporting){event.preventDefault();toast('Use Cancel export to stop the renderer.');}});
window.chrome?.webview?.addEventListener('message',event=>{const message=event.data;if(message.type==='event'){const data=message.data;
  switch(message.name){
    case 'app.connected':connectedApplication(data);break;
    case 'app.waiting':if(!connection)showWaiting(data);break;
    case 'app.waitEnded':captureState('idle');toast(data.message,true);break;
    case 'app.closed':if(recording)void action(finishRecording);disconnected(data.message,true);break;
    case 'app.disconnected':disconnected();break;
    case 'app.viewError':appLayout(false);toast(data.message,true);break;
    case 'app.minimized':$('slot-message').textContent=data.minimized?'Your app is minimized.':'Bringing your app into App view…';$('slot-restore').hidden=!data.minimized;break;
    case 'capture.frame':if(scene.interactive)settle=Math.max(settle,2);void scene.updateLive(data.jpeg,data.width,data.height,undefined,'image/jpeg').then(()=>{if(scene.live)redraw();}).catch(e=>toast(e.message,true));break;
    case 'studio.error':toast(data.message,true);break;
    case 'capture.error':toast(data.message,true);break;
    case 'capture.recordError':toast(data.message,true);if(recording)void action(finishRecording);break;
    case 'automation.step':$('demo-step').textContent=`Step ${data.index+1} of ${data.total}: ${data.label}`;break;
    case 'export.status':$('status').textContent=data.message;break;
  }
}else{const waiter=pending.get(message.id);if(waiter){pending.delete(message.id);message.ok?waiter.resolve(message.data):waiter.reject(Error(message.data?.message??'Studio action failed.'));}}});
document.addEventListener('keydown',event=>{if(event.target===scene.canvas&&scene.interactive)return;if(event.key==='Escape'){setMode(false);if(document.body.classList.contains('focus-mode')&&!document.querySelector('dialog[open]')){document.body.classList.remove('focus-mode');requestAnimationFrame(resize);}return;}if(event.target instanceof HTMLInputElement||event.target instanceof HTMLTextAreaElement||event.target instanceof HTMLSelectElement)return;if(event.ctrlKey&&event.key.toLowerCase()==='z'){event.preventDefault();undo(event.shiftKey);}if(event.ctrlKey&&event.key==='s'){event.preventDefault();$('save-project').click();}if(event.code==='Space'&&!appView){event.preventDefault();$('play').click();}if(event.key.toLowerCase()==='f'&&!event.ctrlKey&&!appView)$('focus-mode').click();});
let last=performance.now();async function tick(now:number){requestAnimationFrame(tick);const delta=Math.min(.1,(now-last)/1000);last=now;if(exporting||rendering||appView)return;if(playing){time+=delta;if(time>=totalDuration(project)){time=0;stopPlayback();}refresh(false);}else if(settle<=0)return;else settle--;rendering=true;try{await scene.render(project,time,{camera:!manualCamera,playing});}catch(e){$('status').textContent=(e as Error).message;}finally{rendering=false;}}
void action(async()=>{const state=await host('ready');tools=state.tools??{};if(state.project){try{project=validateProject(state.project);}catch(e){toast(`Your autosave could not be opened (${(e as Error).message}). Starting fresh.`,true);}}else if(!native){const cached=localStorage.getItem('launchreel-studio');if(cached)project=validateProject(JSON.parse(cached));}
  if(state.build)$('build-label').textContent=`LAUNCHREEL STUDIO · v${state.build.version} · built ${new Date(state.build.built).toLocaleString()}`;renderRecent(state.recent??[]);if(state.recovered){await addCapture(state.recovered);toast('The editor restarted during a recording. The take so far is saved in your assets.');}$('command-result').textContent=commandHelp;
  refresh();await scene.prepare(project);requestAnimationFrame(tick);if(native)openTab('capture');});
window.studioTest={debug:()=>({rendering,settle,appView,playing,live:scene.live,canvas:`${scene.canvas.width}x${scene.canvas.height}`,css:`${scene.canvas.style.width} ${scene.canvas.style.height}`,container:(()=>{const r=$('canvas-container').getBoundingClientRect();return `${Math.round(r.width)}x${Math.round(r.height)}`;})(),lost:scene.renderer.getContext().isContextLost()}),getProject:()=>structuredClone(project),setProject:(p:Project)=>replace(p),setExporting:(value:boolean)=>exporting=value,renderAt:async(t:number,decoded?:Record<string,{url:string;fps:number;count:number}>)=>{await scene.prepare(project);await scene.render(project,t,{export:true,decoded});return scene.png();},scene,host,applyScript:(value:unknown)=>{replace(applyScript(project,value).project);}};

// The editor never goes blank on an unexpected error: show it and keep working.
window.addEventListener('error',e=>toast(`Something went wrong in the editor: ${e.message}`,true));
window.addEventListener('unhandledrejection',e=>toast(`Something went wrong in the editor: ${(e.reason as Error)?.message??e.reason}`,true));
