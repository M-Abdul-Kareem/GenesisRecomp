@echo off
rem  Builds the single-file, self-contained GenesisRecomp.exe (no .NET install needed to run it) into  publish\
rem  This is the same exe the GitHub workflow produces. Run it from a Developer PowerShell / Command Prompt.
rem  Note: if Generated\Rom_*.cs files exist here, the exe also contains those games' recompiled code (for your own use only).
setlocal
set "HERE=%~dp0"
dotnet publish "%HERE%GenesisRecomp.csproj" -c Release -r win-x64 --self-contained true -p:OutputType=WinExe -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%HERE%publish"
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo.
echo Done:  %HERE%publish\GenesisRecomp.exe
echo Double-click it: it offers to install, then you can double-click game files to play them.
endlocal
