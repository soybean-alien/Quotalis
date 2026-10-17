@echo off
rem Builds and starts Claudex from source. Usage: run [-Demo] [-Release]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\run.ps1" %*
