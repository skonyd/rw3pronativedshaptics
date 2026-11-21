@echo off
rem Starts the test game "Şişe Atışı" (src\RW3TestRange). Builds it first if needed. Run it through explorer.exe or a
rem double-click so its logs land in the real %LOCALAPPDATA%\RW3TestRange\logs (not a packaged app's redirect).
set ROOT=%~dp0..
set EXE=%ROOT%\src\RW3TestRange\bin\Release\net10.0-windows\RW3TestRange.exe
if not exist "%EXE%" "C:\Program Files\dotnet\dotnet.exe" build "%ROOT%\src\RW3TestRange\RW3TestRange.csproj" -c Release -nologo -v q
start "" "%EXE%"
