"""Bounded ASR windows with a replaceable current hypothesis, never final text."""


def next_window_start(total_ms, retained_ms, previous_start_ms, previous_end_ms,
                      limit_ms=6000, overlap_ms=1500):
    """Keep an anchor while the window grows; roll only at its size limit.

    Inference remains bounded even when audio arrives in a burst. The assembler
    uses the returned start as its revision identity, not the packet number.
    """
    earliest = max(0, total_ms - retained_ms)
    start = max(earliest, previous_start_ms)
    if total_ms - start > limit_ms:
        start = max(earliest, total_ms - limit_ms, previous_end_ms - overlap_ms)
    return min(total_ms, max(0, start))


class WindowTranscript:
    def __init__(self, merge):
        self.merge = merge
        self.start = None
        self.prefix = ""
        self.text = ""

    def update(self, start_ms, window_text):
        # A changed hypothesis within the same audio window replaces its suffix.
        # Previously the whole cumulative result was committed on EVERY update.
        if self.start is not None and start_ms < self.start:
            return self.text
        if self.start is not None and start_ms != self.start:
            self.prefix = self.text
        self.start = start_ms
        self.text = self.merge(self.prefix, window_text)
        return self.text
