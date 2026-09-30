"""Linux entry point around unchanged production ASR, using explicit local assets."""
import argparse
import os
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=9881)
    args = parser.parse_args()
    root = args.root.resolve()
    models = {"iic/SenseVoiceSmall": root / "models/sensevoice", "fsmn-vad": root / "models/vad", "cam++": root / "models/speaker"}
    for model in models.values():
        if not (model / "config.yaml").is_file():
            raise FileNotFoundError("Missing explicit offline model: " + str(model))
    profile = root / "data/speaker_profiles.json"
    if not profile.is_file():
        raise FileNotFoundError("Missing migrated speaker identity database")
    dictionary = root / "assets/open_jtalk_dic_utf_8-1.11"
    if not dictionary.is_dir():
        raise FileNotFoundError("Missing offline Japanese dictionary")
    os.environ["OPEN_JTALK_DICT_DIR"] = str(dictionary)
    os.environ["HF_HUB_OFFLINE"] = "1"
    # Keep diagnostic dumps in this ASR release, never in another service tree.
    os.chdir(root / "src")
    sys.path.insert(0, str(root / "src"))
    import sensevoice_server as server
    original_auto_model = server.AutoModel

    def offline_auto_model(**kwargs):
        requested = kwargs.get("model")
        if requested not in models:
            raise ValueError("Unmapped model requested by production ASR: " + str(requested))
        kwargs["model"] = str(models[requested])
        kwargs["check_latest"] = False
        return original_auto_model(**kwargs)

    server.AutoModel = offline_auto_model
    sys.argv = [str(root / "src/sensevoice_server.py"), "--host", args.host, "--port", str(args.port),
                "--device", "cuda:0", "--require-cuda", "--vad-device", "cpu", "--speaker-device", "cpu",
                "--pitch-device", "cpu", "--disable-torchcrepe", "--speaker-profile-path", str(profile),
                "--song-catalog-path", str(root / "data/song_catalog.json"),
                "--ai-voice-wav", str(root / "assets/41041.wav")]
    server.main()


if __name__ == "__main__":
    main()
