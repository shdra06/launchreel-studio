import * as T from 'three';
import {OrbitControls} from 'three/addons/controls/OrbitControls.js';
import {RoundedBoxGeometry} from 'three/addons/geometries/RoundedBoxGeometry.js';
import {GLTFLoader} from 'three/addons/loaders/GLTFLoader.js';
import {EffectComposer} from 'three/addons/postprocessing/EffectComposer.js';
import {RenderPass} from 'three/addons/postprocessing/RenderPass.js';
import {BokehPass} from 'three/addons/postprocessing/BokehPass.js';
import {OutputPass} from 'three/addons/postprocessing/OutputPass.js';
import {cameraAt,shotAt,sourceUV,screenFit,deviceScreen,frameDistance,titleOpacity,titleFades,wordProgress,layerMotion,calloutOpacity,highlightWeight,transitionOpacity,ease,type Project,type Shot,type Asset,type Vec3,type Highlight,type Finish} from './project.ts';
import {WINDOW_CORNER} from './cutout.ts';

type Decoded=Record<string,{url:string;fps:number;count:number}>;
type Size={width:number;height:number};
const FINISH:Record<Finish,{body:string;metal:number;rough:number}>={silver:{body:'#b9bdc3',metal:.7,rough:.28},graphite:{body:'#4a4d53',metal:.65,rough:.32},black:{body:'#17181b',metal:.45,rough:.38},white:{body:'#e9e8e4',metal:.1,rough:.42}};
const FONT='"Segoe UI Variable Display","Segoe UI",sans-serif';

export class StudioScene {
  renderer:T.WebGLRenderer; scene=new T.Scene(); camera=new T.PerspectiveCamera(34,16/9,.05,300);
  controls:OrbitControls; stage=new T.Group(); device=new T.Group(); layers=new T.Group(); screen?:T.Mesh;
  /** Everything that belongs to the app's screen surface: the picture, sections, focus overlays and callouts. */
  private anchor=new T.Group();
  private overlay=new T.Scene(); private overlayCamera=new T.OrthographicCamera(-1,1,1,-1,0,10);
  private titleCanvas=document.createElement('canvas');private titleTexture:T.CanvasTexture;private titleMesh:T.Mesh;private fadeMesh:T.Mesh;
  private placeholder:T.CanvasTexture; private textures=new Map<string,T.Texture>();private videos=new Map<string,HTMLVideoElement>();private activeVideo?:HTMLVideoElement;
  private currentShot='';private currentKey='';private currentTitle='';private liveTexture?:T.Texture;private liveImage?:HTMLImageElement;private loader=new T.TextureLoader();
  private sourceSize:Size={width:1920,height:1080}; private raycaster=new T.Raycaster();private busyLive=false;
  private fit:Size={width:5.9,height:3.7};private anisotropy=1;
  private layerTextures=new Map<string,T.Texture>();private sectionTextures=new Map<string,{key:string;texture:T.Texture}>();private model?:T.Object3D;private modelAsset='';
  private hemi=new T.HemisphereLight(0xe8eeff,0x363e45,2);private key=new T.DirectionalLight(0xfff2dd,4);private fill=new T.DirectionalLight(0x9dc3ff,2.5);private rim=new T.DirectionalLight(0xffffff,3);
  private focus?:{mesh:T.Mesh;canvas:HTMLCanvasElement;texture:T.CanvasTexture;key:string};private glows:Array<{mesh:T.Mesh;x:number;y:number;r:number;speed:number;phase:number}>=[];private motes:Array<{mesh:T.Sprite;at:T.Vector3;speed:number;phase:number}>=[];private vignette:T.Mesh;private callouts:Array<{group:T.Group;id:string}>=[];
  width=1920;height=1080;live=false;interactive=false;private composer:EffectComposer;private focusPass:FocusPass;private framedWidth=1600;
  onInput?:(data:Record<string,unknown>)=>void; onCameraChange?:()=>void; onError?:(error:unknown)=>void; onExit?:()=>void;
  constructor(public canvas:HTMLCanvasElement){
    this.renderer=new T.WebGLRenderer({canvas,antialias:true,alpha:false,preserveDrawingBuffer:true});
    this.renderer.setPixelRatio(1);this.renderer.outputColorSpace=T.SRGBColorSpace;this.renderer.toneMapping=T.ACESFilmicToneMapping;this.renderer.toneMappingExposure=1.1;
    this.renderer.shadowMap.enabled=true;this.renderer.shadowMap.type=T.PCFSoftShadowMap;this.anisotropy=this.renderer.capabilities.getMaxAnisotropy();
    this.camera.position.set(4,2.8,10);this.controls=new OrbitControls(this.camera,canvas);this.controls.target.set(0,0,0);this.controls.enableDamping=false;this.controls.minDistance=1;this.controls.maxDistance=60;this.controls.addEventListener('change',()=>this.onCameraChange?.());
    this.device.add(this.anchor);this.scene.add(this.stage,this.device,this.layers,this.hemi,this.key,this.fill,this.rim,this.key.target);
    this.key.castShadow=true;this.key.shadow.mapSize.set(2048,2048);Object.assign(this.key.shadow.camera,{left:-14,right:14,top:14,bottom:-14,far:60});this.key.shadow.bias=-.0002;this.key.shadow.radius=4;this.key.shadow.camera.updateProjectionMatrix();
    this.placeholder=this.makePlaceholder();this.titleTexture=new T.CanvasTexture(this.titleCanvas);this.titleTexture.colorSpace=T.SRGBColorSpace;
    this.titleMesh=new T.Mesh(new T.PlaneGeometry(2,2),new T.MeshBasicMaterial({map:this.titleTexture,transparent:true,depthTest:false,depthWrite:false}));this.titleMesh.position.z=-1;
    this.fadeMesh=new T.Mesh(new T.PlaneGeometry(2,2),new T.MeshBasicMaterial({color:0,transparent:true,opacity:0,depthTest:false,depthWrite:false}));this.fadeMesh.position.z=-.5;
    this.vignette=new T.Mesh(new T.PlaneGeometry(2,2),new T.MeshBasicMaterial({map:this.canvasTexture(512,512,ctx=>{const g=ctx.createRadialGradient(256,256,150,256,256,370);g.addColorStop(0,'rgba(0,0,0,0)');g.addColorStop(1,'rgba(0,0,0,1)');ctx.fillStyle=g;ctx.fillRect(0,0,512,512);}),transparent:true,opacity:.3,depthTest:false,depthWrite:false}));this.vignette.renderOrder=0;this.titleMesh.renderOrder=1;this.fadeMesh.renderOrder=2;
    this.overlay.add(this.vignette,this.titleMesh,this.fadeMesh);
    this.composer=new EffectComposer(this.renderer,new T.WebGLRenderTarget(1920,1080,{type:T.HalfFloatType,samples:4}));this.focusPass=new FocusPass(this.scene,this.camera);
    this.composer.addPass(new RenderPass(this.scene,this.camera));this.composer.addPass(this.focusPass);this.composer.addPass(new OutputPass());
    // Live 3D: pointer and keyboard on the panel drive the real app. Hover moves are sent too (throttled), so the
    // app's own hover effects appear; keys carry their virtual-key code for the app's own text handling.
    let lastMove=0;
    canvas.addEventListener('pointerdown',e=>{if(!this.interactive)return;const hit=this.hit(e);if(!hit)return;canvas.setPointerCapture(e.pointerId);canvas.focus();this.onInput?.({...hit,action:'pointerDown',button:e.button});e.preventDefault();});
    canvas.addEventListener('pointermove',e=>{if(!this.interactive)return;const now=performance.now();if(!e.buttons&&now-lastMove<33)return;lastMove=now;const hit=this.hit(e);if(hit)this.onInput?.({...hit,action:'pointerMove',button:e.button});});
    canvas.addEventListener('pointerup',e=>{if(!this.interactive)return;const hit=this.hit(e);if(hit)this.onInput?.({...hit,action:'pointerUp',button:e.button});});
    canvas.addEventListener('contextmenu',e=>{if(this.interactive)e.preventDefault();});
    canvas.addEventListener('wheel',e=>{if(!this.interactive)return;const hit=this.hit(e);if(hit){e.preventDefault();this.onInput?.({...hit,action:'scroll',delta:e.deltaY});}},{passive:false});
    canvas.tabIndex=0;
    const key=(e:KeyboardEvent,type:'down'|'up')=>{if(!this.interactive)return;if(e.key==='Escape'&&e.shiftKey){this.setInteractive(false);this.onExit?.();return;}e.preventDefault();this.onInput?.({action:'key',type,vk:e.keyCode,key:e.key,alt:e.altKey});};
    canvas.addEventListener('keydown',e=>key(e,'down'));canvas.addEventListener('keyup',e=>key(e,'up'));
  }
  setInteractive(value:boolean){this.interactive=value;this.controls.enabled=!value;this.canvas.style.cursor=value?'crosshair':'grab';}
  resize(width:number,height:number){this.width=Math.round(width);this.height=Math.round(height);this.renderer.setSize(this.width,this.height,false);this.composer.setSize(this.width,this.height);this.camera.aspect=width/height;this.camera.updateProjectionMatrix();}
  makePlaceholder(){const c=document.createElement('canvas');c.width=1600;c.height=1000;const x=c.getContext('2d')!;
    const g=x.createLinearGradient(0,0,1600,1000);g.addColorStop(0,'#171c23');g.addColorStop(1,'#10141a');x.fillStyle=g;x.fillRect(0,0,c.width,c.height);x.textAlign='center';x.fillStyle='#d5f58b';x.font=`500 50px ${FONT}`;x.fillText('Your real app goes here',800,465);x.fillStyle='#83919e';x.font=`26px ${FONT}`;x.fillText('Launch it in the Capture tab, then take a still or record it',800,525);const texture=new T.CanvasTexture(c);texture.colorSpace=T.SRGBColorSpace;return texture;}
  /** The current live picture of the app, for picking sections when a shot has no media yet. */
  livePicture(){return this.live?this.liveImage:undefined;}
  private disposeGroup(group:T.Object3D){group.traverse(o=>{if(o instanceof T.Mesh||o instanceof T.InstancedMesh){o.geometry.dispose();(o.userData.ownedTexture as T.Texture|undefined)?.dispose();const materials=Array.isArray(o.material)?o.material:[o.material];materials.forEach((m:T.Material)=>{(m as T.MeshBasicMaterial).map?.userData.owned&&(m as T.MeshBasicMaterial).map!.dispose();m.dispose();});}});group.clear();}
  private material(color:string,metal=.6,rough=.3,extra:Partial<T.MeshPhysicalMaterialParameters>={}){return new T.MeshPhysicalMaterial({color,metalness:metal,roughness:rough,clearcoat:metal>.3?.6:0,...extra});}
  private box(parent:T.Object3D,width:number,height:number,depth:number,material:T.Material,position:Vec3,radius=.06){const mesh=new T.Mesh(new RoundedBoxGeometry(width,height,depth,3,Math.min(radius,width/2.01,height/2.01,depth/2.01)),material);mesh.position.fromArray(position);mesh.castShadow=true;mesh.receiveShadow=true;parent.add(mesh);return mesh;}
  /**
   * Out-of-focus light motes, as a fast lens shows them: soft discs with a faint bright rim drifting behind and beside
   * the app, never in front of it. Placement is seeded per shot, so a preview and its export match.
   */
  private buildMotes(shot:Shot){
    let seed=[...shot.id].reduce((n,c)=>(n*31+c.charCodeAt(0))>>>0,7);const rnd=()=>(seed=(seed*1664525+1013904223)>>>0)/4294967296;
    const disc=this.canvasTexture(128,128,ctx=>{const g=ctx.createRadialGradient(64,64,0,64,64,62);g.addColorStop(0,'rgba(255,255,255,.55)');g.addColorStop(.78,'rgba(255,255,255,.7)');g.addColorStop(.92,'rgba(255,255,255,1)');g.addColorStop(1,'rgba(255,255,255,0)');ctx.fillStyle=g;ctx.fillRect(0,0,128,128);});
    const bright=shot.stage==='atelier'||shot.stage==='pastel'||shot.stage==='studio';
    const colors=bright?['#fff4e0','#ffffff','#ffe9c4']:[shot.accent,'#ffffff','#ffd9a0','#9fc4ff'];
    for(let i=0;i<46;i++){
      const side=rnd()<.5?-1:1,x=side*(this.fit.width*.62+rnd()*9),y=(rnd()-.45)*9,z=-2-rnd()*16,size=.18+rnd()*rnd()*1.1;
      const material=new T.SpriteMaterial({map:i===0?disc:disc.clone(),color:colors[i%colors.length],transparent:true,depthWrite:false,blending:bright?T.NormalBlending:T.AdditiveBlending,toneMapped:false,fog:false,opacity:0});
      const mesh=new T.Sprite(material);mesh.scale.set(size,size,1);mesh.userData.opacity=(bright?.22:.5)*(.4+rnd()*.6);this.stage.add(mesh);
      this.motes.push({mesh,at:new T.Vector3(x,y,z),speed:.08+rnd()*.18,phase:rnd()*6.28});
    }
  }
  /**
   * Windows acrylic and mica let the wallpaper's colour show through an app's background, which reads as a muddy
   * cast once the app sits in a designed scene. This lifts that cast: only dark, muted pixels (the app's own
   * translucent background) move toward a clean neutral graphite; icons, images, text and accents keep their colour.
   */
  private gradeApp(material:T.MeshBasicMaterial,amount:number){if(amount<=0)return;
    material.customProgramCacheKey=()=>'clean-tint';
    material.onBeforeCompile=shader=>{shader.uniforms.cleanTint={value:amount};
      shader.fragmentShader='uniform float cleanTint;\n'+shader.fragmentShader.replace('#include <map_fragment>',`#include <map_fragment>
        float tintLum=dot(diffuseColor.rgb,vec3(.2126,.7152,.0722));
        float tintChroma=max(diffuseColor.r,max(diffuseColor.g,diffuseColor.b))-min(diffuseColor.r,min(diffuseColor.g,diffuseColor.b));
        float tintWeight=cleanTint*(1.0-smoothstep(.06,.16,tintChroma))*(1.0-smoothstep(.22,.45,tintLum));
        diffuseColor.rgb=mix(diffuseColor.rgb,vec3(tintLum)*vec3(.95,.98,1.07),tintWeight);`);};
    material.needsUpdate=true;}
  /** A rounded-rectangle alpha mask for a cutout of the given width:height; the corner radius is a share of its width. */
  private roundedMask(aspect:number,corner:number){const W=aspect>=1?1024:Math.max(64,Math.round(1024*aspect)),H=aspect>=1?Math.max(64,Math.round(1024/aspect)):1024,r=Math.min(Math.max(1.5,corner*W),W/2,H/2);
    const texture=this.canvasTexture(W,H,ctx=>{ctx.fillStyle='#000';ctx.fillRect(0,0,W,H);ctx.fillStyle='#fff';ctx.beginPath();ctx.roundRect(0,0,W,H,r);ctx.fill();});texture.colorSpace=T.NoColorSpace;return texture;}
  private canvasTexture(width:number,height:number,draw:(ctx:CanvasRenderingContext2D)=>void){const c=document.createElement('canvas');c.width=width;c.height=height;draw(c.getContext('2d')!);const texture=new T.CanvasTexture(c);texture.colorSpace=T.SRGBColorSpace;texture.userData.owned=true;return texture;}

