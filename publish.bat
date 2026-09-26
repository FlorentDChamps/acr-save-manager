@echo off
dotnet publish ACRSaveManager.csproj ^
  -c Release ^
  -r win-x64 ^
  --no-self-contained ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
rem PDF copies of the READMEs. pandoc turns the GitHub Markdown into a plain HTML page, with
rem pandoc's own default look and no stylesheet of ours, and Edge, present on every Windows
rem 10/11, prints it. The CI badge is left out: a live build status means nothing on paper.
rem The page is written to %TEMP%, where the screenshots' relative paths lead nowhere, so
rem pandoc embeds them in it, looked up from the READMEs' own folder.
set "EDGE=%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE%" set "EDGE=%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
set "WORK=%TEMP%\acrsavemanager-pdf"
rem Last run's PDFs go first, made or not this time: stale ones would end up in the zip.
del "%~dp0publish\README*.pdf" 2>nul
where pandoc >nul 2>nul || (
  echo PDF skipped: pandoc not found ^(https://pandoc.org^).
  goto done
)
if not exist "%EDGE%" (
  echo PDF skipped: Microsoft Edge not found.
  goto done
)
if not exist "%WORK%" mkdir "%WORK%"
for %%f in (README*.md) do (
  findstr /v /b /c:"[![build]" "%%f" | pandoc -f gfm -t html5 -s --embed-resources --resource-path="%~dp0." --metadata pagetitle="ACR Save Manager" -o "%WORK%\%%~nf.html"
  "%EDGE%" --headless --disable-gpu --no-pdf-header-footer --user-data-dir="%WORK%\edge" --print-to-pdf="%~dp0publish\%%~nf.pdf" "%WORK%\%%~nf.html" >nul 2>nul
  if exist "%~dp0publish\%%~nf.pdf" (echo %%~nf.pdf) else (echo PDF failed: %%~nf.pdf)
)
rmdir /s /q "%WORK%" 2>nul

:done
rem What gets handed out: the exe and the PDFs, in one zip. Whatever PDF is missing (pandoc or
rem Edge absent) is simply not in it. Compress-Archive comes with Windows 10/11's PowerShell.
powershell -NoProfile -Command "$files = @('ACRSaveManager.exe', 'README.pdf', 'README.fr.pdf') | ForEach-Object { Join-Path '%~dp0publish' $_ } | Where-Object { Test-Path $_ }; Compress-Archive -Path $files -DestinationPath '%~dp0publish\ACRSaveManager.zip' -Force"
if errorlevel 1 (
  echo Zip failed.
) else (
  echo ACRSaveManager.zip
)
echo.
echo Done. Output in .\publish\
