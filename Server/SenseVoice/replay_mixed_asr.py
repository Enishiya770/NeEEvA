"""Offline real-waveform comparison; never learns speakers or writes the song library.

python replay_mixed_asr.py band_dumps/TAKE.json --model LOCAL_MODEL_DIR --output REPORT.json
Historical metadata supplies boundary hypotheses. --fresh-analysis re-runs the
current acoustic analyzer. Optional reference_segments are human annotations;
the old ASR text is a baseline, never a correctness reference.
"""
import argparse
import faulthandler
import json
import time
from pathlib import Path

import soundfile as sf
from funasr import AutoModel
import sensevoice_server as server
from mixed_turn_asr import transcribe_segments
from singing_analysis import SingingAnalyzer


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("metadata", nargs="+")
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--fresh-analysis", action="store_true")
    parser.add_argument("--device", default="cpu")
    args = parser.parse_args()
    # An explicit local directory prevents a replay from downloading a model.
    if not Path(args.model).is_dir():
        parser.error("--model must be an existing local model directory")
    faulthandler.dump_traceback_later(60, repeat=True)
    print(f"Loading local ASR on {args.device}", flush=True)
    model = AutoModel(model=args.model, trust_remote_code=True, disable_update=True, device=args.device)
    print("Local ASR loaded", flush=True)
    def recognize(piece):
        result = model.generate(input=piece, cache={}, language="auto", use_itn=True,
                                disable_pbar=True)
        parsed = server.parse_output(result[0]["text"] if result else "")
        return dict(text=parsed[0], language=parsed[1], audio_event=parsed[3])
    analyzer = SingingAnalyzer(enable_torchcrepe=True) if args.fresh_analysis else None
    if analyzer:
        analyzer.island_transcriber = lambda piece: recognize(piece)["text"]
    reports = []
    for item in args.metadata:
        meta_path = Path(item)
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        wav_path = meta_path.with_suffix(".content.wav")
        if not wav_path.exists():
            wav_path = meta_path.with_suffix(".wav")
        wav, rate = sf.read(wav_path, dtype="float32")
        if rate != 16000 or wav.ndim != 1:
            raise ValueError("Replay expects mono 16k content WAV")
        t0 = time.perf_counter()
        print(f"Whole ASR: {wav_path}", flush=True)
        whole = recognize(wav)
        whole_seconds = time.perf_counter() - t0
        if analyzer:
            print("Fresh acoustic analysis", flush=True)
            analysis = analyzer.analyze(wav, lyrics=whole["text"], audio_event=whole["audio_event"],
                                        language=whole["language"], thorough=True,
                                        force_score=bool(meta.get("expect_singing")))
        else:
            analysis = dict(meta, analysis_available=True)
        started = time.perf_counter()
        print("Independent segment ASR", flush=True)
        result = transcribe_segments(wav, analysis, recognize, whole["text"])
        reports.append(dict(sample=str(wav_path), historical_text=meta.get("text"),
                            whole=whole, whole_seconds=whole_seconds,
                            segmentation_seconds=time.perf_counter() - started,
                            fresh_analysis=bool(analyzer), result=result,
                            boundary_candidates=analysis.get("asr_boundary_candidates", []),
                            reference_segments=meta.get("reference_segments", [])))
        print(json.dumps(reports[-1], ensure_ascii=False), flush=True)
    Path(args.output).write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding="utf-8")
    faulthandler.cancel_dump_traceback_later()


if __name__ == "__main__":
    main()
