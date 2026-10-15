"use strict";
const api = globalThis.browser || globalThis.chrome;
const IS_FIREFOX=!!globalThis.browser?.runtime.getBrowserInfo;
let routingSerial=Promise.resolve();let routingEnabled=false;
const HOST = "com.downloadmanager.bridge";
const defaults = { enabled: false, extensions: "zip rar 7z exe msi iso pdf mp4 mkv mp3", excludedSites: "" };
function validUrls(values) {
  const unique = new Set();
  for (const value of values.slice(0,1000)) {
    if (typeof value !== "string" || value.length > 16384 || /[\x00-\x1f\x7f]/.test(value)) continue;
    try { const url = new URL(value); if (!["http:","https:"].includes(url.protocol) || url.username || url.password) continue; url.hash=""; unique.add(url.href); } catch {}
  }
  const result=[...unique]; if (!result.length || new TextEncoder().encode(JSON.stringify(result)).length > 800000) throw new Error("invalidLinks"); return result;
}
function native(request,timeoutMs=290000) {
  return new Promise((resolve,reject) => {
    const port=api.runtime.connectNative(HOST); let done=false;
    const timer=setTimeout(()=>finish(new Error("bridgeUnavailable")),timeoutMs);
    function finish(error,reply) { if(done)return;done=true;clearTimeout(timer);try{port.disconnect();}catch{} if(error)reject(error);else resolve(reply); }
    port.onMessage.addListener(reply=>{if(reply?.version!==1 || (reply.id && reply.id!==request.id))return finish(new Error("bridgeUnavailable"));finish(null,reply);});
    port.onDisconnect.addListener(()=>{void api.runtime.lastError;finish(new Error("bridgeUnavailable"));});
    port.postMessage(request);
  });
}
async function sendUrls(values,media,providedContext,id,responseMetadata) { const urls=validUrls(values);const context=providedContext;return await native({version:1,id:id||crypto.randomUUID(),action:"import",urls,...(media?{media:true}:{}),...(context?{context}:{}),...(responseMetadata?{responseMetadata}:{})}); }
async function resultStatus(key) {
 await api.storage.session.set({lastStatus:key});
 const options=await api.storage.local.get(defaults);
 const label=api.i18n.getMessage(options.enabled?"extensionOn":"extensionOff");
 const status=api.i18n.getMessage(key);
 await api.action.setTitle({title:label+(status?" — "+status:"")});
}
api.runtime.onMessage.addListener((message,sender,sendResponse)=>{
  if(sender.id!==api.runtime.id)return false;
  (async()=>{
    switch(message?.action){
      case "ping": return await native({version:1,id:crypto.randomUUID(),action:"ping",urls:[]});
      case "setEnabled": return await setEnabled(message.enabled===true);
      case "captureFailure":await resultStatus(message.status||"bridgeUnavailable");return{ok:true};
      case "routingState":return {ok:true,...await api.storage.local.get(defaults)};
      case "intercept":if(sender.tab?.incognito)return{ok:false,status:"unsupportedSource"};return await intercept(message.url,message.context,message.id,message.pageUrl,sender.tab?.id);
      default: throw new Error("invalidLinks");
    }
  })().then(sendResponse,()=>sendResponse({ok:false,status:"bridgeUnavailable"}));return true;
});
function excluded(url,patterns) {
  const host=new URL(url).hostname;
  return patterns.split(/[\s,;]+/).filter(Boolean).some(pattern=>{
    const escaped=pattern.replace(/[.+?^${}()|[\]\\]/g,"\\$&").replace(/\*/g,".*");try{return new RegExp("^"+escaped+"$","i").test(host)||new RegExp("^"+escaped+"$","i").test(url);}catch{return false;}
  });
}
// Session data stays in memory; the desktop encrypts it for the current user.
const recentRequests=new Map();
const responseMetadata=new Map();
function takeResponse(details){const value=responseMetadata.get(details.requestId);responseMetadata.delete(details.requestId);if(!value||value.url!==details.url||value.tabId!==details.tabId||Date.now()-value.at>30000)return undefined;const {tabId:_,at:__,...metadata}=value;return metadata;}
function rememberResponse(details){
 if(details.incognito||!["main_frame","sub_frame"].includes(details.type)||details.statusCode<200||details.statusCode>299)return;
 const header=name=>details.responseHeaders?.find(h=>h.name.toLowerCase()===name)?.value||"";
 const disposition=header("content-disposition"),type=header("content-type"),length=header("content-length");
 if(disposition.length>8192||type.length>256||/[\x00-\x1f\x7f]/.test(disposition+type))return;
 const size=/^\d+$/.test(length)&&!header("content-encoding")&&Number.isSafeInteger(Number(length))?Number(length):null;
 responseMetadata.set(details.requestId,{url:details.url,tabId:details.tabId,at:Date.now(),contentDisposition:disposition,contentType:type,size,statusCode:details.statusCode});
 for(const [id,value] of responseMetadata)if(Date.now()-value.at>30000||responseMetadata.size>100)responseMetadata.delete(id);
}
function rememberRequest(details,headers) {
  if(details.incognito||!routingEnabled||!["main_frame","sub_frame"].includes(details.type))return;
  let url;try{url=validUrls([details.url])[0];}catch{return;}
  const previous=recentRequests.get(details.requestId);
  const old=(previous?.url===url?previous:undefined)||{url,method:details.method,headers:{},at:Date.now(),tabId:details.tabId};
  recentRequests.set(details.requestId,old);
  for(const [id,value]of recentRequests)if(Date.now()-value.at>300000||recentRequests.size>100)recentRequests.delete(id);
  if(headers)for(const h of details.requestHeaders||[])if(["cookie","authorization","referer","user-agent","accept","accept-language","content-type"].includes(h.name.toLowerCase())&&typeof h.value==="string"&&h.value.length<=65536&&!/[\r\n]/.test(h.value))old.headers[h.name.toLowerCase()]=h.value;
  if(details.requestBody){
    if(details.requestBody.raw){const chunks=details.requestBody.raw.map(x=>x.bytes?new Uint8Array(x.bytes):null);if(chunks.some(x=>!x)){old.bodyUnavailable=true;return;}const size=chunks.reduce((n,x)=>n+x.length,0);if(size>262144){old.bodyUnavailable=true;return;}const bytes=new Uint8Array(size);let i=0;for(const chunk of chunks){bytes.set(chunk,i);i+=chunk.length;}let binary="";for(const byte of bytes)binary+=String.fromCharCode(byte);old.bodyBase64=btoa(binary);}
    else if(details.requestBody.formData){const form=new URLSearchParams();for(const [key,values]of Object.entries(details.requestBody.formData))for(const value of values)form.append(key,value);const text=form.toString();if(text.length>262144){old.bodyUnavailable=true;return;}old.formEncoded=true;old.bodyBase64=btoa(text);old.headers["content-type"]="application/x-www-form-urlencoded";}
  }
  if(old.formEncoded&&old.headers["content-type"]&&!old.headers["content-type"].toLowerCase().startsWith("application/x-www-form-urlencoded"))old.bodyUnavailable=true;
  recentRequests.set(details.requestId,old);
  for(const [id,value]of recentRequests)if(Date.now()-value.at>300000||recentRequests.size>100)recentRequests.delete(id);
}
if(api.webRequest){
 api.webRequest.onBeforeRequest.addListener(d=>rememberRequest(d,false),{urls:["http://*/*","https://*/*"]},["requestBody"]);
 try{api.webRequest.onBeforeSendHeaders.addListener(d=>rememberRequest(d,true),{urls:["http://*/*","https://*/*"]},["requestHeaders","extraHeaders"]);}catch{api.webRequest.onBeforeSendHeaders.addListener(d=>rememberRequest(d,true),{urls:["http://*/*","https://*/*"]},["requestHeaders"]);}
}
async function requestContext(url,useRecorded,tabId) {
 try{
  if(!api.permissions||!await api.permissions.contains({origins:[new URL(url).origin+"/*"],permissions:["cookies"]}))return undefined;
  const recorded=[...recentRequests.values()].reverse().find(v=>v.url===url&&(tabId===undefined||v.tabId===tabId)&&(useRecorded||v.method==="GET")&&Date.now()-v.at<300000);
  const headers={...(recorded?.headers||{})};
  if(!headers.cookie){const tab=tabId!==undefined?await api.tabs.get(tabId):undefined;const cookies=await api.cookies.getAll({url,...(tab?.cookieStoreId?{storeId:tab.cookieStoreId}:{})});headers.cookie=cookies.filter(c=>!c.partitionKey).sort((a,b)=>b.path.length-a.path.length).map(c=>c.name+"="+c.value).join("; ");}
  if(!headers.referer){const tab=tabId!==undefined?await api.tabs.get(tabId):(await api.tabs.query({active:true,currentWindow:true}))[0];if(tab?.url&&new URL(tab.url).origin===new URL(url).origin)headers.referer=tab.url;}
  if(recorded?.method==="POST"&&(recorded.bodyUnavailable||!recorded.bodyBase64))return undefined;
  return{url,method:recorded?.method||"GET",headers,bodyBase64:recorded?.bodyBase64||""};
 }catch{return undefined;}
}

