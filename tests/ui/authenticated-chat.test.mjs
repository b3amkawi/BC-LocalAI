import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
const require = createRequire(import.meta.url), auth = require('../../BC-LocalAI/wwwroot/js/auth-core.js');
const history = require('../../BC-LocalAI/wwwroot/js/history-core.js'), chat = require('../../BC-LocalAI/wwwroot/js/chat-core.js');
const source = readFileSync(new URL('../../BC-LocalAI/wwwroot/js/authenticated-chat.js', import.meta.url), 'utf8');
const guid = n => '00000000-0000-4000-8000-' + n.toString(16).padStart(12,'0');
const A=guid(1), B=guid(2), C=guid(3), D=guid(4), date='2026-10-21T08:00:00Z';
const person = id => ({id,username:'synthetic.'+id.slice(-1),displayName:'ผู้ใช้ '+id.slice(-1),roles:['User']});
const conversation = (id,title) => ({id,title,createdAt:date,updatedAt:date});
const deferred = () => { let resolve; const promise=new Promise(done=>{resolve=done;}); return {promise,resolve}; };
class Element {
    constructor(tag='div') { this.tag=tag; this.children=[]; this.events={}; this.attributes={}; this.hidden=false; this.disabled=false;
        this.value=''; this.textContent=''; this.dataset={}; this.scrollHeight=100; this.clientHeight=100; this.scrollTop=0; this.isConnected=true; this.open=false; }
    set innerHTML(_) { throw new Error('Unsafe HTML sink'); }
    setAttribute(k,v) { this.attributes[k]=v; }
    addEventListener(k,fn) { this.events[k]=fn; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children=[...children]; }
    querySelectorAll(tag) { return this.children.flatMap(child=>[...(child.tag===tag?[child]:[]),...child.querySelectorAll(tag)]); }
    focus() { this.focused=true; }
    showModal() { this.open=true; }
    close() { this.open=false; }
}
function mount(options={}) {
    const nodes={}, events={}, windowEvents={}, channels=[], timers=[], requests=[], streams=[];
    const node=id=>nodes[id]||(nodes[id]=new Element());
    node('composer').dataset.timeoutSeconds='200';
    const fixture={user:person(A),rows:{[A]:[conversation(C,'A private')],[B]:[conversation(D,'B private')]},messages:[],...options};
    const document={body:{dataset:{historyEnabled:options.historyEnabled===false?'false':'true',idleMinutes:'30'}},visibilityState:'visible',activeElement:null,
        getElementById:node,createElement:tag=>new Element(tag),addEventListener:(k,fn)=>{events[k]=fn;}};
    let getActor;
    const http={
        authTransport:async(path)=>{
            if(path.endsWith('/csrf')) return {status:200,json:async()=>({token:'fresh_csrf'})};
            if(path.endsWith('/logout')) { fixture.logoutActor=getActor(); fixture.user=null; return {status:204}; }
            return fixture.user?{status:200,json:async()=>({user:fixture.user})}:{status:401,json:async()=>({})};
        },
        request:async(path,init={})=>{
            requests.push({path,init});
            if(fixture.failure) throw fixture.failure;
            if(path==='/api/auth/me') return {user:fixture.user};
            if(path==='/api/conversations' && init.method==='POST') {
                fixture.createdTitle=init.body.title;const made=conversation(C,init.body.title);fixture.rows[init.actor].push(made);return made;
            }
            if(path.startsWith('/api/conversations?')) {
                if(fixture.pendingList && init.actor===A) return fixture.pendingList.promise;
                return {items:fixture.rows[init.actor],nextCursor:null};
            }
            if(path.includes('/messages')) return fixture.pages?fixture.pages(path):{items:fixture.messages,nextCursor:null};
            const id=path.split('/')[3];
            return Object.values(fixture.rows).flat().find(row=>row.id===id);
        },
        stream:async(body,init)=>{streams.push({body,init}); return init.consume({},init.signal);}
    };
    const window={fetch:()=>assert.fail('Must use injected transport'),location:{protocol:'https:',replace:path=>{fixture.navigation=path;}},
        addEventListener:(k,fn)=>{windowEvents[k]=fn;}};
    class Channel {
        constructor() { channels.push(this); }
        addEventListener(_,fn) { this.listener=fn; }
        postMessage(value) { fixture.broadcast=value; }
        close() { this.closed=true; }
    }
    const context={document,window,AuthCore:auth,HistoryCore:history,ChatCore:chat,AbortController,TextEncoder,
        FrontendHttp:{fault:(code,status=0)=>Object.assign(new Error(code),{code,status}),create:(_,config)=>{getActor=config.getActor;return http;}},
        ChatStream:{STATUS_TEXT:{streaming:'กำลังสร้าง'},readAnswer:async(_,update,settings)=>{
            fixture.update=update; fixture.streamSettings=settings;
            settings.onEvent({event:'started',data:JSON.stringify({model:'qwen3:4b',conversationId:C,messageId:D})});
            update({state:'streaming',text:'คำตอบ A',model:'qwen3:4b'});
            if(fixture.pendingStream) return fixture.pendingStream.promise;
            return {state:'completed',text:'คำตอบ A'};
        }},BroadcastChannel:Channel,setTimeout:(fn,ms)=>{const timer={fn,ms};timers.push(timer);return timer;},clearTimeout:()=>{},
        console:{log:()=>assert.fail('No sensitive logging'),error:()=>assert.fail('No sensitive logging')}};
    for(const target of [context,window]) for(const name of ['localStorage','sessionStorage','indexedDB'])
        Object.defineProperty(target,name,{get:()=>assert.fail('No persistent browser storage')});
    vm.runInNewContext(source,context);
    return {nodes,node,fixture,requests,streams,channels,timers,
        click:async id=>{assert.equal(node(id).disabled,false,id+' must be enabled'); await node(id).events.click?.();},
        submit:()=>node('composer').events.submit({preventDefault(){}}),
        key:event=>node('message').events.keydown({...event,preventDefault(){}}),
        select:async()=>{const button=node('conversation-list').querySelectorAll('button')[0];assert.ok(button);await button.events.click();},
        visibility:value=>{document.visibilityState=value;events.visibilitychange();},
        pagehide:()=>windowEvents.pagehide(),pageshow:()=>windowEvents.pageshow({persisted:true}),
        broadcast:()=>channels.at(-1).listener({data:{type:'auth-changed'}})};
}
async function settle(){for(let i=0;i<15;i++)await new Promise(setImmediate);}
test('real shell shows only current profile/list, hostile text stays inert',async()=>{
    const page=mount({user:{...person(A),displayName:'<script>never execute</script>'}});await settle();
    assert.equal(page.node('display-name').textContent,'<script>never execute</script>');
    assert.equal(page.node('conversation-list').querySelectorAll('button')[0].textContent,'A private');
    assert.equal(page.node('protected-app').hidden,false);
});
test('authenticated history-disabled mode is explicit and never calls saved-history APIs',async()=>{
    const page=mount({historyEnabled:false});await settle();
    assert.match(page.node('history-notice').textContent,/ไม่ถูกบันทึก/);assert.equal(page.node('new-chat').disabled,true);
    assert.equal(page.node('send').disabled,false);page.node('message').value='not saved';page.submit();await settle();
    assert.equal(page.streams.length,1);assert.equal(Object.hasOwn(page.streams[0].body,'conversationId'),false);
    assert.ok(page.requests.every(request=>!request.path.startsWith('/api/conversations')));
});
test('logout captures expected actor before clearing profile and sends no identity in broadcast',async()=>{
    const page=mount();await settle();page.node('message').value='private draft';await page.click('logout');await settle();
    assert.equal(page.fixture.logoutActor,A);assert.equal(page.node('messages').children.length,0);assert.equal(page.node('message').value,'');
    assert.equal(page.node('protected-app').hidden,true);assert.deepEqual(JSON.parse(JSON.stringify(page.fixture.broadcast)),{type:'auth-changed'});assert.equal(page.fixture.navigation,'/login');
});
test('late history A cannot overwrite B after hidden/visible session change',async()=>{
    const pendingList=deferred(),page=mount({pendingList});await settle();
    page.visibility('hidden');assert.equal(page.node('display-name').textContent,'');page.fixture.user=person(B);
    page.visibility('visible');await settle();pendingList.resolve({items:[conversation(C,'A secret late')],nextCursor:null});await settle();
    assert.equal(page.node('display-name').textContent,'ผู้ใช้ 2');
    assert.equal(page.node('conversation-list').querySelectorAll('button')[0].textContent,'B private');
});
test('late stream A cannot reappear after B signs in even if upstream ignores abort',async()=>{
    const pendingStream=deferred(),page=mount({pendingStream});await settle();await page.select();await settle();
    page.node('message').value='A question';page.submit();await settle();assert.equal(page.streams.length,1);
    page.visibility('hidden');assert.equal(page.streams[0].init.signal.aborted,true);page.fixture.user=person(B);page.visibility('visible');await settle();
    page.fixture.update({state:'streaming',text:'A private late'});pendingStream.resolve({state:'completed',text:'A private late'});await settle();
    assert.equal(page.node('messages').children.length,0);assert.equal(page.node('display-name').textContent,'ผู้ใช้ 2');
});
test('duplicate submit, Shift+Enter and IME cannot submit another turn',async()=>{
    const page=mount({pendingStream:deferred()});await settle();await page.select();await settle();page.node('message').value='question';
    page.key({key:'Enter',shiftKey:true});page.key({key:'Enter',isComposing:true});await settle();assert.equal(page.streams.length,0);
    page.key({key:'Enter'});page.submit();await settle();assert.equal(page.streams.length,1);assert.equal(page.streams[0].body.conversationId,C);
    page.pagehide();page.fixture.pendingStream.resolve({state:'cancelled'});await settle();
});
for(const [code,status] of [['AUTH_REQUIRED',401],['AUTH_FORBIDDEN',403],['AUTH_CSRF_INVALID',403]])test(code+' clears private view and drafts',async()=>{
    const page=mount();await settle();page.node('message').value='private draft';page.fixture.failure=Object.assign(new Error(code),{code,status});
    await page.click('refresh-history');await settle();assert.equal(page.node('protected-app').hidden,true);assert.equal(page.node('message').value,'');assert.equal(page.node('display-name').textContent,'');
});
test('older message page disables composer until latest page is loaded',async()=>{
    const msg=(id,seq,content)=>({id,conversationId:C,sequence:seq,role:'user',content,status:'completed',createdAt:date,updatedAt:date});
    const page=mount({pages:path=>path.includes('cursor=next')?{items:[msg(B,2,'latest')],nextCursor:null}:{items:[msg(A,1,'older')],nextCursor:'next'}});
    await settle();await page.select();await settle();assert.equal(page.node('send').disabled,false);
    await page.click('previous-messages');await settle();assert.equal(page.node('send').disabled,true);
    await page.click('next-messages');await settle();assert.equal(page.node('send').disabled,false);
});
test('bfcache rechecks identity and reconnects cross-tab signal without restoring private draft',async()=>{
    const page=mount();await settle();page.node('message').value='private';page.pagehide();assert.equal(page.channels[0].closed,true);
    page.fixture.user=person(B);page.pageshow();await settle();assert.equal(page.channels.length,2);assert.equal(page.node('message').value,'');
    assert.equal(page.node('display-name').textContent,'ผู้ใช้ 2');page.fixture.user=null;page.broadcast();await settle();assert.equal(page.node('protected-app').hidden,true);
});
test('idle expiry clears UI without background requests that keep session alive',async()=>{
    const page=mount();await settle();const before=page.requests.length;page.timers.at(-1).fn();await settle();
    assert.equal(page.requests.length,before);assert.equal(page.node('protected-app').hidden,true);
});
test('auto-title never splits a Unicode surrogate pair at the 80-character boundary',async()=>{
    const page=mount();await settle();page.node('message').value='x'.repeat(79)+'😀z';page.submit();await settle();
    assert.equal(page.fixture.createdTitle,'x'.repeat(79)+'😀');assert.equal(page.fixture.createdTitle.isWellFormed(),true);
});
test('over-budget question does not create a conversation or send AI request',async()=>{
    const page=mount();await settle();page.node('message').value='ก'.repeat(342);page.submit();await settle();
    assert.equal(page.fixture.createdTitle,undefined);assert.equal(page.streams.length,0);assert.match(page.node('hint').textContent,/KiB/);
});
for(const settles of [true,false])test('Stop reconciliation is bounded, read-only, and '+(settles?'confirms cancellation':'does not invent confirmation'),async()=>{
    const pendingStream=deferred(),page=mount({pendingStream});await settle();await page.select();await settle();
    page.node('message').value='stop question';page.submit();await settle();let reads=0;
    page.fixture.pages=()=>({items:[{id:D,conversationId:C,sequence:2,role:'assistant',content:'ส่วนหนึ่ง',
        status:++reads>1&&settles?'cancelled':'pending',createdAt:date,updatedAt:date}],nextCursor:null});
    await page.click('stop');pendingStream.resolve({state:'cancelled'});await settle();
    for(let i=0;i<(settles?1:3);i++){const timer=page.timers.filter(timer=>timer.ms<=600).at(-1);assert.ok(timer);timer.fn();await settle();}
    assert.equal(page.streams.length,1);assert.equal(page.requests.filter(request=>request.init.method && request.init.method!=='GET').length,0);
    assert.equal(reads,settles?2:4);assert.equal(page.node('send').disabled,!settles);
    if(settles)assert.match(page.node('messages').children[0].children[2].textContent,/หยุด/);
    else assert.match(page.node('history-notice').textContent,/ยังยืนยันสถานะ/);
});
