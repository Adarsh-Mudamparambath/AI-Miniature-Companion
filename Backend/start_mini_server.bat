@echo off
REM Double-click this file to start Mini's free AI server (Whisper + Ollama + Piper).
REM Keep the Quest plugged in with the USB cable, then open Mini in the headset.
cd /d "%~dp0"

echo Connecting the Quest to this PC over USB...
"C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" reverse tcp:8787 tcp:8787
if errorlevel 1 echo WARNING: Quest not found. Plug in the USB cable and allow USB debugging in the headset, then run this again.

echo Starting Mini server (first start takes about 20 seconds to load the models)...
set MINI_HOST=127.0.0.1
set MINI_PORT=8787
python -P server.py
pause
