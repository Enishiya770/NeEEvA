"""Embed immutable original WAVs and true PCM min/max envelopes in offline HTML."""
import argparse
import array
import base64
import hashlib
import io
import json
from pathlib import Path
import sys
import wave


def waveform(raw, bins=2400):
    with wave.open(io.BytesIO(raw),'rb') as wav:
        if wav.getsampwidth()!=2 or wav.getcomptype()!='NONE':
            raise ValueError('annotation_generator_requires_uncompressed_PCM16')
        sr,count,channels=wav.getframerate(),wav.getnframes(),wav.getnchannels()
        samples=array.array('h',wav.readframes(count))
    if sys.byteorder!='little':samples.byteswap()
    if len(samples)!=count*channels or not count:raise ValueError('invalid_pcm_sample_count')
    bins=min(bins,count);envelope=[];edges=[]
    for i in range(bins):
        start=i*count//bins;end=(i+1)*count//bins
        mono=[sum(samples[j*channels:(j+1)*channels])/channels/32768 for j in range(start,end)]
        envelope.append([round(min(mono),6),round(max(mono),6)]);edges.append(start)
    edges.append(count)
    return {'sample_rate':sr,'sample_count':count,'channels':channels},envelope,edges


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--manifest',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--dataset-id',default='singing-subsystem-20260929-seven-original-wavs',
                   help='Independent annotation dataset identity; defaults to the original seven-WAV dataset.')
    a=p.parse_args();raw_manifest=a.manifest.read_bytes();rows=json.loads(raw_manifest.decode('utf-8-sig'))
    if not a.dataset_id.strip():p.error('--dataset-id must not be blank')
    samples=[]
    for row in rows:
        raw=Path(row['wav']).read_bytes();digest=hashlib.sha256(raw).hexdigest()
        if digest!=row['sha256']:raise ValueError('manifest_audio_sha256_mismatch:'+row['id'])
        properties,envelope,edges=waveform(raw)
        samples.append({'id':row['id'],'source':{'audio_sha256':digest,**properties},
            'wav_base64':base64.b64encode(raw).decode('ascii'),'pcm_envelope':envelope,'envelope_edges':edges})
    # Deliberately omit dialogue hints, model scores, previous crops and labels.
    data={'dataset_id':a.dataset_id,
          'source_manifest_sha256':hashlib.sha256(raw_manifest).hexdigest(),'samples':samples}
    here=Path(__file__).parent
    html=(here/'annotation_template.html').read_text(encoding='utf-8')
    html=html.replace('__ANNOTATION_SAMPLE_COUNT__',str(len(samples)))
    html=html.replace('/*__ANNOTATION_CORE__*/',(here/'annotation_core.js').read_text(encoding='utf-8'))
    html=html.replace('/*__ANNOTATION_APP__*/',(here/'annotation_app.js').read_text(encoding='utf-8'))
    html=html.replace('__ANNOTATION_DATA__',json.dumps(data,ensure_ascii=False,separators=(',',':')).replace('<','\\u003c'))
    a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(html,encoding='utf-8')
    audit={'artifact_sha256':hashlib.sha256(a.output.read_bytes()).hexdigest(),'dataset_id':data['dataset_id'],
        'source_manifest_sha256':data['source_manifest_sha256'],'sources':[{'id':s['id'],**s['source']} for s in samples],
        'embedded_original_wavs':True,'embedded_model_predictions':False,'autoplay':False,'prepopulated_human_labels':False,
        'implementation_files':[{'file':name,'sha256':hashlib.sha256((here/name).read_bytes()).hexdigest()} for name in
            ['build_annotation_tool.py','annotation_template.html','annotation_app.js','annotation_core.js','test_annotation.js']]}
    a.output.with_suffix('.manifest.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'output':str(a.output),'bytes':a.output.stat().st_size,'sources':len(samples)}))


if __name__=='__main__':main()
