@echo off
rem  Adds a game to the program: recompiles its 68000 code, builds a release version into Port\, and writes a Play shortcut.
rem  Usage:  build_port.cmd "C:\path\to\game.bin"              (needs the .NET 8 Desktop Runtime on the PC that runs it)
rem          build_port.cmd "C:\path\to\game.bin" standalone    (one self-contained .exe, no .NET install needed; bigger file)
rem  Run it once per game. Games added earlier stay inside the program, so Port\GenesisRecomp.exe plays all of them.
rem  Any other ROM can also be dragged onto Port\GenesisRecomp.exe: it plays through the interpreter without any building.
setlocal
if "%~1"=="" (
  echo Usage: build_port.cmd "C:\path\to\game.bin" [standalone]
  exit /b 1
)
if not exist "%~1" (
  echo ROM not found: %~1
  exit /b 1
)
set "ROM=%~f1"
set "HERE=%~dp0"

echo [1/3] Recompiling the game's 68000 code to C#...
dotnet run --project "%HERE%GenesisRecomp.csproj" -c Release -- --recompile "%ROM%"
if errorlevel 1 (
  echo Recompile step failed.
  exit /b 1
)

echo [2/3] Building and publishing...
if /i "%~2"=="standalone" (
  dotnet publish "%HERE%GenesisRecomp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%HERE%Port"
) else (
  dotnet publish "%HERE%GenesisRecomp.csproj" -c Release -p:DebugType=None -p:DebugSymbols=false -o "%HERE%Port"
)
if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)

echo [3/3] Writing the shortcut...
if not exist "%HERE%Port\roms" mkdir "%HERE%Port\roms"
copy /y "%ROM%" "%HERE%Port\roms\" >nul
> "%HERE%Port\Play %~n1.cmd" echo @echo off
>> "%HERE%Port\Play %~n1.cmd" echo start "" "%%~dp0GenesisRecomp.exe" "%%~dp0roms\%~nx1"
echo.
echo Done. Double-click:  %HERE%Port\Play %~n1.cmd
echo The program in Port\ contains code derived from the games you added: keep it for yourself, don't share it.
endlocal
