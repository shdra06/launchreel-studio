import type {Rect} from './project.ts';

/** Windows 11 rounds top-level windows to 8 px; cutouts of an app use the same radius so its corners come out clean. */
export const WINDOW_CORNER=8;

/**
 * Snaps a rectangle drawn on the app's picture to the app's own borders, so a cutout never carries a sliver of the
 * desktop or a neighbouring window. For each side it looks for the longest unbroken straight edge within reach:
 * text and icons inside the app give short, broken edges and lose, while a window's border against whatever is
 * behind it runs on even where another window covers part of it. Of near-equal edges the one nearest the drawn line
 * wins, and the outer one on a tie, so a loose cut keeps the app's frame rather than a card inside it. A side with
 * no clear edge stays where it was drawn.
 */
export function snapToEdges(pixels:ArrayLike<number>,width:number,height:number,area:Rect,reach=Math.max(6,Math.round(width*.012))):Rect{
  const lum=(x:number,y:number)=>{const i=(y*width+x)*4;return .299*pixels[i]+.587*pixels[i+1]+.114*pixels[i+2];};
  const x0=Math.round(area[0]*width),y0=Math.round(area[1]*height),x1=Math.round((area[0]+area[2])*width),y1=Math.round((area[1]+area[3])*height);
  // Measure along the middle of each side, clear of the rounded corners.
  const mid=(a:number,b:number,limit:number)=>[Math.max(0,a+Math.round((b-a)*.12)),Math.min(limit,b-Math.round((b-a)*.12))] as const;
  const [ra,rb]=mid(y0,y1,height),[ca,cb]=mid(x0,x1,width);
  // Longest run (as a share of the side) of clear steps between pixel c-1 and c, bridging gaps of up to 2 pixels.
  const run=(from:number,to:number,step:(i:number)=>boolean)=>{let best=0,start=-1,gap=0;
    for(let i=from;i<to;i++){if(step(i)){if(start<0)start=i;gap=0;best=Math.max(best,i-start+1);}else if(start>=0&&++gap>2)start=-1;}
    return to>from?best/(to-from):0;};
  const column=(c:number)=>c<1||c>=width?0:run(ra,rb,y=>Math.abs(lum(c,y)-lum(c-1,y))>10);
  const row=(r:number)=>r<1||r>=height?0:run(ca,cb,x=>Math.abs(lum(x,r)-lum(x,r-1))>10);
  const snap=(at:number,score:(c:number)=>number,outward:1|-1)=>{
    const scores:Array<[number,number]>=[];for(let c=at-reach;c<=at+reach;c++)scores.push([c,score(c)]);
    const best=Math.max(...scores.map(s=>s[1]));if(best<.4)return at;
    const near=(c:number)=>Math.abs(c-at)*2-((c-at)*outward>0?1:0);
    return scores.filter(s=>s[1]>=best-.08).reduce((a,b)=>near(b[0])<near(a[0])?b:a)[0];
  };
  const left=snap(x0,column,-1),right=snap(x1,column,1),top=snap(y0,row,-1),bottom=snap(y1,row,1);
  if(right-left<8||bottom-top<8)return area;
  return [left/width,top/height,(right-left)/width,(bottom-top)/height];
}
