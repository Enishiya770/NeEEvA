"""Export a new dataset using the verified full-CUDA official BF16 teacher.

Requires pinned local model caches, two original cached verification controls,
and at least 18 GiB free GPU memory. Never starts or stops other services.
"""
from .diagnose_teacher_offload import run


if __name__ == "__main__":
    run(generic=True)