  // ── Devices ────────────────────────────────────────────────────────────────────────────────────────────────
  /** Builds the device around the screen anchor and returns the lowest point of the device, for placing the stage. */
  private buildDevice(shot:Shot,aspect:number):number{
    const finish=FINISH[shot.finish],body=this.material(finish.body,finish.metal,finish.rough),bezel=this.material('#06080a',.2,.35,{clearcoat:1,clearcoatRoughness:.15});
    const {width:w,height:h}=deviceScreen(shot.device,aspect);this.anchor.position.set(0,0,0);this.anchor.rotation.set(0,0,0);
    switch(shot.device){
      case 'laptop':{const hinge=-h/2-.12;const lid=new T.Group();lid.position.set(0,hinge,0);lid.rotation.x=-.16;this.device.add(lid);
        this.box(lid,w+.32,h+.36,.08,body,[0,h/2+.16,-.02],.08);this.box(lid,w+.12,h+.14,.02,bezel,[0,h/2+.16,.025],.05);lid.add(this.anchor);this.anchor.position.set(0,h/2+.17,.037);
        const depth=h*.78;this.box(this.device,w+.42,.13,depth,body,[0,hinge-.07,depth/2-.1],.06);this.box(this.device,w-.3,.012,depth*.48,this.material('#0b0d10',.2,.6),[0,hinge+.002,depth*.36],.01);
        this.box(this.device,w*.3,.008,depth*.28,this.material(finish.body,finish.metal*.8,finish.rough+.1),[0,hinge+.0,depth*.76],.02);return hinge-.14;}
      case 'tablet':{this.box(this.device,w+.36,h+.36,.13,body,[0,0,-.02],.28);this.box(this.device,w+.12,h+.12,.02,bezel,[0,0,.05],.18);this.anchor.position.z=.062;return -h/2-.2;}
      case 'phone':{this.box(this.device,w+.22,h+.22,.18,body,[0,0,0],.2);this.box(this.device,w+.1,h+.1,.08,bezel,[0,0,.06],.16);this.box(this.device,.55,.09,.012,this.material('#07090b',.2,.3),[0,h/2-.1,.106],.04);this.anchor.position.z=.102;return -h/2-.12;}
      case 'floating':{this.box(this.device,w+.1,h+.1,.05,this.material('#15171b',.4,.3),[0,0,-.03],.05);this.anchor.position.z=.0;return -h/2-.05;}
      case 'panel':{
        // The app's own UI as a floating glass slab: rounded like a modern window, a thin dark-glass body with
        // clear-coat edges, a soft accent halo behind it and a contact shadow under it.
        const frost=shot.finish==='white',border=frost?.34:.06,depth=frost?.26:.18;
        this.anchor.position.z=depth/2+.001;
        // White finish: a thick frosted-glass frame with a bright rim, as in light, airy product films.
        const slab=new T.Mesh(new RoundedBoxGeometry(w+border,h+border,depth,8,frost?.125:.085),frost?new T.MeshPhysicalMaterial({color:'#f3f6fb',roughness:.32,metalness:0,clearcoat:1,clearcoatRoughness:.2,transparent:true,opacity:.72,sheen:1,sheenColor:new T.Color('#ffffff')}):this.material('#0b0e14',.4,.16,{clearcoat:1,clearcoatRoughness:.06}));slab.castShadow=true;slab.receiveShadow=true;this.device.add(slab);
        // Dark glass catches the light on its edge: a fine rim, bright where the key light hits and fading into the accent.
        if(!frost){const W=1024,H=Math.max(64,Math.round(W*(h+border)/(w+border))),r=.085/(w+border)*W*1.15;
          const rim=new T.Mesh(new T.PlaneGeometry(w+border,h+border),new T.MeshBasicMaterial({map:this.canvasTexture(W,H,ctx=>{const g=ctx.createLinearGradient(0,0,W,H);g.addColorStop(0,'rgba(255,255,255,.95)');g.addColorStop(.45,shot.accent+'88');g.addColorStop(1,'rgba(255,255,255,.35)');
            ctx.strokeStyle=g;ctx.lineWidth=3;ctx.shadowColor=shot.accent;ctx.shadowBlur=10;ctx.beginPath();ctx.roundRect(2,2,W-4,H-4,r);ctx.stroke();}),transparent:true,depthWrite:false,blending:T.AdditiveBlending,toneMapped:false}));
          rim.position.z=depth/2+.003;this.device.add(rim);}
        const accent=frost?new T.Color('#ffffff'):new T.Color(shot.accent),rgba=(a:number)=>`rgba(${accent.r*255|0},${accent.g*255|0},${accent.b*255|0},${a})`;
        const halo=new T.Mesh(new T.PlaneGeometry(w*2,h*2.3),new T.MeshBasicMaterial({map:this.canvasTexture(256,256,ctx=>{const g=ctx.createRadialGradient(128,128,0,128,128,128);g.addColorStop(0,rgba(.32));g.addColorStop(.45,rgba(.1));g.addColorStop(1,rgba(0));ctx.fillStyle=g;ctx.fillRect(0,0,256,256);}),transparent:true,depthWrite:false,blending:T.AdditiveBlending,toneMapped:false}));halo.position.z=-.9;this.device.add(halo);
        const shadow=new T.Mesh(new T.PlaneGeometry(w*1.1,h*.5),new T.MeshBasicMaterial({map:this.canvasTexture(256,128,ctx=>{const g=ctx.createRadialGradient(128,64,0,128,64,120);g.addColorStop(0,'rgba(0,0,0,.55)');g.addColorStop(1,'rgba(0,0,0,0)');ctx.fillStyle=g;ctx.fillRect(0,0,256,128);}),transparent:true,depthWrite:false}));shadow.rotation.x=-Math.PI/2;shadow.position.set(0,-h/2-.75,.1);this.device.add(shadow);
        return -h/2-.76;}
      case 'title':return -2.6;
      case 'none':{const shadow=new T.Mesh(new T.PlaneGeometry(w*1.18,h*1.24),new T.MeshBasicMaterial({map:this.canvasTexture(512,512,ctx=>{ctx.filter='blur(26px)';ctx.fillStyle='rgba(0,0,0,.6)';ctx.beginPath();ctx.roundRect(60,70,392,392,30);ctx.fill();}),transparent:true,depthWrite:false}));shadow.position.set(.05,-.12,-.08);this.device.add(shadow);return -h/2;}
      default:{this.box(this.device,w+.22,h+.22,.18,body,[0,0,0],.08);this.box(this.device,w+.10,h+.10,.08,bezel,[0,0,.11],.07);this.anchor.position.z=.157;
        this.box(this.device,.3,.85,.3,body,[0,-h/2-.38,-.12],.04);this.box(this.device,2.1,.1,1.2,body,[0,-h/2-.78,.1],.05);return -h/2-.83;}
    }
  }
  private buildScreen(shot:Shot,framedWidth:number){
    const material=new T.MeshBasicMaterial({map:this.placeholder,toneMapped:false});
    // Frameless looks show the app as a cutout: round it exactly like the app's own window corners.
    this.gradeApp(material,shot.cleanTint);
    if(shot.device==='panel'||shot.device==='none'){material.alphaMap=this.roundedMask(this.fit.width/this.fit.height,WINDOW_CORNER/framedWidth);material.transparent=true;}
    const screen=new T.Mesh(new T.PlaneGeometry(1,1),material);screen.userData={input:true,crop:[0,0,1,1]};screen.visible=shot.device!=='title';this.screen=screen;this.anchor.add(screen);
    if(!['none','floating','panel','title'].includes(shot.device)){const sheen=new T.Mesh(new T.PlaneGeometry(1,1),new T.MeshBasicMaterial({map:this.canvasTexture(256,256,ctx=>{const g=ctx.createLinearGradient(0,0,256,256);g.addColorStop(0,'rgba(255,255,255,.10)');g.addColorStop(.45,'rgba(255,255,255,.02)');g.addColorStop(1,'rgba(255,255,255,0)');ctx.fillStyle=g;ctx.fillRect(0,0,256,256);}),transparent:true,depthWrite:false,toneMapped:false}));sheen.name='sheen';sheen.position.z=.003;this.anchor.add(sheen);}
  }

