const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const root=path.resolve(__dirname,'../..'),html=fs.readFileSync(path.join(root,'test.html'),'utf8');
for(const match of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)){
  const src=match[1].match(/\bsrc="([^"]+)"/i);
  if(src){const file=path.join(root,src[1]);new vm.Script(fs.readFileSync(file,'utf8'),{filename:file});}
  else if(match[2].trim())new vm.Script(match[2],{filename:'test.html',lineOffset:html.slice(0,match.index).split('\n').length-1});
}

