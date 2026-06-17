@echo off
cd /d "%~dp0"
if not exist LLMChoir.exe (
  call build.bat
  if not exist LLMChoir.exe exit /b 1
)
start "" LLMChoir.exe