  // ── Stages ─────────────────────────────────────────────────────────────────────────────────────────────────
  private floor(y:number,color:string,rough=.6,metal=.12){const floor=new T.Mesh(new T.PlaneGeometry(300,300),new T.MeshStandardMaterial({color,roughness:rough,metalness:metal}));floor.rotation.x=-Math.PI/2;floor.position.y=y;floor.receiveShadow=true;this.stage.add(floor);}
  private cyclorama(y:number,color:string){const geometry=new T.CylinderGeometry(34,34,40,64,1,true,Math.PI*.62,Math.PI*.76);const wall=new T.Mesh(geometry,new T.MeshStandardMaterial({color,roughness:.85,side:T.BackSide}));wall.position.set(0,y+20,6);wall.receiveShadow=true;this.stage.add(wall);}
  private buildStage(shot:Shot,bottom:number){
    const bg=new T.Color(shot.background),light=bg.clone().lerp(new T.Color('#ffffff'),.06).getStyle(),dark=bg.clone().multiplyScalar(.7).getStyle();
    const grounded=['desktop','laptop'].includes(shot.device);const floorY=grounded?bottom:bottom-1.1;
    switch(shot.stage){
      case 'desk':{this.floor(floorY-3,dark);this.box(this.stage,16,.28,8,this.material('#3b3431',.1,.55),[0,floorY-.14,0],.04);this.box(this.stage,1.5,.12,1,this.material('#eee6d5',0,.7),[-4.6,floorY+.06,1.6],.03);break;}
      case 'workspace':this.workspace(shot,floorY);break;
      case 'abstract':{this.floor(floorY,shot.background);for(let i=0;i<3;i++){const ring=new T.Mesh(new T.TorusGeometry(4.5+i*1.7,.025,8,128),new T.MeshStandardMaterial({color:shot.accent,emissive:shot.accent,emissiveIntensity:.45,metalness:.5,roughness:.3}));ring.position.set(0,-.4,-3-i);ring.rotation.set(.3+i*.2,.1*i,.2*i);this.stage.add(ring);}break;}
      case 'gradient':{const sky=new T.Mesh(new T.SphereGeometry(90,48,24),new T.MeshBasicMaterial({side:T.BackSide,depthWrite:false,map:this.canvasTexture(16,512,ctx=>{const g=ctx.createLinearGradient(0,0,0,512);g.addColorStop(0,new T.Color(shot.accent).lerp(bg,.72).getStyle());g.addColorStop(.55,shot.background);g.addColorStop(1,dark);ctx.fillStyle=g;ctx.fillRect(0,0,16,512);})}));this.stage.add(sky);
        const catcher=new T.Mesh(new T.PlaneGeometry(80,80),new T.ShadowMaterial({opacity:.28}));catcher.rotation.x=-Math.PI/2;catcher.position.y=floorY;catcher.receiveShadow=true;this.stage.add(catcher);break;}
      case 'void':{this.floor(floorY,new T.Color(shot.background).multiplyScalar(.35).getStyle(),.22,.6);const glow=new T.Mesh(new T.CircleGeometry(7,64),new T.MeshBasicMaterial({map:this.canvasTexture(256,256,ctx=>{const g=ctx.createRadialGradient(128,128,0,128,128,128);g.addColorStop(0,new T.Color(shot.accent).multiplyScalar(.35).getStyle());g.addColorStop(1,'rgba(0,0,0,0)');ctx.fillStyle=g;ctx.fillRect(0,0,256,256);}),transparent:true,depthWrite:false,blending:T.AdditiveBlending}));glow.rotation.x=-Math.PI/2;glow.position.y=floorY+.01;this.stage.add(glow);break;}
      case 'aurora':{
        // Launch-event look: a deep, glossy floor under slowly drifting fields of coloured light.
        if(shot.device!=='title')this.floor(floorY,new T.Color(shot.background).multiplyScalar(.12).getStyle(),.45,.85);
        const hue=new T.Color(shot.accent).getHSL({h:0,s:0,l:0}).h;
        ([[0,0,-6,30,.7],[.14,-11,-3,26,.5],[-.12,10,2,28,.6],[.45,-2,-9,22,.35]] as const).forEach(([shift,x,y,size,a],i)=>{const c=new T.Color().setHSL((hue+shift+1)%1,.85,.55),rgba=(al:number)=>`rgba(${c.r*255|0},${c.g*255|0},${c.b*255|0},${al})`;
          const mesh=new T.Mesh(new T.PlaneGeometry(size,size),new T.MeshBasicMaterial({map:this.canvasTexture(256,256,ctx=>{const g=ctx.createRadialGradient(128,128,0,128,128,128);g.addColorStop(0,rgba(a));g.addColorStop(.5,rgba(a*.35));g.addColorStop(1,rgba(0));ctx.fillStyle=g;ctx.fillRect(0,0,256,256);}),transparent:true,depthWrite:false,blending:T.AdditiveBlending,toneMapped:false,fog:false}));
          mesh.position.set(x,floorY+10+y,-22-i*2);this.stage.add(mesh);this.glows.push({mesh,x,y:floorY+10+y,r:3+i,speed:.12+i*.04,phase:i*1.7});});
        break;}
      case 'atelier':this.atelier(shot,floorY);break;
      case 'pastel':this.pastel(shot,floorY);break;
      default:{this.floor(floorY,shot.background);this.cyclorama(floorY,light);}
    }
  }
  /** A PC on a desk: the app's monitor (or laptop) with a tower, keyboard, mouse, lamp, plant and a lit wall. */
  private workspace(shot:Shot,top:number){
    const wood=this.material('#6a4c37',0,.62),accent=new T.Color(shot.accent),deskDepth=8.5,{width:w}=deviceScreen(shot.device,16/10);
    this.floor(top-3.1,'#2a2523',.8,.05);this.box(this.stage,19,.3,deskDepth,wood,[0,top-.15,.6],.05);
    for(const x of [-9,9])this.box(this.stage,.3,3,.3,this.material('#222',.6,.4),[x,top-1.65,3.9],.04);
    const wall=new T.Mesh(new T.PlaneGeometry(60,24),new T.MeshStandardMaterial({color:new T.Color(shot.background).lerp(new T.Color('#3b3f45'),.5),roughness:.95}));wall.position.set(0,top+9,-3.8);wall.receiveShadow=true;this.stage.add(wall);
    const window_=new T.Mesh(new T.PlaneGeometry(6,4.2),new T.MeshBasicMaterial({color:new T.Color('#f3dcb8').multiplyScalar(.85)}));window_.position.set(-w/2-4.5,top+5.4,-3.78);this.stage.add(window_);
    for(const [x,y,ww,hh] of [[0,0,.12,4.2],[0,0,6,.12]] as const){const bar=new T.Mesh(new T.PlaneGeometry(ww,hh),new T.MeshBasicMaterial({color:'#2b2b2b'}));bar.position.set(-w/2-4.5+x,top+5.4+y,-3.77);this.stage.add(bar);}
    const towerX=w/2+2.4,tower=this.box(this.stage,1.5,3.6,3.2,this.material('#121316',.5,.35),[towerX,top+1.8,-1.2],.08);tower.castShadow=true;
    const glass=new T.Mesh(new T.PlaneGeometry(2.8,3.1),new T.MeshPhysicalMaterial({color:'#0a0c10',transmission:.0,roughness:.08,metalness:.1,clearcoat:1,transparent:true,opacity:.85}));glass.rotation.y=-Math.PI/2;glass.position.set(towerX-.76,top+1.8,-1.2);this.stage.add(glass);
    const strip=new T.Mesh(new T.BoxGeometry(.04,3.2,.04),new T.MeshBasicMaterial({color:accent.clone().multiplyScalar(1.6),toneMapped:false}));strip.position.set(towerX-.74,top+1.8,.38);this.stage.add(strip);
    for(let i=0;i<3;i++){const fan=new T.Mesh(new T.TorusGeometry(.34,.035,8,40),new T.MeshBasicMaterial({color:accent.clone().multiplyScalar(1.3),toneMapped:false}));fan.rotation.y=Math.PI/2;fan.position.set(towerX-.74,top+.9+i*.95,-1.7);this.stage.add(fan);}
    const keyboardZ=shot.device==='laptop'?deskDepth*.28+2.4:2.5;
    if(shot.device!=='laptop'){this.box(this.stage,4.6,.14,1.55,this.material('#1b1d21',.4,.5),[0,top+.07,keyboardZ],.06);
      const keys=new T.InstancedMesh(new RoundedBoxGeometry(.22,.07,.22,2,.03),this.material('#2a2d33',.2,.55),15*5),m=new T.Matrix4();let n=0;for(let r=0;r<5;r++)for(let c=0;c<15;c++){m.makeTranslation(-2.05+c*.293,top+.17,keyboardZ-.56+r*.28);keys.setMatrixAt(n++,m);}keys.castShadow=true;this.stage.add(keys);
      const mouse=new T.Mesh(new T.SphereGeometry(.3,32,16),this.material('#1b1d21',.4,.4));mouse.scale.set(1,.42,1.5);mouse.position.set(3.4,top+.12,keyboardZ);mouse.castShadow=true;this.stage.add(mouse);
      this.box(this.stage,2.4,.02,1.9,this.material('#101114',0,.9),[3.4,top+.01,keyboardZ],.05);}
    const mug=new T.Mesh(new T.CylinderGeometry(.32,.28,.75,32),this.material('#e9e3d8',0,.5));mug.position.set(-w/2-1.4,top+.38,1.4);mug.castShadow=true;this.stage.add(mug);
    const handle=new T.Mesh(new T.TorusGeometry(.17,.045,10,24),this.material('#e9e3d8',0,.5));handle.position.set(-w/2-1.07,top+.4,1.4);this.stage.add(handle);
    const pot=new T.Mesh(new T.CylinderGeometry(.55,.42,.9,32),this.material('#c9b49b',0,.8));pot.position.set(-w/2-3.4,top+.45,-1.6);pot.castShadow=true;this.stage.add(pot);
    const leaf=this.material('#3f6b45',0,.6);for(let i=0;i<9;i++){const l=new T.Mesh(new T.SphereGeometry(.35+(i%3)*.08,16,12),leaf);const a=i/9*Math.PI*2;l.scale.set(.6,1.5,.35);l.position.set(-w/2-3.4+Math.cos(a)*.38,top+1.4+(i%3)*.32,-1.6+Math.sin(a)*.38);l.rotation.set(Math.sin(a)*.5,a,Math.cos(a)*.5);l.castShadow=true;this.stage.add(l);}
    const lampX=w/2+.9;this.box(this.stage,.9,.08,.9,this.material('#1d1e21',.6,.3),[lampX+3.1,top+.04,1.6],.03);const arm=this.box(this.stage,.08,2.6,.08,this.material('#1d1e21',.6,.3),[lampX+3.1,top+1.3,1.6],.02);arm.rotation.z=.25;
    const head=new T.Mesh(new T.ConeGeometry(.42,.6,32,1,true),this.material('#1d1e21',.6,.3,{side:T.DoubleSide}));head.position.set(lampX+2.55,top+2.55,1.6);head.rotation.z=-.9;this.stage.add(head);
    const bulb=new T.PointLight(0xffd9a0,6,9,2);bulb.position.set(lampX+2.4,top+2.3,1.6);this.stage.add(bulb);
  }

