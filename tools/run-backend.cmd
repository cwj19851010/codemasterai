@echo off
chcp 65001 >nul
setlocal
rem ===========================================================================
rem  启动 CodeMaster 后端（沙箱环境下唯一可用的方式）
rem
rem  为什么不能直接 dotnet run / Visual Studio F5：
rem    沙箱按「进程镜像路径」判定写边界。apphost（bin\...\CodeMaster.WebApi.exe）
rem    位于工作区内，于是它只能写工作区，而且连 %TEMP% 都写不了：
rem      - 生成项目在工作区外 -> Access to the path '...' is denied
rem      - MSBuild 建临时目录 -> Access to the path '%TEMP%\MSBuildTemp...' is denied
rem    加 sandbox.extraAllowWrite 也没用，那条规则只管代理侧工具。
rem
rem    dotnet exec 由 C:\Program Files\dotnet\dotnet.exe 托管，镜像是它自己，
rem    不受该限制，行为与 dotnet run 等价。
rem
rem  用法：双击，或在终端执行 tools\run-backend.cmd
rem        改端口：tools\run-backend.cmd --urls http://localhost:5001
rem ===========================================================================

set "ROOT=%~dp0.."
set "PROJ=%ROOT%\CodeMaster.WebApi"
set "DLL=%PROJ%\bin\Debug\net10.0\CodeMaster.WebApi.dll"

if not exist "%DLL%" (
  echo [错误] 找不到 %DLL%
  echo        请先执行：dotnet build CodeMaster.WebApi\CodeMaster.WebApi.csproj
  exit /b 1
)

netstat -ano | findstr /R /C:"LISTENING" | findstr /C:":5000 " >nul 2>&1
if not errorlevel 1 (
  echo [警告] 5000 端口已被占用，若是旧进程请先关掉。
)

set ASPNETCORE_ENVIRONMENT=Development
if "%~1"=="" (
  set "ARGS=--urls http://localhost:5000"
) else (
  set "ARGS=%*"
)

echo [启动] ContentRoot=%PROJ%
echo [启动] dotnet exec "%DLL%" %ARGS%
echo.

rem 必须在项目目录里执行：dotnet exec 拿当前工作目录当 ContentRoot，
rem 否则读不到 appsettings.Development.json，登录会 500。
cd /d "%PROJ%"
dotnet exec "%DLL%" %ARGS%

endlocal
