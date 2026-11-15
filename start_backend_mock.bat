@echo off
REM DEV MODE ONLY: scripted backend replies (no VLM). Used to test Unity movement without a GPU/model.
title MOSSBOUND backend (MOCK / DEV MODE)
cd /d "%~dp0backend"
set MOSS_MOCK=1
echo  DEV MODE: replies are scripted, NOT produced by a vision-language model.
python server.py
pause