  /** An arched wall: a slab with a round-topped opening, extruded with softened edges. */
  private arch(opening:number,height:number,wallWidth:number,material:T.Material,x:number,y:number,z:number,rotation=0){
    const shape=new T.Shape(),r=opening/2,top=height*1.5;shape.moveTo(-wallWidth/2,0);shape.lineTo(wallWidth/2,0);shape.lineTo(wallWidth/2,top);shape.lineTo(-wallWidth/2,top);shape.lineTo(-wallWidth/2,0);
    const hole=new T.Path();hole.moveTo(-r,0);hole.lineTo(-r,height-r);hole.absarc(0,height-r,r,Math.PI,0,true);hole.lineTo(r,0);hole.lineTo(-r,0);shape.holes.push(hole);
    const mesh=new T.Mesh(new T.ExtrudeGeometry(shape,{depth:.7,bevelEnabled:true,bevelSize:.08,bevelThickness:.08,bevelSegments:3,curveSegments:64}),material);
    mesh.position.set(x,y,z);mesh.rotation.y=rotation;mesh.castShadow=true;mesh.receiveShadow=true;this.stage.add(mesh);return mesh;}
  private sky(top:string,bottom:string){const sky=new T.Mesh(new T.SphereGeometry(140,48,24),new T.MeshBasicMaterial({side:T.BackSide,depthWrite:false,fog:false,map:this.canvasTexture(16,512,ctx=>{const g=ctx.createLinearGradient(0,0,0,512);g.addColorStop(0,top);g.addColorStop(.55,bottom);g.addColorStop(1,bottom);ctx.fillStyle=g;ctx.fillRect(0,0,16,512);})}));this.stage.add(sky);}
  /** Bright pastel architecture: arches, a circular window onto the sky and a stepped pedestal under the app. */
  private atelier(shot:Shot,floorY:number){
    const tint=new T.Color('#e8edf4').lerp(new T.Color(shot.accent),.06),wall=new T.MeshStandardMaterial({color:tint.clone().multiplyScalar(1.02),roughness:.92});
    this.sky(new T.Color('#9fbbe0').lerp(new T.Color(shot.accent),.15).getStyle(),'#eef2f7');
    const pedestalTop=floorY-.25,ground=pedestalTop-.42;
    this.floor(ground,tint.getStyle(),.85,0);
    const {width:w,height:h}=this.fit,radius=Math.max(w,h*.8)*.62;
    const step=new T.Mesh(new T.CylinderGeometry(radius,radius,.3,96),new T.MeshStandardMaterial({color:'#f4f6fa',roughness:.6}));step.position.y=pedestalTop-.15;step.castShadow=true;step.receiveShadow=true;this.stage.add(step);
    const base=new T.Mesh(new T.CylinderGeometry(radius*1.28,radius*1.3,.12,96),new T.MeshStandardMaterial({color:'#e9edf3',roughness:.75}));base.position.y=ground+.06;base.receiveShadow=true;this.stage.add(base);
    this.arch(9,15,30,wall,-6,ground,-16);this.arch(7,12,16,wall,13,ground,-9,-.55);this.arch(6,10,14,wall,-15,ground,-6,.7);
    const back=new T.Mesh(new T.PlaneGeometry(90,40),wall);back.position.set(0,ground+20,-26);back.receiveShadow=true;this.stage.add(back);
    const portal=new T.Mesh(new T.CircleGeometry(7.5,96),new T.MeshBasicMaterial({fog:false,map:this.canvasTexture(256,256,ctx=>{const g=ctx.createRadialGradient(150,170,10,128,128,150);g.addColorStop(0,'#ffe9d2');g.addColorStop(.45,'#cfdcf0');g.addColorStop(1,'#a9c1e2');ctx.fillStyle=g;ctx.fillRect(0,0,256,256);})}));portal.position.set(7,ground+12,-25.9);this.stage.add(portal);
    const rim=new T.Mesh(new T.TorusGeometry(7.6,.45,24,128),wall);rim.position.copy(portal.position);rim.position.z+=.2;rim.castShadow=true;this.stage.add(rim);
  }
  /** Light, saturated colour fields drifting behind the app, like a premium end card. */
  private pastel(shot:Shot,floorY:number){
    this.sky('#f6f2f8','#f2eef6');
    const catcher=new T.Mesh(new T.PlaneGeometry(80,80),new T.ShadowMaterial({opacity:.16}));catcher.rotation.x=-Math.PI/2;catcher.position.y=floorY-.6;catcher.receiveShadow=true;this.stage.add(catcher);
    (['#ff6fb1','#ffb36b','#6aa8ff','#a77bff'] as const).forEach((color,i)=>{const c=new T.Color(color),rgba=(a:number)=>`rgba(${c.r*255|0},${c.g*255|0},${c.b*255|0},${a})`;
      const mesh=new T.Mesh(new T.PlaneGeometry(24,24),new T.MeshBasicMaterial({map:this.canvasTexture(256,256,ctx=>{const g=ctx.createRadialGradient(128,128,0,128,128,128);g.addColorStop(0,rgba(.85));g.addColorStop(.55,rgba(.3));g.addColorStop(1,rgba(0));ctx.fillStyle=g;ctx.fillRect(0,0,256,256);}),transparent:true,depthWrite:false,fog:false,toneMapped:false}));
      const x=[-6,6,-2,4][i],y=floorY+[8,9,4,3][i];mesh.position.set(x,y,-18-i*.5);this.stage.add(mesh);this.glows.push({mesh,x,y,r:3.5,speed:.16+i*.05,phase:i*1.9});});
  }

