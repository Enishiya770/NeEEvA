"""Bounded exact-window ASR reuse. Caller owns the model inference lock.

No approximate matching, audio truncation, transcript merging or pitch changes.
Keys include the model generation, requested language, array format and all bytes.
"""
import copy
import hashlib
import time
from collections import OrderedDict


class AsrWindowCache:
    def __init__(self, capacity=128, ttl=90.0, clock=time.monotonic):
        self.capacity = max(1, int(capacity))
        self.ttl = float(ttl)
        self.clock = clock
        self.entries = OrderedDict()

    def resolve(self, model, language, shape, dtype, audio_bytes, infer):
        key = (model, language, tuple(shape), str(dtype),
               hashlib.sha256(audio_bytes).digest())
        now = self.clock()
        for stale in [k for k, (at, _) in self.entries.items() if now - at >= self.ttl]:
            del self.entries[stale]
        if key in self.entries:
            _, result = self.entries[key]
            self.entries.move_to_end(key)
            return copy.deepcopy(result), True
        # Failure is never cached. Consumers cannot mutate the stored result.
        result = infer()
        self.entries[key] = (self.clock(), copy.deepcopy(result))
        while len(self.entries) > self.capacity:
            self.entries.popitem(last=False)
        return result, False
