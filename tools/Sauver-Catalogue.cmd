@echo off
rem ---------------------------------------------------------------------------
rem  Recopie le catalogue vivant de la boutique dans le depot, pour qu'il entre
rem  dans git.
rem
rem  Le catalogue vit dans D:\PhotoStudioData\catalog, HORS du depot : changer un
rem  prix ou capturer des reglages pilote ne se voit donc pas dans git status, et
rem  le seul filet est products.json.bak, qui ne garde que la version d'avant.
rem
rem  A lancer apres chaque changement du catalogue, puis a committer.
rem
rem  Le mot de passe WiFi (config\wifi.json) n'est PAS copie : le depot est
rem  public. Les profils ICC non plus : ils viennent des pilotes et se
rem  reimportent en deux clics depuis Catalogue.
rem ---------------------------------------------------------------------------
setlocal
chcp 65001 >nul
title Studio Photo - sauvegarde du catalogue

set "RACINE=%~dp0.."
set "SOURCE=D:\PhotoStudioData\catalog"
set "CIBLE=%RACINE%\catalog\boutique"

echo.
echo   Sauvegarde du catalogue
echo   -----------------------
echo.

if not exist "%SOURCE%\products.json" goto :sansSource

if not exist "%CIBLE%" mkdir "%CIBLE%"

copy /Y "%SOURCE%\products.json" "%CIBLE%\products.json" >nul
if errorlevel 1 goto :echec
echo   products.json           copie

set "DEVMODES=0"
for %%F in ("%SOURCE%\devmode-*.bin") do (
    copy /Y "%%F" "%CIBLE%\%%~nxF" >nul
    if not errorlevel 1 (
        echo   %%~nxF   copie
        set /a DEVMODES+=1
    )
)

echo.
echo   Copie faite dans catalog\boutique.
echo.
echo   Il reste a l'enregistrer dans git :
echo.
echo       git add catalog/boutique
echo       git commit -m "Sauvegarde du catalogue de la boutique"
echo       git push
echo.
pause
exit /b 0

:sansSource
echo   Catalogue introuvable : %SOURCE%\products.json
echo.
echo   Ce script attend les donnees de la boutique dans D:\PhotoStudioData.
echo   Sur un autre poste, il n'y a rien a sauvegarder.
echo.
pause
exit /b 1

:echec
echo.
echo   ***  LA COPIE A ECHOUE  ***
echo.
echo   Le fichier est peut-etre ouvert ailleurs, ou le disque protege en
echo   ecriture. Rien n'a ete modifie dans le catalogue de la boutique :
echo   ce script ne fait que lire.
echo.
pause
exit /b 1
