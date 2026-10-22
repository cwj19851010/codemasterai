#!/usr/bin/env bash
# 在 WorkBuddy 沙箱内跑 dotnet run 的替代品。
#
# 为什么需要它：
#   生成的 apphost（bin/<Config>/<tfm>/<App>.exe）镜像在工作区内，沙箱拒绝它
#   加载工作区外的 hostfxr.dll → "HRESULT: 0x80070005"。
#   `dotnet exec <App>.dll` 由 C:\Program Files\dotnet\dotnet.exe 直接托管，
#   不受该限制，行为与 `dotnet run` 等价（同样读 runtimeconfig.json、同样传参）。
#
# 用法：
#   tools/dotnet-run.sh <项目.csproj> [传给应用的参数...]
#   例：ASPNETCORE_ENVIRONMENT=Development \
#       tools/dotnet-run.sh CodeMaster.WebApi/CodeMaster.WebApi.csproj --urls http://localhost:5000

set -uo pipefail

if [ $# -lt 1 ]; then
  echo "用法: tools/dotnet-run.sh <项目.csproj> [应用参数...]" >&2
  exit 64
fi

PROJECT="$1"
shift

PROJECT_DIR="$(dirname "$PROJECT")"
NAME="$(basename "$PROJECT" .csproj)"

echo "[dotnet-run] 构建 $PROJECT"
MSBUILDDISABLENODEREUSE=1 dotnet build "$PROJECT" -m:1 -v q --nologo || exit $?

DLL="$(ls -t "$PROJECT_DIR"/bin/Debug/net*/"$NAME".dll 2>/dev/null | head -1)"
if [ -z "${DLL:-}" ]; then
  echo "[dotnet-run] 找不到 $PROJECT_DIR/bin/Debug/net*/$NAME.dll" >&2
  exit 65
fi

echo "[dotnet-run] dotnet exec $DLL"
exec dotnet exec "$DLL" "$@"
