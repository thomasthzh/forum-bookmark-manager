@echo off
cd /d "%~dp0"
set "PYTHONPATH=%CD%\src;%PYTHONPATH%"
python -m forum_bookmark_manager.cli stop
if errorlevel 1 pause
