from dataclasses import dataclass
from pathlib import Path
import os


@dataclass(frozen=True)
class Config:
    data_dir: Path
    checkpoint: str = ''
    labels: str = ''
    dependencies: str = ''
    device: str = 'cpu'
    torch_threads: int = 2
    queue_limit: int = 16
    max_upload_bytes: int = 24 * 1024 * 1024
    max_duration_seconds: float = 120.0
    max_recordings: int = 1000
    auto_train_debounce_seconds: float = 2.0
    # Optional teaching features only; never authorizes live singing playback.
    pitch_stream_max_active: int = 8
    pitch_stream_ttl_seconds: float = 600.0
    pitch_stream_max_chunk_samples: int = 16000
    pitch_stream_max_duration_seconds: float = 120.0
    sequence_boundary_enabled: bool = True
    local_boundary_enabled: bool = True
    category_evidence_enabled: bool = True

    @classmethod
    def from_env(cls):
        return cls(Path(os.environ.get('SINGING_LEARNING_DATA', 'work/singing-learning-data')),
                   os.environ.get('SINGING_LEARNING_CHECKPOINT', ''),
                   os.environ.get('SINGING_LEARNING_LABELS', ''),
                   os.environ.get('SINGING_LEARNING_DEPS', ''),
                   os.environ.get('SINGING_LEARNING_DEVICE', 'cpu'),
                   max(1, min(4, int(os.environ.get('SINGING_LEARNING_TORCH_THREADS', '2')))),
                   sequence_boundary_enabled=os.environ.get('SINGING_LEARNING_SEQUENCE', '1') == '1',
                   local_boundary_enabled=os.environ.get('SINGING_LEARNING_LOCAL_BOUNDARY', '1') == '1',
                   category_evidence_enabled=os.environ.get('SINGING_LEARNING_CATEGORY_EVIDENCE', '1') == '1')
