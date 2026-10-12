@echo off
rem Builds EndTask.exe with the .NET Framework 4 compiler that ships with Windows 10.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set REFS=-r:Accessibility.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll

rem First build without an icon so the program can draw EndTask.ico for itself.
"%CSC%" -nologo -optimize -target:winexe %REFS% -out:EndTask.exe EndTask.cs || exit /b 1
".\EndTask.exe" /makeicon EndTask.ico
"%CSC%" -nologo -optimize -target:winexe -win32icon:EndTask.ico %REFS% -out:EndTask.exe EndTask.cs || exit /b 1
echo Done: %~dp0EndTask.exe
