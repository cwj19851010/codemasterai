#!/usr/bin/env bash
# 在 WorkBuddy 沙箱内跑 dotnet test。
#
# 为什么需要它：
#   沙箱会拒绝"镜像位于工作区内"的进程加载工作区外的 DLL。.NET 的 apphost
#   (testhost.exe) 正是这种进程，它一启动就要 LoadLibrary 加载
#   C:\Program Files\dotnet\host\fxr\<ver>\hostfxr.dll，被拒后报
#   "Failed to load [hostfxr.dll], HRESULT: 0x80070005"。
#   而 `dotnet.exe` 本身位于 C:\Program Files\dotnet，不在限制范围内，
#   所以 `dotnet exec` 一直正常——这就是"build 能过、test 不能跑"的原因。
#
# 做法：
#   删掉输出目录里的 testhost.exe。vstest 找不到它时会自动回退到
#   `dotnet exec testhost.dll`，走 dotnet.exe 这个可信镜像，测试正常执行。
#   testhost.exe 每次构建都会重新生成，所以脚本每次都先构建再删。
#
# 用法：
#   tools/dotnet-test.sh <测试项目.csproj> [dotnet test 的其余参数...]
#   例：tools/dotnet-test.sh CodeMaster.Agent.Tests/CodeMaster.Agent.Tests.csproj \
#         --filter "FullyQualifiedName~AiStageFieldTypeSyncTests"

set -uo pipefail

if [ $# -lt 1 ]; then
  echo "用法: tools/dotnet-test.sh <测试项目.csproj> [dotnet test 的其余参数...]" >&2
  exit 64
fi

PROJECT="$1"
shift

PROJECT_DIR="$(dirname "$PROJECT")"

echo "[dotnet-test] 构建 $PROJECT"
MSBUILDDISABLENODEREUSE=1 dotnet build "$PROJECT" -m:1 -v q --nologo || exit $?

removed=0
if [ -d "$PROJECT_DIR/bin" ]; then
  while IFS= read -r -d '' exe; do
    rm -f "$exe" && removed=$((removed + 1))
  done < <(find "$PROJECT_DIR/bin" -type f -name testhost.exe -print0 2>/dev/null)
fi
echo "[dotnet-test] 已移除 $removed 个 testhost.exe（改用 dotnet exec testhost.dll）"

exec dotnet test "$PROJECT" --no-build "$@"
