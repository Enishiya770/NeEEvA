"""Small bootstrap used to run the upstream Seed-VC CLI with local dependencies."""

from __future__ import annotations

import runpy
import sys
import types
from pathlib import Path


ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "seed-vc"
LOCAL_PACKAGES = ROOT / "python_packages"

sys.path.insert(0, str(LOCAL_PACKAGES))
sys.path.insert(0, str(VENDOR))
sys.path.insert(0, str(ROOT))

# Seed-VC only needs dac.nn.quantize.  The package's top-level __init__ imports the
# much larger training-only audiotools stack, so expose namespace parents without
# executing those optional imports.
dac_root = LOCAL_PACKAGES / "dac"
if dac_root.is_dir():
    dac_module = types.ModuleType("dac")
    dac_module.__path__ = [str(dac_root)]
    dac_nn_module = types.ModuleType("dac.nn")
    dac_nn_module.__path__ = [str(dac_root / "nn")]
    sys.modules["dac"] = dac_module
    sys.modules["dac.nn"] = dac_nn_module

import yaml

from model_assets import MODEL_ROOT, ensure_model_assets


assets = ensure_model_assets()

# Keep the official architecture config, changing only repository identifiers to
# local directories.  This avoids fragile hub metadata calls for Xet-backed files.
with assets["seed_config"].open("r", encoding="utf-8") as source:
    runtime_config = yaml.safe_load(source)
runtime_config["model_params"]["vocoder"]["name"] = str((MODEL_ROOT / "bigvgan").resolve())
runtime_config["model_params"]["speech_tokenizer"]["name"] = str(
    (MODEL_ROOT / "whisper-small").resolve()
)
runtime_config_path = MODEL_ROOT / "runtime_seedvc_config.yml"
with runtime_config_path.open("w", encoding="utf-8") as output:
    yaml.safe_dump(runtime_config, output, allow_unicode=True, sort_keys=False)

# inference.py imports this function directly, so patch the module before run_path.
import hf_utils


_original_hf_loader = hf_utils.load_custom_model_from_hf


def _local_hf_loader(repo_id: str, model_filename: str, config_filename: str | None = None):
    pair = (repo_id.lower(), model_filename.lower())
    if pair == ("lj1995/voiceconversionwebui", "rmvpe.pt"):
        return str(assets["rmvpe"])
    if pair == ("funasr/campplus", "campplus_cn_common.bin"):
        return str(assets["campplus"])
    return _original_hf_loader(repo_id, model_filename, config_filename)


hf_utils.load_custom_model_from_hf = _local_hf_loader

if "--checkpoint" not in sys.argv:
    sys.argv.extend(["--checkpoint", str(assets["seed_checkpoint"])])
if "--config" not in sys.argv:
    sys.argv.extend(["--config", str(runtime_config_path)])

runpy.run_path(str(VENDOR / "inference.py"), run_name="__main__")
