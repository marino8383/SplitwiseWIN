@echo off
REM Doppio clic per creare il pacchetto zip da condividere (versione corrente, niente incremento).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0distribuisci.ps1"