  // ── Light ──────────────────────────────────────────────────────────────────────────────────────────────────
  private applyLighting(shot:Shot){
    const k=shot.lightIntensity,accent=new T.Color(shot.accent);
    const set=(light:T.DirectionalLight,color:T.ColorRepresentation,intensity:number,position:Vec3)=>{light.color.set(color);light.intensity=intensity*k;light.position.fromArray(position);};
    switch(shot.lighting){
      case 'soft':this.hemi.color.set(0xf1f4ff);this.hemi.groundColor.set(0x5a5f66);this.hemi.intensity=3.1*k;set(this.key,0xfff6ea,2.2,[3,7,7]);set(this.fill,0xdfe8ff,2,[-6,3,4]);set(this.rim,0xffffff,1.4,[0,5,-6]);break;
      case 'dramatic':this.hemi.color.set(0x9aa4b8);this.hemi.groundColor.set(0x111215);this.hemi.intensity=.55*k;set(this.key,0xfff0dc,6.5,[5,8,3]);set(this.fill,0x6c84b0,.55,[-6,1,3]);set(this.rim,0xffffff,5,[-2,4,-6]);break;
      case 'sunset':this.hemi.color.set(0xffc69a);this.hemi.groundColor.set(0x2b1d2e);this.hemi.intensity=1.2*k;set(this.key,0xffa96a,4.8,[-7,2.6,4]);set(this.fill,0x6d7cff,1.3,[6,2,3]);set(this.rim,0xff7a59,3.4,[2,4,-6]);break;
      case 'neon':this.hemi.color.set(0x8a8fb0);this.hemi.groundColor.set(0x0b0b12);this.hemi.intensity=.5*k;set(this.key,0xffffff,1.8,[3,6,6]);set(this.fill,accent,3.6,[-6,1,2]);set(this.rim,new T.Color().setHSL((accent.getHSL({h:0,s:0,l:0}).h+.5)%1,1,.6),5.2,[4,3,-6]);break;
      default:this.hemi.color.set(0xe8eeff);this.hemi.groundColor.set(0x363e45);this.hemi.intensity=2*k;set(this.key,0xfff2dd,4,[3,6,6]);set(this.fill,0x9dc3ff,2.5,[-5,2,1]);set(this.rim,0xffffff,3,[0,4,-5]);
    }
  }

  private setup(shot:Shot,aspect:number,framedWidth:number){
    this.glows=[];this.motes.forEach(m=>{m.mesh.material.map?.dispose();m.mesh.material.dispose();});this.motes=[];this.disposeGroup(this.stage);for(const child of [...this.device.children])if(child!==this.anchor){this.disposeGroup(child);this.device.remove(child);}
    this.anchor.removeFromParent();this.disposeGroup(this.anchor);this.device.add(this.anchor);this.disposeGroup(this.layers);this.focus=undefined;this.callouts=[];this.sectionTextures.forEach(s=>s.texture.dispose());this.sectionTextures.clear();
    this.currentShot=shot.id;this.currentKey=this.setupKey(shot,aspect,framedWidth);this.framedWidth=framedWidth;
    this.fit=screenFit(shot.device,aspect);const bottom=this.buildDevice(shot,aspect);this.buildScreen(shot,framedWidth);this.buildStage(shot,bottom);if(shot.depthOfField>0&&shot.device!=='title')this.buildMotes(shot);
    for(const layer of shot.layers){const mesh=new T.Mesh(new T.PlaneGeometry(1,1),new T.MeshBasicMaterial({map:this.placeholder,transparent:true,opacity:layer.opacity,toneMapped:false,side:T.DoubleSide}));mesh.name=layer.id;mesh.userData={input:!!layer.liveElementId,elementId:layer.liveElementId,crop:layer.crop};
      if(layer.attach==='screen'){mesh.castShadow=true;
        // A section is a cutout of the app's own pixels: round it like the app's corners, shadow included.
        const c=shot.crop??[0,0,1,1],w=layer.crop[2]/c[2],h=layer.crop[3]/c[3],mask=w>0&&h>0?this.roundedMask(w*this.fit.width/(h*this.fit.height),WINDOW_CORNER/(framedWidth*w)):undefined;
        const material=mesh.material as T.MeshBasicMaterial;this.gradeApp(material,shot.cleanTint);if(mask){material.alphaMap=mask;material.needsUpdate=true;}
        const edge=new T.Mesh(new T.PlaneGeometry(1,1),new T.MeshBasicMaterial({color:0x000000,transparent:true,opacity:.35,depthWrite:false,alphaMap:mask??null}));edge.name='shadow';edge.position.set(.02,-.03,-.01);mesh.add(edge);this.anchor.add(mesh);}else this.layers.add(mesh);}
    for(const callout of shot.callouts)this.callouts.push({id:callout.id,group:this.buildCallout(callout.text,shot.accent)});
  }
  /** Device and stage depend on these; anything else is updated per frame. */
  private setupKey(shot:Shot,aspect:number,framedWidth:number){return JSON.stringify([Math.round(framedWidth),shot.cleanTint,shot.id,shot.device,shot.stage,shot.finish,shot.background,shot.accent,shot.layers.map(l=>[l.id,l.attach]),shot.callouts.map(c=>[c.id,c.text]),Math.round(aspect*100)]);}

