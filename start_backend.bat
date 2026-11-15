@echo off
REM MOSSBOUND - start the VLM backend (Ollama must be installed; it normally runs in the tray).
title MOSSBOUND backend
cd /d "%~dp0backend"
where ollama >nul 2>nul || set "PATH=%LOCALAPPDATA%\Programs\Ollama;%PATH%"
curl -s http://127.0.0.1:11434/api/version >nul 2>nul || (start "" /min ollama serve & timeout /t 4 >nul)
python -c "import fastapi, uvicorn, httpx, PIL" 2>nul || python -m pip install -r requirements.txt
echo.
echo  MOSSBOUND backend starting on http://127.0.0.1:8765  (first start loads the model into GPU memory)
echo  Keep this window open while you press Play in Unity. Ctrl+C to stop.
echo.
python server.py
pause
