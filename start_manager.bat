@echo off
cd /d "%~dp0"
set "PYTHONPATH=%CD%\src;%PYTHONPATH%"
if not exist "config\settings.toml" if exist "config\settings.example.toml" (
  copy "config\settings.example.toml" "config\settings.toml" >nul
)
python -c "import forum_bookmark_manager, fastapi, uvicorn, playwright" >nul 2>nul
if errorlevel 1 (
  echo Installing required Python packages...
  python -m pip install -e .
  if errorlevel 1 (
    echo Failed to install dependencies.
    pause
    exit /b 1
  )
)
python -m forum_bookmark_manager.cli open
if errorlevel 1 pause
