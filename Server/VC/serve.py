"""Isolated Spark deployment of the existing 9882 bridge and character worker."""
import json
import os
import sys
from pathlib import Path
ROOT=Path(__file__).resolve().parent
RELEASE=ROOT/"release"
os.environ["NEEEVA_RVC_PYTHON"]=sys.executable
# Faiss 1.10 ARM CPU probing imports numpy.distutils, removed with Python 3.12.
# Use the package's supported generic implementation with the unchanged index.
os.environ.setdefault("FAISS_OPT_LEVEL","generic")
sys.path.insert(0,str(RELEASE/"SeedVC"))
import seedvc_server as bridge
import torch

manifest=json.loads((RELEASE/"manifest.json").read_text())
bridge.LAST_CONVERSION=ROOT/"runtime/last_conversion"
health=bridge.health
def deployment_health():
    return {**health(),"release_sha256":manifest["release_sha256"],
            "reference_path":str(RELEASE/"refs/41041.wav"),
            "torch":torch.__version__,"cuda":torch.version.cuda,
            "cuda_available":torch.cuda.is_available(),
            "cpu_threads":torch.get_num_threads()}
for route in bridge.app.router.routes:
    if getattr(route,"path",None)=="/health":
        bridge.app.router.routes.remove(route)
        break
bridge.app.get("/health")(deployment_health)
if __name__=="__main__":
    import uvicorn
    uvicorn.run(bridge.app,host="0.0.0.0",port=9882,workers=1)
