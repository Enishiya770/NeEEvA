# NeEEvA dedicated singing voice

This directory trains a local RVC v2 f0 32 kHz model for the active character.
Unlike zero-shot Seed-VC, the model learns the character timbre from a clean local
corpus and then transfers that timbre onto the user's real timing, melody, lyrics,
breaths and pitch variation.

The private generated corpus, training checkpoints, official repository, Python
environment and final weights are ignored by Git.  The checked-in scripts are
enough to recreate them:

Set `NEEEVA_GPT_SOVITS_ROOT` when GPT-SoVITS is installed outside this project,
or `NEEEVA_FFMPEG_DIR` when `ffmpeg.exe` and `ffprobe.exe` live elsewhere.

1. Run `install_rvc.ps1` once.
2. Generate the speech corpus with `prepare_character_dataset.py` and the
   humming/vocalisation supplement with `prepare_character_hum_dataset.py`
   while GPT-SoVITS is on port 9880.  The current corpus contains 61 clips /
   12.80 minutes (10.26 minutes of speech plus 2.54 minutes of humming-like
   voiced material).
3. Stop other GPU-heavy model processes temporarily and run:
   `.venv\Scripts\python.exe train_character_rvc.py --epochs 100 --batch-size 2`

The training pipeline is resumable.  It stages only WAV files, slices the corpus,
extracts RMVPE F0 and HuBERT features, trains the v2 model, builds the retrieval
index, and exports `models/neeeva_character.pth` plus
`models/neeeva_character.index`.

The final run produced 294 clean training slices.  Held-out humming evaluation
plateaued after epoch 75, so the deployed weight intentionally exports
`neeeva_character_v2_e75_s10875.pth` instead of the later epoch-100 checkpoint.
Use `--export-epoch 75` to reproduce that selection.

`Server/SeedVC/seedvc_server.py` automatically prefers this dedicated model when
both exported files exist.  It computes a bounded whole-melody pitch shift into
the character's natural register, while RVC preserves the relative notes and
human micro-variation.  Humming inference is deterministic with seed 1234 and
retrieval is disabled by default because the local index reduced held-out pure
humming identity in this corpus.  Seed-VC remains available as an automatic
pre-model fallback.

## Learning a melody from an original song

Do not add a commercial original singer to the character training corpus: that
would teach the target model the wrong identity. Instead,
`prepare_original_song_cover.py` uses the bundled local UVR5 model to isolate the
lead vocal, treats it as melody/lyrics/performance input, converts it with the
existing character RVC, and remixes it with the separated backing track. The
generated previews stay under the Git-ignored `song_covers` folder.
