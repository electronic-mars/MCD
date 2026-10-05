@echo off
rem The control panel: a local site about the program, and a page of icons to tick.
rem Opens in the browser; close this window to stop it.
cd /d "%~dp0.."
python tools\panel\serve.py --open
if errorlevel 1 pause