  // ── Callouts and focus ─────────────────────────────────────────────────────────────────────────────────────
  private buildCallout(text:string,accent:string){
    const group=new T.Group();const ctx=document.createElement('canvas').getContext('2d')!;ctx.font=`600 46px ${FONT}`;const textWidth=Math.min(1600,ctx.measureText(text).width);
    const label=this.canvasTexture(Math.ceil(textWidth+120),112,c=>{c.fillStyle='rgba(16,19,24,.92)';c.beginPath();c.roundRect(4,4,c.canvas.width-8,104,52);c.fill();c.strokeStyle=accent;c.globalAlpha=.55;c.lineWidth=3;c.stroke();c.globalAlpha=1;c.fillStyle=accent;c.beginPath();c.arc(54,56,11,0,Math.PI*2);c.fill();c.fillStyle='#f3f5f0';c.font=`600 46px ${FONT}`;c.textBaseline='middle';c.fillText(text,82,58);});
    const height=.42,width=height*label.image.width/label.image.height;
    const card=new T.Mesh(new T.PlaneGeometry(width,height),new T.MeshBasicMaterial({map:label,transparent:true,depthWrite:false,toneMapped:false}));card.name='card';card.userData.width=width;group.add(card);
    const dot=new T.Mesh(new T.CircleGeometry(.055,32),new T.MeshBasicMaterial({color:accent,toneMapped:false,transparent:true}));dot.name='dot';group.add(dot);
    const ring=new T.Mesh(new T.RingGeometry(.08,.1,32),new T.MeshBasicMaterial({color:accent,toneMapped:false,transparent:true}));ring.name='ring';group.add(ring);
    const line=new T.Mesh(new T.PlaneGeometry(1,.012),new T.MeshBasicMaterial({color:accent,toneMapped:false,transparent:true}));line.name='line';group.add(line);
    this.anchor.add(group);return group;
  }
  private placeCallouts(shot:Shot,local:number){
    for(const {id,group} of this.callouts){const callout=shot.callouts.find(c=>c.id===id);if(!callout){group.visible=false;continue;}
      const opacity=calloutOpacity(local,shot.duration,callout.delay);group.visible=opacity>0;if(!group.visible)continue;
      const x=(callout.x-.5)*this.fit.width,y=(.5-callout.y)*this.fit.height,side=callout.x<.62?1:-1,pop=ease(Math.min(1,(local-callout.delay)/.45),'ease-out');
      const card=group.getObjectByName('card') as T.Mesh,width=card.userData.width as number,cardX=x+side*(.55+width/2),cardY=y+.38,lift=.35+.25*pop+shot.explode*1.25;
      card.position.set(cardX,cardY,lift);card.scale.setScalar(.85+.15*pop);
      group.getObjectByName('dot')!.position.set(x,y,.012);const ring=group.getObjectByName('ring')!;ring.position.set(x,y,.012);ring.scale.setScalar(1+.6*((local*1.4)%1));
      const line=group.getObjectByName('line') as T.Mesh,ex=cardX-side*width/2,ey=cardY,len=Math.hypot(ex-x,ey-y);line.position.set((x+ex)/2,(y+ey)/2,lift/2);line.scale.x=len;line.rotation.z=Math.atan2(ey-y,ex-x);
      group.traverse(o=>{if(o instanceof T.Mesh){const m=o.material as T.MeshBasicMaterial;m.opacity=opacity*(o.name==='ring'?1-((local*1.4)%1):1);}});}
  }
  private activeHighlight(shot:Shot,local:number):{h:Highlight;w:number}|undefined{let best:{h:Highlight;w:number}|undefined;for(const h of shot.highlights){const w=highlightWeight(local,h);if(w>0&&(!best||w>best.w))best={h,w};}return best;}
  private drawFocus(shot:Shot,active:{h:Highlight;w:number}|undefined){
    if(!active||active.h.style==='zoom'){if(this.focus)this.focus.mesh.visible=false;return;}
    const key=JSON.stringify([active.h.rect,active.h.style,shot.accent,this.fit.width/this.fit.height]);
    if(!this.focus){const canvas=document.createElement('canvas');const texture=new T.CanvasTexture(canvas);texture.colorSpace=T.SRGBColorSpace;texture.userData.owned=true;const mesh=new T.Mesh(new T.PlaneGeometry(1,1),new T.MeshBasicMaterial({map:texture,transparent:true,depthWrite:false,toneMapped:false}));mesh.position.z=.006;this.anchor.add(mesh);this.focus={mesh,canvas,texture,key:''};}
    const f=this.focus;f.mesh.visible=true;f.mesh.scale.set(this.fit.width,this.fit.height,1);(f.mesh.material as T.MeshBasicMaterial).opacity=active.w;
    if(f.key!==key){f.key=key;const W=1600,H=Math.round(W*this.fit.height/this.fit.width);f.canvas.width=W;f.canvas.height=H;const ctx=f.canvas.getContext('2d')!;const [x,y,w,h]=active.h.rect,r=Math.min(28,w*W/6,h*H/6);
      ctx.clearRect(0,0,W,H);if(active.h.style==='spotlight'){ctx.fillStyle='rgba(4,6,9,.66)';ctx.fillRect(0,0,W,H);ctx.globalCompositeOperation='destination-out';ctx.beginPath();ctx.roundRect(x*W,y*H,w*W,h*H,r);ctx.fill();ctx.globalCompositeOperation='source-over';}
      ctx.shadowColor=shot.accent;ctx.shadowBlur=24;ctx.strokeStyle=shot.accent;ctx.lineWidth=5;ctx.beginPath();ctx.roundRect(x*W,y*H,w*W,h*H,r);ctx.stroke();f.texture.needsUpdate=true;}
  }
  /** A zoom highlight flies the camera to the region, filling the frame with that part of the real UI. */
  private zoomCamera(active:{h:Highlight;w:number}|undefined){
    if(!active||active.h.style!=='zoom')return;const [x,y,w,h]=active.h.rect;this.device.updateMatrixWorld(true);
    const point=this.anchor.localToWorld(new T.Vector3((x+w/2-.5)*this.fit.width,(.5-y-h/2)*this.fit.height,0));const normal=new T.Vector3(0,0,1).applyQuaternion(this.anchor.getWorldQuaternion(new T.Quaternion()));
    // Never magnify past the footage: at most 2.2 screen pixels per recorded pixel, so a close-up stays crisp.
    const crisp=this.fit.width*this.width/(this.framedWidth*2.2)/(2*Math.tan(this.camera.fov*Math.PI/360)*this.camera.aspect);
    const distance=Math.max(crisp,frameDistance(w*this.fit.width,h*this.fit.height,this.camera.fov,this.camera.aspect,.8));const t=ease(active.w,'smooth');
    this.camera.position.lerp(point.clone().addScaledVector(normal,distance),t);this.controls.target.lerp(point,t);this.camera.lookAt(this.controls.target);
  }

  // ── Media ──────────────────────────────────────────────────────────────────────────────────────────────────
  private prepareTexture(texture:T.Texture){texture.colorSpace=T.SRGBColorSpace;texture.anisotropy=this.anisotropy;return texture;}
  private async texture(asset:Asset):Promise<T.Texture>{const cached=this.textures.get(asset.id);if(cached)return cached;let texture:T.Texture;
    if(asset.kind==='video'){const video=document.createElement('video');video.src=asset.url;video.muted=true;video.playsInline=true;video.preload='auto';await new Promise<void>((resolve,reject)=>{video.onloadeddata=()=>resolve();video.onerror=()=>reject(Error(`Cannot decode ${asset.name}`));});texture=new T.VideoTexture(video);this.videos.set(asset.id,video);}
    else texture=await this.loader.loadAsync(asset.url);this.prepareTexture(texture);this.textures.set(asset.id,texture);return texture;}
  async prepare(p:Project){for(const a of p.assets.filter(a=>['image','video'].includes(a.kind))){try{await this.texture(a);}catch(e){this.onError?.(e);}}}
  /** Draws the shot's media (or the live app) into a canvas, for the sections editor. */
  async picture(p:Project,shot:Shot):Promise<HTMLCanvasElement|undefined>{
    const asset=p.assets.find(a=>a.id===shot.assetId&&(a.kind==='image'||a.kind==='video'));let source:CanvasImageSource|undefined,w=0,h=0;
    if(asset){const texture=await this.texture(asset);const video=this.videos.get(asset.id);if(video){await this.seek(video,shot.sourceStart);source=video;w=video.videoWidth;h=video.videoHeight;}else{const image=texture.image as HTMLImageElement;source=image;w=image.width;h=image.height;}}
    else if(this.liveImage){source=this.liveImage;w=this.liveImage.width;h=this.liveImage.height;}
    if(!source||!w||!h)return undefined;const c=document.createElement('canvas');c.width=w;c.height=h;c.getContext('2d')!.drawImage(source,0,0);return c;
  }
  private static size(texture:T.Texture,fallback:Size):Size{const image=texture.image as {videoWidth?:number;videoHeight?:number;width?:number;height?:number}|undefined;const width=image?.videoWidth||image?.width||0,height=image?.videoHeight||image?.height||0;return width>0&&height>0?{width,height}:fallback;}
  private async seek(video:HTMLVideoElement,time:number){const target=Math.min(Math.max(0,time),Math.max(0,(video.duration||time)-.001));if(Math.abs(video.currentTime-target)<.0001&&video.readyState>=2)return;await new Promise<void>((resolve,reject)=>{const timer=setTimeout(()=>{video.removeEventListener('seeked',done);reject(Error('Video seeking timed out.'));},10000);const done=()=>{clearTimeout(timer);resolve();};video.addEventListener('seeked',done,{once:true});video.currentTime=target;});}
  /** Preview playback: play the clip in real time while the timeline plays, hold the exact frame while scrubbing. */
  private follow(video:HTMLVideoElement,target:number,playing:boolean){if(this.activeVideo&&this.activeVideo!==video)this.activeVideo.pause();this.activeVideo=video;const end=Math.max(0,(video.duration||target)-.05);target=Math.min(target,end);
    if(playing){if(video.paused)void video.play().catch(()=>{});if(Math.abs(video.currentTime-target)>.3)video.currentTime=target;}
    else{if(!video.paused)video.pause();if(Math.abs(video.currentTime-target)>1/60)video.currentTime=target;}}
  /** A cropped view of a texture for a section, reused while its source and crop stay the same. */
  private section(layerId:string,source:T.Texture,crop:number[]):T.Texture{const key=source.uuid+crop.join(',');const cached=this.sectionTextures.get(layerId);if(cached?.key===key)return cached.texture;cached?.texture.dispose();
    const texture=source.clone();texture.repeat.set(crop[2],crop[3]);texture.offset.set(crop[0],1-crop[1]-crop[3]);texture.needsUpdate=true;this.sectionTextures.set(layerId,{key,texture});return texture;}

