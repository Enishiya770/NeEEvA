"""Explicit isolated Spark release; no downloads, ASR imports or model activation."""
import hashlib
import json
import os
from pathlib import Path
import sys

root = Path(__file__).resolve().parent
release = root / 'release'
manifest = json.loads((release / 'manifest.json').read_text(encoding='utf-8'))
for entry in manifest['files']:
    path = (release / entry['path']).resolve()
    if not path.is_relative_to(release.resolve()) or not path.is_file():
        raise RuntimeError('invalid_release_path:' + entry['path'])
    if hashlib.sha256(path.read_bytes()).hexdigest() != entry['sha256']:
        raise RuntimeError('release_hash_mismatch:' + entry['path'])
os.environ.setdefault('SINGING_LEARNING_DATA', str(root / 'data'))
os.environ['SINGING_LEARNING_CHECKPOINT'] = str(release / 'models/panns.pth')
os.environ['SINGING_LEARNING_LABELS'] = str(release / 'models/class_labels_indices.csv')
os.environ['SINGING_LEARNING_DEPS'] = str(release / 'deps')
os.environ.setdefault('SINGING_LEARNING_DEVICE', 'cpu')
os.environ.setdefault('SINGING_LEARNING_TORCH_THREADS', '2')
os.environ.setdefault('SINGING_LEARNING_HOST', '0.0.0.0')
os.environ.setdefault('SINGING_LEARNING_PORT', '9884')
sys.path.insert(0, str(release))
os.chdir(release)
if '--verify' in sys.argv:
    import tempfile
    import wave
    import numpy as np
    from Server.SingingLearning.backend import PannsBackend
    from Server.SingingLearning.config import Config
    with tempfile.TemporaryDirectory() as temp:
        audio = Path(temp) / 'smoke.wav'
        with wave.open(str(audio), 'wb') as wav:
            wav.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            wav.writeframes(np.zeros(32000, dtype='<i2').tobytes())
        result = PannsBackend(Config.from_env()).extract(audio)
        assert result['status'] == 'candidate' and result['source']['sample_count'] == 32000
        print(json.dumps(dict(ok=True, model_version=result['model_version'],
                              frame_count=len(result['timeline']['scores']))))
else:
    import uvicorn
    uvicorn.run('Server.SingingLearning.app:create_app', factory=True,
                host=os.environ['SINGING_LEARNING_HOST'],
                port=int(os.environ['SINGING_LEARNING_PORT']), workers=1)
