@echo off
rem GPT-SoVITS api_v2 TTS server for NeEEvA (Antoneva voice)
rem Unity side posts to http://127.0.0.1:9880/tts
rem Voice weights and reference audio are preconfigured in GPT_SoVITS/configs/tts_infer.yaml
cd /d %~dp0
echo Starting GPT-SoVITS TTS server on http://127.0.0.1:9880 ...
runtime\python.exe api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS/configs/tts_infer.yaml
pause