  async render(p:Project,time:number,options:{camera?:boolean;export?:boolean;playing?:boolean;decoded?:Decoded}={}){
    const {shot,local}=shotAt(p,time);
    const useLive=this.live&&!!this.liveTexture&&!options.export;let texture=useLive?this.liveTexture!:this.placeholder;let media:Size=useLive?this.sourceSize:{width:1600,height:1000};
    const asset=p.assets.find(a=>a.id===shot.assetId);
    if(asset&&(asset.kind==='image'||asset.kind==='video')&&!useLive){
      if(options.decoded?.[asset.id]){const d=options.decoded[asset.id],frame=Math.min(d.count-1,Math.max(0,Math.floor((shot.sourceStart+local)*d.fps+1e-6)));const cacheKey=`${asset.id}:export`;const old=this.textures.get(cacheKey);texture=this.prepareTexture(await this.loader.loadAsync(`${d.url}/frame_${String(frame+1).padStart(8,'0')}.png`));old?.dispose();this.textures.set(cacheKey,texture);}
      else{texture=await this.texture(asset);const video=this.videos.get(asset.id);if(video){if(options.export)await this.seek(video,shot.sourceStart+local);else this.follow(video,shot.sourceStart+local,!!options.playing);}}
      media=StudioScene.size(texture,{width:asset.width??1920,height:asset.height??1080});
    }else if(this.activeVideo&&!options.export){this.activeVideo.pause();this.activeVideo=undefined;}
    const crop=shot.crop,aspect=(media.width*(crop?crop[2]:1))/(media.height*(crop?crop[3]:1));
    const framedWidth=media.width*(crop?crop[2]:1);
    if(this.currentShot!==shot.id||this.currentKey!==this.setupKey(shot,aspect,framedWidth))this.setup(shot,aspect,framedWidth);
    this.applyLighting(shot);const ground=shot.stage==='aurora'?new T.Color(shot.background).multiplyScalar(.35):shot.stage==='atelier'?new T.Color('#eef2f7'):shot.stage==='pastel'?new T.Color('#f4f0f6'):new T.Color(shot.background);this.scene.background=ground;this.scene.fog=shot.stage==='gradient'||shot.stage==='pastel'?null:new T.Fog(ground,shot.stage==='aurora'?30:shot.stage==='atelier'?22:26,shot.stage==='aurora'?110:shot.stage==='atelier'?75:90);
    for(const m of this.motes){const t=time*m.speed+m.phase,rise=(time*m.speed*.12+m.phase/6.28)%1;m.mesh.position.set(m.at.x+Math.sin(t)*.6,m.at.y+Math.sin(t*.7+1)*.4+rise*3,m.at.z);m.mesh.material.opacity=(m.mesh.userData.opacity as number)*Math.sin(Math.PI*rise)*(.7+.3*Math.sin(t*1.3));}
    for(const g of this.glows){g.mesh.position.x=g.x+Math.sin(time*g.speed+g.phase)*g.r;g.mesh.position.y=g.y+Math.cos(time*g.speed*.8+g.phase)*g.r*.5;}
    (this.vignette.material as T.MeshBasicMaterial).opacity=shot.stage==='aurora'||shot.stage==='void'?.55:shot.stage==='atelier'||shot.stage==='pastel'?.12:.3;
    this.device.rotation.set(...shot.deviceRotation);this.device.position.fromArray(shot.devicePosition);
    // The glass panel breathes: a slow float that keeps hero shots alive without distracting from the UI.
    if(shot.device==='panel'){this.device.position.y+=.06*Math.sin(time*.9);this.device.rotation.y+=.035*Math.sin(time*.55);this.device.rotation.x+=.02*Math.sin(time*.7+1);}
    if(options.camera!==false){const k=cameraAt(shot.camera,local,shot.easing);this.camera.position.fromArray(k.position);this.controls.target.fromArray(k.target);this.camera.fov=k.fov;this.camera.lookAt(this.controls.target);}
    const active=this.activeHighlight(shot,local);if(options.camera!==false)this.zoomCamera(active);this.camera.updateProjectionMatrix();
    this.screen!.scale.set(this.fit.width,this.fit.height,1);this.anchor.getObjectByName('sheen')?.scale.set(this.fit.width,this.fit.height,1);
    this.screen!.userData.crop=crop??[0,0,1,1];
    (this.screen!.material as T.MeshBasicMaterial).map=crop?this.section('__screen__',texture,crop):texture;(this.screen!.material as T.MeshBasicMaterial).needsUpdate=true;
    let index=0;for(const layer of shot.layers){const container=layer.attach==='screen'?this.anchor:this.layers;const mesh=container.getObjectByName(layer.id) as T.Mesh|undefined;if(!mesh)continue;const i=layer.attach==='screen'?index++:0;
      const motion=layerMotion(local,shot.duration,i,layer,shot.explode);let lt=layer.liveElementId?this.layerTextures.get(layer.liveElementId):undefined;let size=lt?StudioScene.size(lt,media):media;
      if(!lt){const la=p.assets.find(a=>a.id===layer.assetId);const source=la&&!(layer.attach==='screen'&&!options.export&&useLive)?await this.texture(la):texture;size=la?StudioScene.size(source,{width:la.width??1920,height:la.height??1080}):media;lt=this.section(layer.id,source,layer.crop);}
      const material=mesh.material as T.MeshBasicMaterial;material.map=lt;material.opacity=layer.opacity*motion.opacity;material.needsUpdate=true;
      // Section rectangles are measured on the full footage; place them relative to the framed (cropped) part.
      if(layer.attach==='screen'){const [cx,cy,cw,ch]=crop?[(layer.crop[0]-crop[0])/crop[2],(layer.crop[1]-crop[1])/crop[3],layer.crop[2]/crop[2],layer.crop[3]/crop[3]]:layer.crop;mesh.position.set((cx+cw/2-.5)*this.fit.width+layer.position[0]+motion.x,(.5-cy-ch/2)*this.fit.height+layer.position[1],.01+layer.position[2]+motion.z);mesh.scale.set(cw*this.fit.width*layer.scale*motion.scale,ch*this.fit.height*layer.scale*motion.scale,1);mesh.rotation.set(...layer.rotation);
        const shadow=mesh.getObjectByName('shadow') as T.Mesh|undefined;if(shadow)(shadow.material as T.MeshBasicMaterial).opacity=Math.min(.4,motion.z*.5)*layer.opacity;}
      else{const aspectL=layer.liveElementId?size.width/size.height:(size.width*layer.crop[2])/(size.height*layer.crop[3]);mesh.position.fromArray(layer.position);mesh.position.x+=motion.x;mesh.position.z+=motion.z;mesh.rotation.set(...layer.rotation);mesh.scale.set(2.1*layer.scale*motion.scale,2.1/aspectL*layer.scale*motion.scale,1);}}
    this.placeCallouts(shot,local);this.drawFocus(shot,active);
    const modelAsset=p.assets.find(a=>a.kind==='model');if(modelAsset?.id!==this.modelAsset){if(this.model){this.scene.remove(this.model);this.model=undefined;}this.modelAsset=modelAsset?.id??'';if(modelAsset){const gltf=await new GLTFLoader().loadAsync(modelAsset.url);this.model=gltf.scene;const box=new T.Box3().setFromObject(this.model),size=box.getSize(new T.Vector3()).length();this.model.scale.setScalar(4/(size||1));this.model.position.set(-4,-1.7,-1);this.model.traverse(o=>{o.castShadow=true;o.receiveShadow=true;});this.scene.add(this.model);}}
    // A paused editing preview shows the shot as composed; fades and title timing play during playback and export.
    const timed=!!options.playing||!!options.export;
    this.drawTitle(shot,this.width,this.height,timed?local:undefined);// A zoom takes the whole frame, so the headline steps aside while it lasts.
    const stepAside=active?.h.style==='zoom'?1-active.w:1;
    (this.titleMesh.material as T.MeshBasicMaterial).opacity=stepAside*(!timed?1:shot.titleMotion==='fade'?titleOpacity(local,shot.duration):Math.max(0,Math.min(1,(shot.duration-local)/titleFades(shot.duration).fadeOut)));
    const fade=this.fadeMesh.material as T.MeshBasicMaterial;fade.color.set(shot.background);fade.opacity=timed?transitionOpacity(local,shot.transition):0;
    this.renderer.autoClear=true;
    if(shot.depthOfField>0){
      // Focus on the app: the band covers its own depth (turned, tilted, lifted sections), so its text never softens.
      const toCamera=this.anchor.getWorldPosition(new T.Vector3()).applyMatrix4(this.camera.matrixWorldInverse);
      const u=this.focusPass.materialBokeh.uniforms;u.focus.value=-toCamera.z;u.band.value=Math.max(this.fit.width,this.fit.height)*.55+shot.explode*1.2;
      u.aperture.value=.0012*shot.depthOfField;u.maxblur.value=.016*shot.depthOfField;this.composer.render();
    }else this.renderer.render(this.scene,this.camera);
    this.renderer.autoClear=false;this.renderer.clearDepth();this.renderer.render(this.overlay,this.overlayCamera);this.renderer.autoClear=true;
  }

