/* Isolated headless acceptance of our generated HTML. Never invokes playback
   or saves labels against real recordings. Requires an existing browser. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const {pathToFileURL}=require('node:url');
async function main(){
  const [artifact,output,playwrightPackage,browserExe]=process.argv.slice(2);
  if(!browserExe)throw new Error('Usage: node test_annotation_browser.js artifact output playwright_package existing_browser_exe');
  const {chromium}=require(path.resolve(playwrightPackage));fs.mkdirSync(output,{recursive:true});
  const checks=[],pageErrors=[],externalRequests=[];
  const browser=await chromium.launch({executablePath:browserExe,headless:true,
    args:['--mute-audio','--disable-background-networking','--disable-component-update','--disable-default-apps','--disable-sync','--metrics-recording-only','--no-first-run']});
  let report;
  try{
    const context=await browser.newContext({viewport:{width:1400,height:1050},acceptDownloads:true});
    await context.route(/^https?:\/\//,route=>{externalRequests.push(route.request().url());return route.abort();});
    await context.addInitScript(()=>{
      window.__mediaPlayAttempts=0;
      HTMLMediaElement.prototype.play=function(){window.__mediaPlayAttempts++;return Promise.reject(new Error('Playback forbidden during silent artifact acceptance'));};
    });
    const page=await context.newPage();page.on('pageerror',e=>pageErrors.push(e.message));
    await page.goto(pathToFileURL(path.resolve(artifact)).href,{waitUntil:'load'});
    await page.getByRole('heading',{name:'c9 · 原始录音'}).waitFor();
    assert.equal(await page.locator('#samples button').count(),7);
    assert.equal(await page.locator('#reviewState').textContent(),'未标注');
    assert.equal(await page.locator('#ranges .empty').count(),1);
    assert.equal(await page.locator('#playSelection').isDisabled(),true);
    assert.equal(await page.evaluate(()=>window.__mediaPlayAttempts),0);
    assert.equal(await page.locator('audio').evaluate(a=>a.paused),true);
    assert.equal(await page.locator('audio[autoplay]').count(),0);
    const canvas=await page.locator('#wave').evaluate(c=>({width:c.width,height:c.height,
      coloredPixels:Array.from(c.getContext('2d').getImageData(0,0,c.width,c.height).data).filter((_,i,a)=>i%4===3&&a[i]>0).length}));
    assert(canvas.width>700&&canvas.height>150&&canvas.coloredPixels>1000);
    checks.push('Seven samples and real waveform render; initial annotations empty; no autoplay.');
    await page.screenshot({path:path.join(output,'ui-initial.png'),fullPage:true});

    await page.locator('#saveRange').click();
    assert.match(await page.locator('#status').textContent(),/请先选择音频区间/);
    await page.locator('#selectWhole').click();
    assert.equal(await page.locator('#start').inputValue(),'0');
    assert.equal(await page.locator('#end').inputValue(),'305120');
    assert.equal(Number(await page.locator('#endSeconds').inputValue()),19.07);
    await page.locator('#saveRange').click();
    assert.match(await page.locator('#status').textContent(),/请显式填写两个误差带/);
    assert.equal(await page.locator('#ranges .empty').count(),1);
    checks.push('Empty selection and missing explicit boundary uncertainty reject saving.');

    await page.locator('#start').fill('123');await page.locator('#start').blur();
    await page.locator('#end').fill('45001');await page.locator('#end').blur();
    assert.equal(Number(await page.locator('#startSeconds').inputValue()),123/16000);
    assert.equal(Number(await page.locator('#endSeconds').inputValue()),45001/16000);
    await page.locator('#zoom').click();
    assert.match(await page.locator('#viewMeta').textContent(),/0\.0077–2\.8126/);
    await page.locator('#resetZoom').click();
    const bounds=await page.locator('#wave').boundingBox();
    await page.mouse.move(bounds.x+16+(bounds.width-32)*.25,bounds.y+80);
    await page.mouse.down();await page.mouse.move(bounds.x+16+(bounds.width-32)*.75,bounds.y+80,{steps:4});await page.mouse.up();
    // Browser pointer coordinates may quantize below one CSS pixel; source field
    // remains an integer and follows the exact documented pixel mapping.
    const start=Number(await page.locator('#start').inputValue()),end=Number(await page.locator('#end').inputValue());
    assert(Number.isInteger(start)&&Number.isInteger(end));
    const tolerance=Math.ceil(305120/(bounds.width-32));
    assert(Math.abs(start-76280)<=tolerance&&Math.abs(end-228840)<=tolerance);
    checks.push('Exact sample inputs round-trip to seconds; zoom and pointer selection map to source samples.');
    await page.locator('#markReviewed').click();
    assert.match(await page.locator('#status').textContent(),/仍有未标注区间/);
    await page.locator('#cancelEdit').click();
    const downloading=page.waitForEvent('download');await page.locator('#export').click();
    const download=await downloading;const exportPath=path.join(output,'ui-export-unreviewed.json');await download.saveAs(exportPath);
    const exported=JSON.parse(fs.readFileSync(exportPath,'utf8'));
    assert.equal(exported.complete,false);assert.equal(exported.recordings.length,7);
    for(const r of exported.recordings){assert.equal(r.review_status,'unreviewed');assert.deepEqual(r.annotations,[]);assert.deepEqual(r.unlabelled_sample_ranges,[[0,r.source.sample_count]]);assert(/^[a-f0-9]{64}$/.test(r.source.audio_sha256));}
    checks.push('Actual browser download exports seven source-bound unreviewed records and zero fabricated labels.');
    await page.locator('#importFile').setInputFiles(exportPath);
    await page.waitForFunction(()=>document.getElementById('status').textContent.includes('已导入并核对全部音频身份'));
    assert.equal(await page.locator('#ranges .empty').count(),1);
    await page.locator('#samples button').nth(1).click();
    assert.equal(await page.locator('#recordingTitle').textContent(),'c15 · 原始录音');
    assert.match(await page.locator('#sourceMeta').textContent(),/250880 samples/);
    assert.equal(await page.locator('#ranges .empty').count(),1);
    assert.equal(await page.evaluate(()=>window.__mediaPlayAttempts),0);assert.equal(await page.locator('audio').evaluate(a=>a.paused),true);
    checks.push('Empty export imports without changing labels; changing recordings uses the matching source metadata.');
    await page.screenshot({path:path.join(output,'ui-after-empty-roundtrip.png'),fullPage:true});
    assert.deepEqual(pageErrors,[]);assert.deepEqual(externalRequests,[]);
    report={passed:true,checks:checks.length,details:checks,browser_version:browser.version(),browser_executable:browserExe,
      artifact_sha256:crypto.createHash('sha256').update(fs.readFileSync(artifact)).digest('hex'),
      test_script_sha256:crypto.createHash('sha256').update(fs.readFileSync(__filename)).digest('hex'),
      viewport:{width:1400,height:1050},page_errors:pageErrors,external_requests:externalRequests,
      media_play_attempts:await page.evaluate(()=>window.__mediaPlayAttempts),human_annotations_created:0,
      scope:'Silent isolated headless rendering and form/download acceptance. Playback is blocked and not tested; no human acoustic labels or listening validation.'};
  }catch(error){report={passed:false,error:String(error.stack||error),checks, page_errors:pageErrors,external_requests:externalRequests};process.exitCode=1;}
  finally{await browser.close();}
  fs.writeFileSync(path.join(output,'browser-test-results.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