const passiveExtensions=new Set("json webmanifest js mjs cjs css map wasm xml html htm svg ico png jpg jpeg gif webp avif woff woff2 ttf mp4 webm mkv mov avi mp3 m4a ogg wav flac m3u8 mpd ts m4s aac".split(" "));
function wanted(url,options){try{const u=new URL(url);if(!["http:","https:"].includes(u.protocol)||excluded(url,options.excludedSites))return false;const suffix=(decodeURIComponent(u.pathname).split(".").pop()||"").toLowerCase();return !passiveExtensions.has(suffix)&&options.extensions.toLowerCase().split(/[\s,;]+/).includes(suffix);}catch{return false;}}
async function intercept(url,provided,id,pageUrl,tabId,capturedResponse,beforeHandoff){
 const options=await api.storage.local.get(defaults);if(!options.enabled)return {ok:false,status:"extensionOff"};
 let safe;try{safe=validUrls([url])[0];}catch{return {ok:false,status:"unsupportedSource"};}
 if(excluded(safe,options.excludedSites))return{ok:false,status:"unsupportedSource"};
 let context=provided||await requestContext(safe,true,tabId);
 if(provided){context={...provided,url:safe};const captured=await requestContext(safe,false,tabId);context.headers={...(captured?.headers||{}),...(provided.headers||{})};}
 if(!context&&[...recentRequests.values()].some(v=>v.url===safe&&(tabId===undefined||v.tabId===tabId)&&v.method==="POST"&&Date.now()-v.at<300000))return{ok:false,status:"postUnavailable"};
 if(pageUrl&&context&&!context.headers.referer){try{if(["http:","https:"].includes(new URL(pageUrl).protocol))context.headers.referer=pageUrl;}catch{}}
 try{if(beforeHandoff)await beforeHandoff();const reply=await sendUrls([safe],false,context,id,capturedResponse);if(!reply.ok&&["unavailable","closing"].includes(reply.status))await setEnabled(false);await resultStatus(reply.ok?"accepted":reply.status||"cancelled");return reply;}catch{await setEnabled(false);await resultStatus("bridgeUnavailable");return {ok:false,status:"bridgeUnavailable"};}
}
async function setEnabled(enabled){
 if(enabled){
  try{const ready=await native({version:1,id:crypto.randomUUID(),action:"ping",urls:[]},25000);if(!ready.ok)throw new Error("bridgeUnavailable");}
  catch{await setEnabled(false);await resultStatus("bridgeUnavailable");return{ok:false,enabled:false,status:"bridgeUnavailable"};}
 }
 try{await api.storage.local.set({enabled});await reconcileRouting();if(!enabled){recentRequests.clear();responseMetadata.clear();attachmentNavigations.clear();committedPages.clear();newTabs.clear();handledRequests.clear();}return{ok:true,enabled};}
 catch{await api.storage.local.set({enabled:false});routingEnabled=false;recentRequests.clear();responseMetadata.clear();attachmentNavigations.clear();committedPages.clear();newTabs.clear();handledRequests.clear();try{await reconcileRouting();}catch{}await resultStatus("routingUnavailable");return{ok:false,enabled:false,status:"routingUnavailable"};}
}
function reconcileRouting(){routingSerial=routingSerial.catch(()=>{}).then(async()=>{
 const options=await api.storage.local.get(defaults);routingEnabled=options.enabled;
 if(!IS_FIREFOX&&api.declarativeNetRequest){
  const types=["main_frame","sub_frame"];const block={type:"block"};
  const suffixes=options.extensions.split(/[\s,;]+/).map(s=>s.replace(/^\./,"")).filter(s=>/^[a-zA-Z0-9]{1,12}$/.test(s)&&!passiveExtensions.has(s.toLowerCase())).slice(0,80);
  const base={excludedInitiatorDomains:[api.runtime.id]};const rules=[];
  if(options.enabled){
   let ruleId=1000;for(let i=0;i<suffixes.length;i+=12){const pattern="^https?://[^?#]+\\.(?:"+suffixes.slice(i,i+12).join("|")+")(?:[?].*)?$";rules.push({id:++ruleId,priority:10,action:block,condition:{...base,regexFilter:pattern,resourceTypes:types,requestMethods:["get"],isUrlFilterCaseSensitive:false}});}
   rules.push({id:1050,priority:5,action:block,condition:{...base,regexFilter:"^https?://.*$",resourceTypes:types,responseHeaders:[{header:"content-disposition",values:["*attachment*"]}]}});
   let next=1100;for(const pattern of options.excludedSites.split(/[\s,;]+/).filter(Boolean).slice(0,100)){let urlFilter=pattern.includes("://")?pattern:"||"+pattern.replace(/^\*\./,"")+"^";rules.push({id:next++,priority:100,action:{type:"allow"},condition:{urlFilter,resourceTypes:["main_frame","sub_frame","other"]}});}
  }
  const existing=await api.declarativeNetRequest.getDynamicRules();await api.declarativeNetRequest.updateDynamicRules({removeRuleIds:existing.map(r=>r.id),addRules:rules});
 }
 if(api.scripting?.getRegisteredContentScripts){const registered=await api.scripting.getRegisteredContentScripts({ids:["download-manager-routing"]});if(options.enabled&&!registered.length){await api.scripting.registerContentScripts([{id:"download-manager-routing",matches:["http://*/*","https://*/*"],js:["intercept.js"],runAt:"document_start",allFrames:true,persistAcrossSessions:true}]);const tabs=await api.tabs.query({url:["http://*/*","https://*/*"]});for(const tab of tabs)if(tab.id&&tab.url)try{await api.scripting.executeScript({target:{tabId:tab.id,allFrames:true},files:["intercept.js"]});}catch{}}else if(!options.enabled&&registered.length)await api.scripting.unregisterContentScripts({ids:["download-manager-routing"]});}
 if(options.enabled&&!committedPages.size){for(const tab of await api.tabs.query({url:["http://*/*","https://*/*"]}))if(tab.id&&tab.url&&!tab.incognito)committedPages.set(tab.id,tab.url);}
 if(api.action){if(api.action.setIcon)await api.action.setIcon({path:Object.fromEntries([16,19,24,32,38].map(n=>[n,`icons/${options.enabled?"on":"off"}-${n}.png`]))});await api.action.setTitle({title:api.i18n.getMessage(options.enabled?"extensionOn":"extensionOff")});await api.action.setBadgeText({text:options.enabled?"ON":"OFF"});await api.action.setBadgeBackgroundColor({color:options.enabled?"#176b4b":"#59616c"});}
 });return routingSerial;}
