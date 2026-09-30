/* Pure annotation coordinates/schema. No audio, model or network operations. */
(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.AnnotationCore=api;})(globalThis,function(){
  'use strict';
  const SCHEMA='neeeva.speech-singing-human.v1';
  const LABELS=['speech','singing','uncertain'];
  function integer(value,name){if(!Number.isSafeInteger(value))throw new Error(name+' 必须为整数样本');return value;}
  function source(value){
    if(!value||!(/^[a-f0-9]{64}$/).test(value.audio_sha256))throw new Error('无效音频 SHA-256');
    if(integer(value.sample_rate,'sample_rate')<=0||integer(value.sample_count,'sample_count')<=0)throw new Error('无效音频范围');
    return {audio_sha256:value.audio_sha256,sample_rate:value.sample_rate,sample_count:value.sample_count};
  }
  function secondsToSample(seconds,s){source(s);const x=Number(seconds);if(!Number.isFinite(x))throw new Error('无效秒数');return Math.max(0,Math.min(s.sample_count,Math.round(x*s.sample_rate)));}
  function pixelToSample(pixel,width,viewStart,viewEnd){
    if(![pixel,width,viewStart,viewEnd].every(Number.isFinite)||width<=0||viewEnd<=viewStart)throw new Error('无效视图坐标');
    return Math.round(viewStart+Math.max(0,Math.min(1,pixel/width))*(viewEnd-viewStart));
  }
  function validateRange(a,s){
    source(s);integer(a.start_sample,'start_sample');integer(a.end_sample,'end_sample');
    if(a.start_sample<0||a.start_sample>=a.end_sample||a.end_sample>s.sample_count)throw new Error('选区必须位于原音频内且起点早于终点');
    if(!LABELS.includes(a.label))throw new Error('无效标签');
    const u=a.boundary_uncertainty;if(!u)throw new Error('请明确填写两端边界误差带');
    for(const key of ['start_min_sample','start_max_sample','end_min_sample','end_max_sample'])integer(u[key],key);
    if(u.start_min_sample<0||u.start_min_sample>a.start_sample||u.start_max_sample<a.start_sample||u.start_max_sample>s.sample_count||u.end_min_sample<0||u.end_min_sample>a.end_sample||u.end_max_sample<a.end_sample||u.end_max_sample>s.sample_count)throw new Error('误差带必须包含名义边界并位于原音频内');
    if(u.kind!=='human_tolerance_band_not_statistical_ci')throw new Error('无效误差带来源');
    return {label:a.label,start_sample:a.start_sample,end_sample:a.end_sample,
      boundary_uncertainty:{...u},note:String(a.note||'').slice(0,2000)};
  }
  function makeRange(label,start,end,startMarginMs,endMarginMs,note,s){
    if(startMarginMs===''||endMarginMs===''||!Number.isFinite(Number(startMarginMs))||!Number.isFinite(Number(endMarginMs))||Number(startMarginMs)<0||Number(endMarginMs)<0)throw new Error('请显式填写两个误差带；0 表示你确认精确到样本');
    const a=Math.ceil(Number(startMarginMs)*s.sample_rate/1000),b=Math.ceil(Number(endMarginMs)*s.sample_rate/1000);
    return validateRange({label,start_sample:Number(start),end_sample:Number(end),note,
      boundary_uncertainty:{kind:'human_tolerance_band_not_statistical_ci',start_min_sample:Math.max(0,Number(start)-a),start_max_sample:Math.min(s.sample_count,Number(start)+a),end_min_sample:Math.max(0,Number(end)-b),end_max_sample:Math.min(s.sample_count,Number(end)+b)}},s);
  }
  function validateRanges(ranges,s){
    if(!Array.isArray(ranges))throw new Error('区间列表无效');
    const clean=ranges.map(a=>validateRange(a,s)).sort((a,b)=>a.start_sample-b.start_sample);
    let last=0;for(const a of clean){if(a.start_sample<last)throw new Error('名义标签区间不能重叠；模糊部分请使用 uncertain');last=a.end_sample;}
    return clean;
  }
  function gaps(ranges,s){let at=0;const missing=[];for(const a of validateRanges(ranges,s)){if(a.start_sample>at)missing.push([at,a.start_sample]);at=a.end_sample;}if(at<s.sample_count)missing.push([at,s.sample_count]);return missing;}
  function exportDocument(dataset,states,annotator,now){
    const recordings=dataset.samples.map(sample=>{
      const s=source(sample.source),state=states[sample.id]||{ranges:[],reviewed:false},ranges=validateRanges(state.ranges,s),missing=gaps(ranges,s);
      if(state.reviewed&&missing.length)throw new Error(sample.id+' 仍有未覆盖区间，不能标成完成');
      return {recording_id:sample.id,source:s,coordinate_origin_sample:0,sample_range_convention:'half-open',
        review_status:state.reviewed?'reviewed':ranges.length?'in_progress':'unreviewed',
        unlabelled_sample_ranges:missing,annotations:ranges};
    });
    return {annotation_schema:SCHEMA,tool_version:'offline-manual-v1',dataset_id:dataset.dataset_id,
      source_manifest_sha256:dataset.source_manifest_sha256,exported_at:now||new Date().toISOString(),
      provenance:{kind:'human_manual',annotator:String(annotator||'').slice(0,200),model_candidates_shown:false,
        playback_is_user_initiated:true,labels_are_not_model_predictions:true},
      complete:recordings.every(r=>r.review_status==='reviewed'),recordings};
  }
  function importDocument(doc,dataset){
    if(!doc||doc.annotation_schema!==SCHEMA||doc.dataset_id!==dataset.dataset_id||doc.source_manifest_sha256!==dataset.source_manifest_sha256||doc.provenance?.kind!=='human_manual'||doc.provenance.model_candidates_shown!==false||doc.provenance.labels_are_not_model_predictions!==true)throw new Error('标注格式、人工来源或数据集身份不一致');
    if(!Array.isArray(doc.recordings)||doc.recordings.length!==dataset.samples.length)throw new Error('录音列表不完整');
    const result={},seen=new Set();
    for(const r of doc.recordings){
      const sample=dataset.samples.find(s=>s.id===r.recording_id);if(!sample||seen.has(r.recording_id))throw new Error('录音身份不一致或重复');seen.add(r.recording_id);
      const s=source(r.source);for(const k of ['audio_sha256','sample_rate','sample_count'])if(s[k]!==sample.source[k])throw new Error('录音 hash / 采样坐标不一致');
      if(r.coordinate_origin_sample!==0||r.sample_range_convention!=='half-open')throw new Error('不支持此坐标约定');
      if(!['reviewed','in_progress','unreviewed'].includes(r.review_status))throw new Error('审核状态无效');
      const ranges=validateRanges(r.annotations,s),missing=gaps(ranges,s),reviewed=r.review_status==='reviewed';
      if(reviewed&&missing.length)throw new Error('完成状态与实际覆盖不一致');
      result[sample.id]={ranges,reviewed};
    }
    return result;
  }
  function previewWav(bytes,s,start,end){
    source(s);integer(start,'start');integer(end,'end');if(start<0||end<=start||end>s.sample_count)throw new Error('预览样本范围无效');
    const v=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),four=at=>String.fromCharCode(...bytes.slice(at,at+4));
    if(bytes.length<44||four(0)!=='RIFF'||four(8)!=='WAVE')throw new Error('预览要求原始 PCM WAV');
    let fmt=null,data=null;for(let at=12;at+8<=bytes.length;){const size=v.getUint32(at+4,true),next=at+8+size;if(next>bytes.length)throw new Error('WAV 块长度错误');if(four(at)==='fmt '){if(size<16)throw new Error('WAV 格式块错误');fmt={kind:v.getUint16(at+8,true),channels:v.getUint16(at+10,true),rate:v.getUint32(at+12,true),align:v.getUint16(at+20,true),bits:v.getUint16(at+22,true)};}if(four(at)==='data')data={start:at+8,size};at=next+(size%2);}
    if(!fmt||!data||fmt.kind!==1||fmt.bits!==16||fmt.channels<1||fmt.align!==fmt.channels*2||fmt.rate!==s.sample_rate||data.size!==s.sample_count*fmt.align)throw new Error('预览 WAV 与原样本坐标不一致');
    const size=(end-start)*fmt.align,out=new Uint8Array(44+size),d=new DataView(out.buffer),tag=(at,text)=>{for(let i=0;i<text.length;i++)out[at+i]=text.charCodeAt(i);};
    tag(0,'RIFF');d.setUint32(4,36+size,true);tag(8,'WAVE');tag(12,'fmt ');d.setUint32(16,16,true);d.setUint16(20,1,true);d.setUint16(22,fmt.channels,true);d.setUint32(24,fmt.rate,true);d.setUint32(28,fmt.rate*fmt.align,true);d.setUint16(32,fmt.align,true);d.setUint16(34,16,true);tag(36,'data');d.setUint32(40,size,true);out.set(bytes.subarray(data.start+start*fmt.align,data.start+end*fmt.align),44);return out;
  }
  return {SCHEMA,LABELS,source,secondsToSample,pixelToSample,makeRange,validateRange,validateRanges,gaps,exportDocument,importDocument,previewWav};
});
