@echo off
setlocal
cd /d "%~dp0"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the C# compiler at:
  echo   %CSC%
  echo Your Windows .NET Framework may be in a different location.
  pause
  exit /b 1
)

echo Building LLMChoir.exe ...
"%CSC%" /nologo /target:winexe /platform:x64 /win32manifest:app.manifest ^
  /out:LLMChoir.exe ^
  /reference:Microsoft.Web.WebView2.Core.dll ^
  /reference:Microsoft.Web.WebView2.WinForms.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Management.dll ^
  MultiLLM.cs

if exist LLMChoir.exe (
  echo.
  echo Build OK -^> LLMChoir.exe
) else (
  echo.
  echo Build FAILED.
)
endlocal
