@echo off
rem Builds dist\valspot.exe (one file, console window). Run from this folder.
cd /d "%~dp0"
".venv\Scripts\python.exe" -m PyInstaller --noconfirm --clean --onefile --console --name valspot --icon assets\valspot.ico ^
  --add-data "templates;templates" ^
  --collect-all winrt ^
  --exclude-module tkinter ^
  valspot.py
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built dist\valspot.exe
