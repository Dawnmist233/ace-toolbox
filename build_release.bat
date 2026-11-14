@echo off
chcp 65001 >nul
cd /d "%~dp0tools"
python build_final.py _inner_gui.exe
if errorlevel 1 (echo 构建失败 & pause & exit /b 1)
echo.
echo 内层已生成：%~dp0tools\_inner_gui.exe
echo 打包请执行（在 ace_pack 目录）：
echo   python pack8.py 内层.exe "ACE工具箱vX.Y.Z.exe" 5 --admin --sign --clean
pause