if(api.storage.onChanged)api.storage.onChanged.addListener((changes,area)=>{if(area==="local"&&["enabled","extensions","excludedSites"].some(k=>k in changes))void reconcileRouting().catch(async()=>{await setEnabled(false);await resultStatus("routingUnavailable");});});
if(api.runtime.onStartup)api.runtime.onStartup.addListener(()=>void reconcileRouting().catch(()=>setEnabled(false)));
void reconcileRouting().catch(async()=>{await setEnabled(false);await resultStatus("routingUnavailable");});
const attachmentNavigations=new Set(),handledRequests=new Set(),newTabs=new Set();
const committedPages=new Map();
api.webNavigation?.onCommitted.addListener(details=>{
 if(routingEnabled&&details.frameId===0&&/^https?:/i.test(details.url)){committedPages.set(details.tabId,details.url);newTabs.delete(details.tabId);if(committedPages.size>100)committedPages.delete(committedPages.keys().next().value);}
});
api.tabs.onCreated?.addListener(tab=>{if(routingEnabled&&!tab.incognito)newTabs.add(tab.id);});
api.tabs.onRemoved?.addListener(id=>{committedPages.delete(id);newTabs.delete(id);});
async function restoreNavigation(details){
 if(details.type!=="main_frame"||details.tabId<0)return;
 try{
  await new Promise(resolve=>setTimeout(resolve,100));
  const tab=await api.tabs.get(details.tabId);if(tab.url!==details.url&&tab.pendingUrl!==details.url)return;
  const previous=committedPages.get(details.tabId);
  if(previous&&previous!==details.url){try{await api.tabs.goBack(details.tabId);}catch{await api.tabs.update(details.tabId,{url:previous});}}
  else if(newTabs.has(details.tabId)&&tab.openerTabId!==undefined){await api.tabs.remove(details.tabId);}
  else await api.tabs.update(details.tabId,{url:"about:blank"});
 }catch{}
}

