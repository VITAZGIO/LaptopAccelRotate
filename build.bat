@echo off
chcp 65001 >nul
title Сборка AutoRotate.exe
echo.
echo   Сборка AutoRotate.exe
echo   =====================
echo.

cd /d "%~dp0"

if not exist "AutoRotate.cs" (
    echo   [ОШИБКА] Рядом с батником нет файла AutoRotate.cs
    echo.
    pause
    exit /b 1
)

set ICON=
if exist "AutoRotate.ico" (
    set ICON=/win32icon:AutoRotate.ico
    echo   Иконка: AutoRotate.ico
) else (
    echo   [!] AutoRotate.ico не найден, собираю без иконки
)

set CSC=
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not defined CSC (
    echo   [ОШИБКА] Компилятор csc.exe не найден.
    echo   Нужен .NET Framework 4, обычно он есть в Windows по умолчанию.
    echo.
    pause
    exit /b 1
)

echo   Компилятор: %CSC%
echo.

"%CSC%" /nologo /target:winexe /optimize+ /out:AutoRotate.exe %ICON% ^
    /reference:System.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    AutoRotate.cs

if errorlevel 1 (
    echo.
    echo   [ОШИБКА] Сборка не удалась, текст ошибки выше.
    echo.
    pause
    exit /b 1
)

echo.
echo   Готово: AutoRotate.exe
echo   Запускай двойным кликом, права администратора не нужны.
echo.
pause
