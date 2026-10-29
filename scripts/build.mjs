import { build } from 'esbuild';
import { cp, mkdir } from 'node:fs/promises';
await mkdir('dist/web', { recursive: true });
await build({entryPoints:['web/main.ts'],bundle:true,format:'esm',target:'es2022',outfile:'dist/web/main.js',sourcemap:true});
await cp('web/index.html','dist/web/index.html');
await cp('web/style.css','dist/web/style.css');
console.log('Studio web assets built.');
