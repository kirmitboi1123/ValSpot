@echo off
title valspot - Spotify on death / buy phase
cd /d "%~dp0"
".venv\Scripts\python.exe" valspot.py run
pause