  // ── Titles ─────────────────────────────────────────────────────────────────────────────────────────────────
  /** Title typography, drawn at the exact output resolution so text stays sharp at 4K. */
  /** With `local`, words enter one after another (kinetic type); without it the title is drawn complete. */
  private paintTitle(canvas:HTMLCanvasElement,shot:Shot,width:number,height:number,local?:number){canvas.width=width;canvas.height=height;const ctx=canvas.getContext('2d')!;ctx.clearRect(0,0,width,height);if(shot.titleLayout==='none')return;
    const portrait=height>width,unit=(portrait?width/1080:Math.min(width/1920,height/1080))*shot.titleScale,centered=shot.titleLayout==='center'||shot.titleLayout==='top-center';
    const px=centered?width/2:width*(portrait?.08:.06),maxWidth=width*(centered?.8:portrait?.84:.52);
    const eyebrow=shot.subtitle.trim()?Math.round((portrait?30:22)*unit):0,size=Math.round((portrait?84:68)*unit),line=size*1.12,gap=(portrait?78:62)*unit;
    ctx.font=`650 ${size}px ${FONT}`;ctx.letterSpacing=`${-1.2*unit}px`;const lines:string[]=[];for(const paragraph of shot.title.split('\n')){let buffer='';for(const word of paragraph.split(' ')){const next=buffer?buffer+' '+word:word;if(ctx.measureText(next).width>maxWidth&&buffer){lines.push(buffer);buffer=word;}else buffer=next;}lines.push(buffer);}
    const block=(eyebrow?gap:0)+lines.length*line;
    let y=shot.titleLayout==='bottom-left'?height*(portrait?.9:.88)-block+(eyebrow?eyebrow:size):shot.titleLayout==='center'?(height-block)/2+(eyebrow?eyebrow:size):height*(portrait?.075:.11);
    // A soft scrim in the shot's background colour keeps the headline readable over busy app UI.
    const widest=Math.max(...lines.map(l=>ctx.measureText(l).width),eyebrow?(ctx.font=`650 ${eyebrow}px ${FONT}`,ctx.measureText(shot.subtitle.toUpperCase()).width):0);ctx.font=`650 ${size}px ${FONT}`;
    const top=y-(eyebrow||size)*1.1,bottom=y+(eyebrow?gap:0)+(lines.length-1)*line+size*.35,left=centered?px-widest/2:px,rx=widest/2+140*unit,ry=(bottom-top)/2+100*unit;
    const progress=(i:number)=>local===undefined?1:wordProgress(local,i,shot.titleMotion);
    const bg=new T.Color(shot.background);ctx.save();ctx.globalAlpha=progress(0);ctx.translate(left+widest/2,(top+bottom)/2);ctx.scale(rx/ry,1);const scrim=ctx.createRadialGradient(0,0,0,0,0,ry);const rgba=(a:number)=>`rgba(${Math.round(bg.r*255)},${Math.round(bg.g*255)},${Math.round(bg.b*255)},${a})`;scrim.addColorStop(0,rgba(.34));scrim.addColorStop(.6,rgba(.16));scrim.addColorStop(1,rgba(0));ctx.fillStyle=scrim;ctx.fillRect(-ry,-ry,ry*2,ry*2);ctx.restore();
    ctx.textAlign=centered?'center':'left';ctx.textBaseline='alphabetic';ctx.shadowColor='rgba(0,0,0,.38)';ctx.shadowBlur=28*unit;
    if(eyebrow){const p=progress(0);ctx.globalAlpha=p;ctx.fillStyle=shot.accent;ctx.font=`650 ${eyebrow}px ${FONT}`;ctx.letterSpacing=`${(portrait?5:4)*unit}px`;ctx.fillText(shot.subtitle.toUpperCase(),px,y+(1-p)*eyebrow*.8);y+=gap;}
    ctx.letterSpacing=`${-1.2*unit}px`;ctx.fillStyle=shot.titleColor;ctx.font=`650 ${size}px ${FONT}`;ctx.textAlign='left';let word=1;
    for(const text of lines){const words=text.split(' '),space=ctx.measureText(' ').width,widths=words.map(w=>ctx.measureText(w).width);let x=centered?px-(widths.reduce((a,b)=>a+b,0)+space*(words.length-1))/2:px;
      for(let k=0;k<words.length;k++){const p=progress(word++);if(p>0){ctx.globalAlpha=Math.min(1,p*1.4);
        if(shot.titleMotion==='pop'){ctx.save();ctx.translate(x+widths[k]/2,y);ctx.scale(.82+.18*p,.82+.18*p);ctx.fillText(words[k],-widths[k]/2,0);ctx.restore();}
        else ctx.fillText(words[k],x,y+(1-p)*size*.55);}
        x+=widths[k]+space;}
      y+=line;}
    ctx.globalAlpha=1;}
  private drawTitle(shot:Shot,width:number,height:number,local?:number){const words=shot.title.split(/\s+/).length+1,animating=local!==undefined&&local<.8+words*.07;const hash=[shot.title,shot.subtitle,shot.accent,shot.titleColor,shot.titleLayout,shot.titleScale,shot.titleMotion,width,height,animating?Math.round(local!*240):'done'].join('|');if(hash===this.currentTitle)return;this.currentTitle=hash;this.paintTitle(this.titleCanvas,shot,width,height,animating?local:undefined);this.titleTexture.dispose();this.titleTexture.needsUpdate=true;}
  /** The same title layer as a transparent PNG, for the cinematic renderer. */
  titlePng(shot:Shot,width:number,height:number){const c=document.createElement('canvas');this.paintTitle(c,shot,width,height);return c.toDataURL('image/png').split(',')[1];}
  invalidate(){this.currentShot='';this.currentTitle='';}
  cameraState(time:number){return {time,position:this.camera.position.toArray() as Vec3,target:this.controls.target.toArray() as Vec3,fov:this.camera.fov};}
  async updateLive(base64:string,width:number,height:number,elementId?:string,mime='image/png'){if(this.busyLive&&!elementId)return;if(!elementId)this.busyLive=true;try{const img=new Image();img.src=`data:${mime};base64,${base64}`;await img.decode();const old=elementId?this.layerTextures.get(elementId):this.liveTexture;const texture=this.prepareTexture(new T.Texture(img));texture.needsUpdate=true;if(elementId)this.layerTextures.set(elementId,texture);else{this.liveTexture=texture;this.liveImage=img;this.sourceSize={width,height};}old?.dispose();}finally{if(!elementId)this.busyLive=false;}}
  private hit(event:PointerEvent|WheelEvent):Record<string,unknown>|null {const rect=this.canvas.getBoundingClientRect();this.raycaster.setFromCamera(new T.Vector2((event.clientX-rect.left)/rect.width*2-1,-(event.clientY-rect.top)/rect.height*2+1),this.camera);const hits=this.raycaster.intersectObjects([this.device,this.layers],true);for(const hit of hits){if(!hit.object.userData.input||!hit.uv)continue;const coord=sourceUV(hit.uv,hit.object.userData.crop,this.sourceSize.width,this.sourceSize.height);return {...coord,elementId:hit.object.userData.elementId,width:this.sourceSize.width,height:this.sourceSize.height};}return null;}
  png(){return this.canvas.toDataURL('image/png').split(',')[1];}
}

/**
 * Depth of field like a fast lens on the app: everything within a band around the focus distance stays fully sharp
 * (so the app's text never softens, even turned or tilted) and the world beyond it blurs with distance. Glows, halos
 * and shadows carry no depth of their own, so the background behind them blurs too instead of leaving sharp patches.
 */
class FocusPass extends BokehPass {
  constructor(scene:T.Scene,camera:T.Camera){
    super(scene,camera,{focus:10,aperture:0,maxblur:0});
    const m=this.materialBokeh;m.uniforms.band={value:1};
    m.fragmentShader=m.fragmentShader.replace('uniform float focus;','uniform float focus;\nuniform float band;')
      .replace('float factor = ( focus + viewZ );','float gap = focus + viewZ; float factor = sign( gap ) * max( abs( gap ) - band, 0.0 );');
    if(!m.fragmentShader.includes('float gap'))throw Error('Depth of field shader changed; update FocusPass.');
    m.needsUpdate=true;
  }
  override render(...args:Parameters<BokehPass['render']>){
    const hidden:T.Object3D[]=[];
    this.scene.traverseVisible(o=>{const m=(o as T.Mesh).material;if(m&&!Array.isArray(m)&&(m as T.Material).depthWrite===false)hidden.push(o);});
    hidden.forEach(o=>o.visible=false);
    try{super.render(...args);}finally{hidden.forEach(o=>o.visible=true);}
  }
}
