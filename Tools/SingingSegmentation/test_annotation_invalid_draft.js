/* Silent synthetic-only UI regression: never creates labels for user audio. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const {chromium}=require('C:/Users/33830/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const C=require('./annotation_core.js');
async function main(){
  const [artifact,output,mode]=process.argv.slice(2);fs.mkdirSync(output,{recursive:true});
  const samples=320000,sr=16000,wav=Buffer.alloc(44+samples*2);wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(sr,24);wav.writeUInt32LE(sr*2,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(samples*2,40);
  const dataset={dataset_id:'synthetic-invalid-draft-test-only',source_manifest_sha256:'d'.repeat(64),samples:[{id:'synthetic-only',source:{audio_sha256:crypto.createHash('sha256').update(wav).digest('hex'),sample_rate:sr,sample_count:samples,channels:1},wav_base64:wav.toString('base64'),pcm_envelope:[[0,0]],envelope_edges:[0,samples]}]};
  const original=fs.readFileSync(artifact,'utf8'),html=original.replace(/(<script id="dataset" type="application\/json">)[\s\S]*?(<\/script>)/,'$1'+JSON.stringify(dataset)+'$2');
  const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true,args:['--mute-audio','--disable-background-networking']});
  const observations=[],errors=[];let report;
  try{
    const context=await browser.newContext({acceptDownloads:true});await context.route(/^https?:\/\//,r=>r.abort());
    await context.addInitScript(()=>{window.__playAttempts=0;HTMLMediaElement.prototype.play=function(){window.__playAttempts++;throw new Error('forbidden');};});
    const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));await page.setContent(html);
    async function field(id,value){await page.locator('#'+id).fill(String(value));await page.locator('#'+id).blur();}
    async function download(){const pending=page.waitForEvent('download');await page.locator('#export').click();const d=await pending;const p=await d.path();return JSON.parse(fs.readFileSync(p,'utf8'));}
    await field('startSeconds',3);await field('endSeconds',14);
    assert.equal(await page.locator('#start').inputValue(),'48000');assert.equal(await page.locator('#end').inputValue(),'224000');
    observations.push('3–14 seconds maps to 48000–224000 samples, without a factor-of-100 error.');
    await field('startSeconds',15);assert.match(await page.locator('#status').textContent(),/起点必须早于终点/);
    await field('startMargin',50);await field('endMargin',50);await page.locator('#saveRange').click();
    const afterInvalid=await download();
    if(mode==='expect-bug'){
      assert.equal(afterInvalid.recordings[0].annotations.length,1);
      assert.equal(afterInvalid.recordings[0].annotations[0].start_sample,48000);assert.equal(afterInvalid.recordings[0].annotations[0].end_sample,224000);
      observations.push('BUG REPRODUCED: startSeconds=15 was rejected against end=14, but Save silently persisted previous selection 3–14.');
    }else{
      assert.equal(afterInvalid.recordings[0].annotations.length,0);observations.push('Invalid reversed seconds cannot save the previous range.');
      // Complete the normal two-field edit; do not clear edit context/drafts.
      await field('endSeconds',16);await page.locator('#saveRange').click();
      const valid=await download();assert.equal(valid.recordings[0].annotations.length,1);assert.equal(valid.recordings[0].annotations[0].start_sample,240000);assert.equal(valid.recordings[0].annotations[0].end_sample,256000);
      observations.push('After the intermediate invalid pair, completing end=16 saves intended 15–16.');
      await page.locator('#ranges button').getByText('编辑',{exact:true}).click();
      await field('startSeconds',17);await field('endSeconds',18);await page.locator('#saveRange').click();
      const edited=await download();assert.equal(edited.recordings[0].annotations.length,1);assert.equal(edited.recordings[0].annotations[0].start_sample,272000);assert.equal(edited.recordings[0].annotations[0].end_sample,288000);
      observations.push('Editing an existing row across a temporary invalid pair preserves editIndex and replaces that row.');
      await page.locator('#ranges button').getByText('编辑',{exact:true}).click();await field('startSeconds','');await page.locator('#saveRange').click();const blank=await download();assert.deepEqual(blank.recordings[0].annotations,edited.recordings[0].annotations);
      observations.push('Empty seconds never coerce to zero or overwrite a saved row.');
      await field('startSeconds',-1);await page.locator('#saveRange').click();const negative=await download();assert.deepEqual(negative.recordings[0].annotations,edited.recordings[0].annotations);
      await field('startSeconds',17);await field('endSeconds',21);await page.locator('#saveRange').click();const beyond=await download();assert.deepEqual(beyond.recordings[0].annotations,edited.recordings[0].annotations);
      observations.push('Negative/out-of-source seconds reject instead of clamping or saving stale data.');
      await field('endSeconds',18);await field('start',272001);await field('end',288001);await page.locator('#saveRange').click();const exact=await download();assert.equal(exact.recordings[0].annotations.length,1);assert.equal(exact.recordings[0].annotations[0].start_sample,272001);assert.equal(exact.recordings[0].annotations[0].end_sample,288001);
      observations.push('Legal sample editing remains authoritative and keeps single-row replacement.');
    }
    assert.deepEqual(errors,[]);assert.equal(await page.evaluate(()=>window.__playAttempts),0);
    report={passed:true,mode,observations,artifact_sha256:crypto.createHash('sha256').update(original).digest('hex'),test_dataset_id:dataset.dataset_id,original_user_recordings_used:false,original_user_annotations_modified:false,media_play_attempts:0,page_errors:errors};
  }catch(e){report={passed:false,mode,error:String(e.stack||e),observations};process.exitCode=1;}
  finally{await browser.close();}
  fs.writeFileSync(path.join(output,'invalid-draft-results.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
