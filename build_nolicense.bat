@echo off
chcp 65001 >nul
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=-reference:System.Windows.Forms.dll -reference:System.Drawing.dll -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll -reference:System.Management.dll
"%CSC%" -nologo -target:winexe -platform:x64 -out:src\_inner_nolock.exe -optimize+ ^
  -define:NOLICENSE -define:OFFLINE ^
  -win32icon:assets\app.ico -win32manifest:assets\ace.manifest ^
  -resource:assets\ace_payload.zip -resource:assets\ace-ops.txt ^
  %REFS% src\ace_tool2.cs
if errorlevel 1 (echo 编译失败 & pause & exit /b 1)
echo 免卡密内层已生成：src\_inner_nolock.exe
echo 打包：
echo   python pack8.py src\_inner_nolock.exe "ACE工具箱vX.Y.Z_免卡密版.exe" 5 --admin --sign --clean
pause
