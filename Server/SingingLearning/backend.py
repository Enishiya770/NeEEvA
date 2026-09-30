"""Lazy frozen PANNs adapter. No model download, network or ASR module import."""
import hashlib
import json
from pathlib import Path
import sys


def file_sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


class PannsBackend:
    def __init__(self, config):
        self.config, self.provider, self._identity = config, None, None

    def identity(self):
        if self._identity is None:
            c = self.config
            if not all((c.checkpoint, c.labels, c.dependencies)):
                raise RuntimeError('feature_backend_not_configured')
            data = dict(checkpoint=file_sha(c.checkpoint), labels=file_sha(c.labels),
                        code=file_sha(Path(c.dependencies) / 'panns_inference' / 'models.py'),
                        provider=file_sha(Path(__file__).resolve().parents[1] / 'SenseVoice' / 'singing_segmentation.py'),
                        device=c.device, schema='panns-native-cells-v1')
            self._identity = hashlib.sha256(json.dumps(data, sort_keys=True).encode()).hexdigest()
        return self._identity

    def extract(self, path):
        self.identity()
        if self.provider is None:
            if self.config.dependencies not in sys.path:
                sys.path.insert(0, self.config.dependencies)
            import torch
            torch.set_num_threads(self.config.torch_threads)
            from Server.SenseVoice.singing_segmentation import PannsFramewiseProvider
            self.provider = PannsFramewiseProvider(self.config.checkpoint, self.config.labels,
                                                  self.config.dependencies, self.config.device)
        result, _ = self.provider.analyze(path)
        if result.get('status') != 'candidate':
            raise RuntimeError('feature_backend_did_not_complete')
        return result
