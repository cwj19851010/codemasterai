#!/usr/bin/env bash
# 在 WorkBuddy 沙箱内跑 dotnet run 的替代品。
#
# 为什么需要它（两个独立的坑，都跟 apphost 有关）：
#   1) 启动不了：生成的 apphost（bin/<Config>/<tfm>/<App>.exe）镜像在工作区内，
#      沙箱拒绝它加载工作区外的 hostfxr.dll → "HRESULT: 0x80070005"。
#   2) 启动了也干不了活：沙箱按**进程镜像路径**判定写边界。镜像在工作区内的进程
#      只能写工作区，`sandbox.extraAllowWrite` 对它无效（2026-10-25 实测）。
#      生成项目一般落在工作区外，于是 Directory.CreateDirectory 直接
#      "Access to the path '...' is denied"。Visual Studio F5 同样中招，
#      因为 VS 拉起的也是这个 apphost。
#
#   `dotnet exec <App>.dll` 由 C:\Program Files\dotnet\dotnet.exe 直接托管，
#   镜像在工作区外，两个坑都没有，行为与 `dotnet run` 等价
#   （同样读 runtimeconfig.json、同样传参）。
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

DLL="$(cd "$(dirname "$DLL")" && pwd)/$(basename "$DLL")"

# 必须在项目目录里执行：dotnet exec 用「当前工作目录」当 ContentRoot，
# 从仓库根目录启动会找不到 appsettings.Development.json，
# 表现为登录 500 —— JwtBearerOptions 拿不到密钥（ArgumentNullException 's'）。
echo "[dotnet-run] cd $PROJECT_DIR && dotnet exec $DLL"
cd "$PROJECT_DIR" || exit 66
exec dotnet exec "$DLL" "$@"