if(!IS_FIREFOX&&api.webRequest?.onHeadersReceived)api.webRequest.onHeadersReceived.addListener(details=>{
 if(!routingEnabled||details.incognito||!["main_frame","sub_frame"].includes(details.type))return;
 rememberResponse(details);
 if(details.responseHeaders?.some(h=>h.name.toLowerCase()==="content-disposition"&&/^\s*attachment(?:\s*;|\s*$)/i.test(h.value||""))){attachmentNavigations.add(details.requestId);if(attachmentNavigations.size>100)attachmentNavigations.delete(attachmentNavigations.values().next().value);}
},{urls:["http://*/*","https://*/*"]},["responseHeaders"]);
if(!IS_FIREFOX&&api.webRequest?.onErrorOccurred)api.webRequest.onErrorOccurred.addListener(async details=>{
 const attachment=attachmentNavigations.delete(details.requestId);
 if(!routingEnabled||details.incognito||!["main_frame","sub_frame"].includes(details.type)||!String(details.error).includes("BLOCKED_BY_CLIENT"))return;
 const options=await api.storage.local.get(defaults);if(!options.enabled||excluded(details.url,options.excludedSites)||(!attachment&&!(details.method==="GET"&&wanted(details.url,options))))return;
 if(handledRequests.has(details.requestId))return;handledRequests.add(details.requestId);if(handledRequests.size>100)handledRequests.delete(handledRequests.values().next().value);
 await intercept(details.url,undefined,undefined,details.documentUrl||details.initiator,details.tabId,takeResponse(details),()=>restoreNavigation(details));
},{urls:["http://*/*","https://*/*"]});
// Firefox permits a blocking response-header listener; Chrome uses DNR above.
if(IS_FIREFOX&&api.webRequest?.onHeadersReceived)api.webRequest.onHeadersReceived.addListener(async details=>{
 if(details.incognito||!["main_frame","sub_frame"].includes(details.type))return{};const options=await api.storage.local.get(defaults);if(!options.enabled||excluded(details.url,options.excludedSites))return{};
 rememberResponse(details);
 const attachment=details.responseHeaders?.some(h=>h.name.toLowerCase()==="content-disposition"&&/attachment/i.test(h.value||""));
 if(!wanted(details.url,options)&&!attachment)return{};
 if(!handledRequests.has(details.requestId)){handledRequests.add(details.requestId);if(handledRequests.size>100)handledRequests.delete(handledRequests.values().next().value);void intercept(details.url,undefined,undefined,details.documentUrl||details.originUrl,details.tabId,takeResponse(details));}return {cancel:true};
},{urls:["http://*/*","https://*/*"]},["blocking","responseHeaders"]);

let changingRouting=false;
async function toggleCapture(permission){
 if(changingRouting)return;changingRouting=true;
 try{const options=await api.storage.local.get(defaults);const enabled=!options.enabled;
 if(enabled&&!await permission){await setEnabled(false);return;}
 const result=await setEnabled(enabled);if(!result.ok)await api.action.setTitle({title:api.i18n.getMessage("routingUnavailable")});
 }catch{await setEnabled(false);}finally{changingRouting=false;}
}
api.action.onClicked.addListener(()=>{
 if(changingRouting)return;
 // Request optional cookie access synchronously in the real toolbar user gesture.
 const permission=routingEnabled?Promise.resolve(true):api.permissions.request({permissions:["cookies"]});
 void toggleCapture(permission);
});
