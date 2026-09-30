ARG BASE_IMAGE=ghcr.io/anemll/dspark-vllm-gx10:0.1.1
FROM ${BASE_IMAGE}
USER root
RUN python3 -m venv --system-site-packages /opt/neeeva-learning
COPY requirements.txt /tmp/learning-requirements.txt
RUN /opt/neeeva-learning/bin/python -m pip install -r /tmp/learning-requirements.txt
ENV OMP_NUM_THREADS=2 OPENBLAS_NUM_THREADS=2 MKL_NUM_THREADS=2 PYTHONUNBUFFERED=1 MPLBACKEND=Agg
ENTRYPOINT ["/opt/neeeva-learning/bin/python"]
