'use strict';
const $ = id => document.getElementById(id);
const token = document.querySelector('meta[name="action-token"]').content;
const labels = {queued:'待处理',parsing:'解析中',submitting:'提交中',imported:'已提交',retry:'等待重试',failed:'失败',skipped:'已跳过',review:'待核对'};
let rulesDirty=false;
let page=1, filter='', total=0, paused=false, busy=false, refreshing=false, inputSource='web';
const formatSize = n => n == null ? '—' : n >= 1073741824 ? (n/1073741824).toFixed(2)+' GB' : (n/1048576).toFixed(1)+' MB';
function el(tag, text, cls) {const n=document.createElement(tag); if(text!=null)n.textContent=text; if(cls)n.className=cls; return n;}
function badge(status) {return el('span',labels[status]||status,'badge '+(status==='imported'?'':status==='failed'?'error':status==='review'||status==='retry'?'warn':status==='skipped'?'neutral':'blue'));}
function toast(message) {$('toast').textContent=message;$('toast').hidden=false;setTimeout(()=>$('toast').hidden=true,4000);}
async function api(url, data) {const options=data===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json','X-Action-Token':token},body:JSON.stringify(data)};const r=await fetch(url,options);let result;try{result=await r.json();}catch{throw Error('服务响应异常，请刷新页面');}if(!r.ok)throw Error(result.error||'请求失败，请刷新页面');return result;}
async function loadTasks() {
  const d=await api(`/api/tasks?page=${page}&status=${encodeURIComponent(filter)}&q=${encodeURIComponent($('search').value)}`);total=d.total;
  const rows=$('taskRows');rows.replaceChildren();
  if(!d.items.length){const tr=el('tr'),td=el('td','暂无匹配任务。添加磁力链接后，处理结果会显示在这里。','empty');td.colSpan=5;tr.append(td);rows.append(tr);}
  for(const item of d.items){const tr=el('tr'),name=el('td'),title=el('button',item.title||item.link,'task-title');title.title=item.title||item.link;title.onclick=()=>showDetail(item.id);name.append(title,el('div',item.selected_name||item.error|| (item.source==='legacy'?'旧版历史记录':'等待解析主视频'),'task-sub'));const status=el('td');status.append(badge(item.status));const action=el('td');if(['failed','retry','review','skipped'].includes(item.status)){const button=el('button','重试','button secondary compact');button.onclick=async()=>{const reviewed=item.status==='review';if(reviewed&&!confirm('提交结果尚不明确。请先在迅雷网页确认没有该任务，再点击确定重新提交。'))return;button.disabled=true;try{await api(`/api/tasks/${item.id}/retry`,{reviewed});toast('已重新加入队列');await refresh();}catch(e){toast(e.message);}finally{button.disabled=false;}};action.append(button);}else{const button=el('button','详情','button secondary compact');button.onclick=()=>showDetail(item.id);action.append(button);}tr.append(name,status,el('td',formatSize(item.selected_size),'nowrap'),el('td',item.created_at,'nowrap'),action);rows.append(tr);}
  $('recordCount').textContent=`共 ${total.toLocaleString()} 条记录`;$('pageNumber').textContent=`${page} / ${Math.max(1,Math.ceil(total/20))}`;$('prev').disabled=page<=1;$('next').disabled=page*20>=total;
}
async function refresh() {
  if(refreshing)return;refreshing=true;
  try{const d=await api('/api/status'),c=d.counts;paused=d.paused;const minimum=d.rules.min_video_size_mb;
    if(!rulesDirty && document.activeElement!==$('minimumInput'))$('minimumInput').value=minimum;
    $('minimumSize').textContent=minimum;$('minimumRule').textContent=`严格大于 ${minimum}MB，等于也不下载`;$('sampleRule').textContent=d.rules.skip_samples?'排除样片、预告、字幕和图片':'排除字幕、图片等非视频文件';
    $('target').textContent=d.api_url.replace(/^https?:\/\//,'');$('uptime').textContent=Math.floor(d.uptime/60)+' 分钟';
    $('pending').textContent=(c.queued||0)+(c.parsing||0)+(c.submitting||0);$('imported').textContent=(c.imported||0).toLocaleString();$('attention').textContent=(c.failed||0)+(c.review||0)+(c.retry||0);
    $('rate').textContent=d.success_rate==null?'—':d.success_rate+'%';$('rateText').textContent=`成功 ${d.day_success} / 失败 ${d.day_failed}`;
    $('healthBanner').className='health-banner'+(d.api.ok===false||paused?' warn':'');$('healthTitle').textContent=paused?'队列已暂停':d.api.ok===true?'自动导入运行正常':d.api.ok===false?'迅雷接口暂不可用':'正在连接迅雷';
    $('healthText').textContent=paused?'当前任务完成后暂停，可继续添加链接。':d.api.ok?`数据库可读写，只提交大于 ${minimum}MB 的主视频。`:'任务记录保存在本地，解析失败会等待重试。';
    $('apiBadge').textContent=d.api.ok===true?'API 正常':d.api.ok===false?'API 离线':'API 检查中';$('apiBadge').className='badge'+(d.api.ok===false?' warn':'');$('dbBadge').textContent='SQLite 正常';
    $('apiStatus').textContent=d.api.ok?'已认证 · '+d.api.latency_ms+'ms':d.api.message;$('downloadPath').textContent=d.download_path||'尚未取得';$('clipboard').checked=d.clipboard;$('clipboardError').textContent=d.clipboard_error;
    $('duplicateCount').textContent=d.duplicates.toLocaleString()+' 次';$('pause').textContent=paused?'继续处理队列':'暂停队列';$('databasePath').textContent=d.database;$('legacyInfo').textContent=d.legacy_records?`已完整存档 ${d.legacy_records.toLocaleString()} 条旧版原始记录。`:'提交记录和统计均保存在这份本地数据库中。';
    $('filters').replaceChildren();for(const reason of new Set(['非视频',`不大于 ${minimum}MB`,'非主视频','样片或预告',...Object.keys(d.filters)])){const row=el('div',null,'filter');row.append(el('span',reason),el('b',d.filters[reason]||0));$('filters').append(row);}
    $('events').replaceChildren();for(const event of d.recent_events){const row=el('div',null,'event'),dot=el('i',null,event.result==='imported'?'':'warn'),body=el('div');body.append(el('strong',event.title||event.selected_name||'旧版记录'),el('small',(labels[event.result]||event.result)+' · '+(event.message||'')));row.append(dot,body,el('span',event.created_at));$('events').append(row);}if(!d.recent_events.length)$('events').append(el('p','处理动态将在这里显示','empty'));
    await loadTasks();
  }catch(e){$('healthBanner').className='health-banner warn';$('healthTitle').textContent='本地服务连接中断';$('healthText').textContent='请确认程序仍在运行。页面会自动重新连接。';}
  finally{refreshing=false;}
}
async function showDetail(id){try{const d=await api('/api/tasks/'+id);$('detailTitle').textContent=d.title;$('detailStatus').textContent=(labels[d.status]||d.status)+' · '+(d.error||'')+(d.remote_id?' · 迅雷任务 '+d.remote_id:'');$('detailLink').textContent=d.link;$('detailFiles').replaceChildren();for(const f of d.files){const row=el('tr');row.append(el('td',f.name),el('td',formatSize(f.size),'nowrap'),el('td',f.reason));$('detailFiles').append(row);}if(!d.files.length){const row=el('tr'),cell=el('td','暂无文件清单；旧版记录保留原始提交结果。','empty');cell.colSpan=3;row.append(cell);$('detailFiles').append(row);}$('detail').showModal();}catch(e){toast(e.message);}}
$('closeDetail').onclick=()=>$('detail').close();
$('submit').onclick=async()=>{if(busy)return;busy=true;$('submit').disabled=true;$('submitFeedback').textContent='正在加入队列…';try{const result=await api('/api/tasks',{text:$('links').value,source:inputSource});$('submitFeedback').className='feedback';$('submitFeedback').textContent=`已加入 ${result.added} 条 · 重复 ${result.duplicates} 条 · 无效 ${result.invalid} 条`;$('links').value='';inputSource='web';page=1;await refresh();}catch(e){$('submitFeedback').className='feedback error';$('submitFeedback').textContent=e.message;}finally{busy=false;$('submit').disabled=false;}};
$('chooseFile').onclick=()=>$('textFile').click();$('textFile').onchange=async()=>{const f=$('textFile').files[0];if(!f)return;if(f.size>2*1024*1024){toast('文件不能超过 2MB');return;}const bytes=await f.arrayBuffer();let text=new TextDecoder('utf-8').decode(bytes);if(text.includes('\ufffd'))text=new TextDecoder('gb18030').decode(bytes);$('links').value=text;inputSource='file';$('inputHint').textContent=f.name;$('textFile').value='';};$('links').oninput=()=>{inputSource='web';$('inputHint').textContent='最多 500 条 / 2MB';};
$('clipboard').onchange=async()=>{try{await api('/api/clipboard',{enabled:$('clipboard').checked});toast($('clipboard').checked?'已开启剪贴板监听':'已关闭剪贴板监听');}catch(e){toast(e.message);}await refresh();};
$('pause').onclick=async()=>{try{await api('/api/pause',{paused:!paused});await refresh();}catch(e){toast(e.message);}};
document.querySelectorAll('[data-status]').forEach(button=>button.onclick=()=>{filter=button.dataset.status;page=1;document.querySelectorAll('[data-status]').forEach(b=>b.classList.toggle('active',b===button));loadTasks().catch(e=>toast(e.message));});
let timer;$('search').oninput=()=>{clearTimeout(timer);timer=setTimeout(()=>{page=1;loadTasks().catch(e=>toast(e.message));},300);};$('prev').onclick=()=>{page=Math.max(1,page-1);loadTasks().catch(e=>toast(e.message));};$('next').onclick=()=>{page++;loadTasks().catch(e=>toast(e.message));};
$('minimumInput').oninput=()=>{rulesDirty=true;};
$('sizeForm').onsubmit=async event=>{event.preventDefault();const minimum=Number($('minimumInput').value);if(!Number.isFinite(minimum)||minimum<=0||minimum>1048576){$('sizeFeedback').textContent='请输入大于 0 且不超过 1048576 的数值。';return;}$('saveSize').disabled=true;$('minimumInput').disabled=true;try{await api('/api/rules',{min_video_size_mb:minimum});rulesDirty=false;$('sizeFeedback').textContent=`已保存：只选择大于 ${minimum}MB 的主视频。`;await refresh();}catch(e){$('sizeFeedback').textContent=e.message;}finally{$('saveSize').disabled=false;$('minimumInput').disabled=false;}};
refresh();setInterval(refresh,3000);
