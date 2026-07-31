@echo off
rem ---------------------------------------------------------------------------
rem  Lance Studio Photo en recompilant d'abord le code source.
rem  Le raccourci du bureau pointe sur ce fichier : il n'y a donc jamais de
rem  version perimee a lancer, meme apres une modification du code.
rem ---------------------------------------------------------------------------
setlocal
chcp 65001 >nul
title Studio Photo - demarrage

set "RACINE=%~dp0.."
set "PROJET=%RACINE%\src\Studio.App\Studio.App.csproj"
set "EXE=%RACINE%\src\Studio.App\bin\Debug\net8.0-windows\Studio.App.exe"

echo.
echo   Studio Photo
echo   ------------
echo   Compilation de la derniere version du code...
echo.

dotnet build "%PROJET%" -c Debug --nologo -v quiet
if errorlevel 1 goto :echec

if not exist "%EXE%" goto :introuvable

echo   Demarrage...
start "" "%EXE%"
exit /b 0

:echec
echo.
echo   ***  LA COMPILATION A ECHOUE  ***
echo.
echo   Le code source contient une erreur : l'application n'a pas ete lancee.
echo   La version precedente n'a pas ete remplacee, rien n'est casse.
echo   Recopiez le message ci-dessus et transmettez-le.
echo.
pause
exit /b 1

:introuvable
echo.
echo   ***  EXECUTABLE INTROUVABLE  ***
echo.
echo   La compilation a reussi mais le fichier attendu est absent :
echo   %EXE%
echo.
pause
exit /b 1
