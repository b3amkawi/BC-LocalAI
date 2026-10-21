import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createRequire} from 'node:module';
import vm from 'node:vm';
const require=createRequire(import.meta.url), AuthCore=require('../../BC-LocalAI/wwwroot/js/auth-core.js');
const source=readFileSync(new URL('../../BC-LocalAI/wwwroot/js/login.js',import.meta.url),'utf8');
function mount(protocol='https:') {
    const events={},windowEvents={},nodes={};let posts=0,resolveLogin,checks=0;
    const get=id=>nodes[id]||(nodes[id]={value:'',hidden:false,disabled:false,textContent:'',setAttribute(){},focus(){},
        addEventListener:(type,fn)=>{events[id+':'+type]=fn;},set innerHTML(_){assert.fail('Unsafe HTML');}});
    const response=new Promise(resolve=>{resolveLogin=resolve;});
    const window={location:{protocol,replace:path=>{window.destination=path;}},fetch:()=>assert.fail('Use reviewed transport'),
        addEventListener:(type,fn)=>{windowEvents[type]=fn;}};
    const context={window,document:{getElementById:get},AuthCore,FrontendHttp:{create:()=>({authTransport:async(path)=>{
        if(path.endsWith('/csrf'))return {status:200,json:async()=>({token:'fresh_csrf'})};
        if(path.endsWith('/login')){posts++;return response;}checks++;return {status:401,json:async()=>({})};
    }})},BroadcastChannel:class{postMessage(value){window.broadcast=value;}close(){}}};
    for(const target of [context,window])for(const name of ['localStorage','sessionStorage','indexedDB'])Object.defineProperty(target,name,{get:()=>assert.fail('No browser storage')});
    vm.runInNewContext(source,{...context,AbortController,TextEncoder});
    return {get,window,posts:()=>posts,checks:()=>checks,resolveLogin,
        submit:()=>events['login-form:submit']({preventDefault(){}}),hide:()=>windowEvents.pagehide(),show:()=>windowEvents.pageshow({persisted:true})};
}
async function settle(){for(let i=0;i<8;i++)await new Promise(setImmediate);}
test('real Login clears password immediately and blocks duplicate submission',async()=>{
    const page=mount();await settle();page.get('login-username').value='synthetic.user';page.get('login-password').value='placeholder'; // ci-allow-secret: synthetic input only.
    page.submit();assert.equal(page.get('login-password').value,'');page.submit();await settle();assert.equal(page.posts(),1);
    page.resolveLogin({status:401,json:async()=>({error:{message:'private detail'}})});await settle();
    assert.equal(page.get('auth-error').textContent,AuthCore.MESSAGES.AUTH_INVALID_CREDENTIALS);
});
test('successful Login uses fixed local redirect and no private broadcast payload',async()=>{
    const page=mount();await settle();page.get('login-username').value='synthetic.user';page.get('login-password').value='placeholder'; // ci-allow-secret: synthetic input only.
    page.submit();await settle();page.resolveLogin({status:200,json:async()=>({user:{id:'00000000-0000-4000-8000-000000000001',username:'synthetic.user',displayName:'สมมติ',roles:['User']}})});await settle();
    assert.equal(page.window.destination,'/');assert.equal(JSON.stringify(page.window.broadcast),' {"type":"auth-changed"}'.trim());
});
test('HTTP cannot send Login or initial profile request',async()=>{
    const page=mount('http:');await settle();assert.equal(page.posts(),0);assert.equal(page.checks(),0);assert.match(page.get('auth-status').textContent,/HTTPS/);
});
test('bfcache clears credentials and rechecks instead of leaving Login disabled',async()=>{
    const page=mount();await settle();page.get('login-username').value='synthetic.user';page.get('login-password').value='placeholder'; // ci-allow-secret: synthetic input only.
    page.hide();assert.equal(page.get('login-username').value,'');assert.equal(page.get('login-password').value,'');
    const checks=page.checks();page.show();await settle();assert.equal(page.checks(),checks+1);assert.equal(page.get('login-submit').disabled,false);
});
