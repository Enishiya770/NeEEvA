"""Request-local wall-clock stages; excludes client/network time and model semantics."""
import time


class AsrTiming:
    PHASES = ("decode", "vad", "quick_pitch", "speaker", "recognizer", "full_pitch",
              "recovery", "segment_asr", "boundary_asr", "tail_asr",
              "song_recall", "dump")

    def __init__(self, clock=None):
        self.clock = clock or time.perf_counter
        self.started = self.cursor = self.clock()
        self.values = {name: 0.0 for name in self.PHASES}
        self.finished = None

    def mark(self, phase):
        now = self.clock()
        self.values[phase] += max(0.0, now - self.cursor)
        self.cursor = now

    def finish(self):
        if self.finished is None:
            total = max(0.0, self.clock() - self.started)
            self.finished = {key: round(value, 4) for key, value in self.values.items()}
            self.finished["other"] = round(max(0.0, total - sum(self.values.values())), 4)
            self.finished["total"] = round(total, 4)
        return dict(self.finished)
