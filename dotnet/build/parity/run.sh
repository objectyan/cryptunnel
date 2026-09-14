#!/usr/bin/env bash
#
# 跨语言加密对齐一键校验（Java ↔ .NET，三种算法）
#
# 做两件事：
#   1. 正方向：C# 解开 Java 侧导出的基准载荷 + 本地 round-trip/篡改/隔离共 52 项断言
#   2. 反方向：Java 解开 C# 现场加密的载荷
# 两个方向都通过，才能认定三端字节级一致。
#
# 用法：bash dotnet/build/parity/run.sh
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
OUT="$HERE/out-reverse"

# --- Java 侧 classpath ----------------------------------------------------
# 反方向校验只需要 cryptunnel-core 的三个 TunnelCipher 实现，不需要整个客户端。
#
# 历史坑：这里原先指向 $REPO/target/cryptunnel-client.jar（老 Java CLI 的 fat jar）。
# 结构重构把老 CLI 整棵树连根 pom 一并删除后，那个 jar **永久不会再产生**，
# 于是本脚本第 3 步固定报「找不到 …，请先跑 mvn package」并 exit 2 ——
# 看起来像"忘了构建"这种可自愈的小事，实际是依赖对象已不存在，
# 照着提示跑 mvn 也永远修不好。
#
# 现在改为直接用 core 的 jar；SM4 需要 BouncyCastle（core 里是 optional 依赖，
# 不会自动进 classpath），所以显式从本地仓库找一份拼上。
#
# BC 版本必须**从 pom 读**，不能拿本地仓库里最新的那个：
# 本机 ~/.m2 下同时存在 1.76/1.78.1/1.79/1.80/1.83/1.84 六个版本（别的项目拉的），
# 早先用 `ls | tail -1` 会挑到 1.84，而 core 声明的是 1.78.1 ——
# 于是"跨语言一致性已验证"用的其实不是发布时真正依赖的那份 BC。
# SM4 实现一旦在版本间有任何行为差异，这种验证就是自欺欺人。
CORE_JAR="$REPO/java/cryptunnel-core/target/cryptunnel-core-1.0.0.jar"
BC_VERSION="$(sed -n 's|.*<bouncycastle\.version>\(.*\)</bouncycastle\.version>.*|\1|p' \
              "$REPO/java/pom.xml" | head -1)"
if [ -z "$BC_VERSION" ]; then
  echo "无法从 java/pom.xml 解析 bouncycastle.version"; exit 2
fi
BC_JAR="$HOME/.m2/repository/org/bouncycastle/bcprov-jdk18on/$BC_VERSION/bcprov-jdk18on-$BC_VERSION.jar"

# --- 沙箱环境兜底 ---------------------------------------------------------
# 1) 死代理会让 dotnet/NuGet 卡住，必须摘掉
# 2) 本沙箱清空了 ProgramFiles(x86) 等一批 Windows 环境变量，
#    NuGet 拼 "C:\Program Files (x86)\NuGet\Config" 时 path1 为 null，
#    表现为 NuGet.targets(782,5): Value cannot be null. (Parameter 'path1')
#    —— 这里补齐即可，与 SDK 版本无关。
dotnet_env=(
  env -u HTTPS_PROXY -u HTTP_PROXY
  "ProgramFiles=C:\\Program Files"
  "ProgramFiles(x86)=C:\\Program Files (x86)"
  "ProgramW6432=C:\\Program Files"
  "CommonProgramFiles=C:\\Program Files\\Common Files"
  "ProgramData=C:\\ProgramData"
  "ALLUSERSPROFILE=C:\\ProgramData"
  "PUBLIC=C:\\Users\\Public"
  "APPDATA=${APPDATA:-C:\\Users\\$USERNAME\\AppData\\Roaming}"
)

# JDK 不在 PATH 上时按常见安装位置探测
if command -v javac >/dev/null 2>&1; then
  JAVAC=javac
  JAVA=java
else
  JDK_BIN="$(ls -d /c/Program\ Files/Eclipse\ Adoptium/jdk-*/bin 2>/dev/null | tail -1 || true)"
  [ -n "$JDK_BIN" ] || { echo "找不到 JDK，请把 javac 加入 PATH"; exit 2; }
  JAVAC="$JDK_BIN/javac"
  JAVA="$JDK_BIN/java"
fi

mkdir -p "$OUT"

echo ">>> [1/3] 正方向：C# 自检（含解 Java 载荷、round-trip、篡改、跨算法隔离）"
# dotnet 是原生 Windows 程序，不认 MSYS 的 /d/... 路径，全部过 cygpath 转换
"${dotnet_env[@]}" dotnet run --project "$(cygpath -w "$HERE/CryptoParity/CryptoParity.csproj")" -c Release --nologo

echo
echo ">>> [2/3] 导出 C# 侧加密载荷"
CSPROJ_DLL="$HERE/CryptoParity/bin/Release/net8.0/CryptoParity.dll"
"${dotnet_env[@]}" dotnet "$(cygpath -w "$CSPROJ_DLL")" --export > "$OUT/dotnet-export.txt"
echo "已写入 $OUT/dotnet-export.txt"

echo
echo ">>> [3/3] 反方向：Java 解开 C# 载荷"
if [ ! -f "$CORE_JAR" ]; then
  echo "找不到 $CORE_JAR"
  echo "请先构建：cd java && JAVA_HOME=<JDK8> mvn.cmd -Pboot25 -DskipTests package"
  exit 2
fi
if [ ! -f "$BC_JAR" ]; then
  echo "找不到 bcprov-jdk18on $BC_VERSION（SM4 用得到）：$BC_JAR"
  echo "先跑一次 mvn 构建即可拉到本地仓库。"
  exit 2
fi
echo "    core: $CORE_JAR"
echo "    bc  : $BC_JAR  (版本取自 java/pom.xml)"

# javac/java 同样是原生 Windows 程序：路径与 classpath 都要转成 Windows 形式，
# classpath 分隔符用分号（Git Bash 下需整体加引号，否则被当命令分隔）
CP_WIN="$(cygpath -w "$CORE_JAR");$(cygpath -w "$BC_JAR")"
"$JAVAC" -encoding UTF-8 -cp "$CP_WIN" \
  -d "$(cygpath -w "$OUT")" "$(cygpath -w "$HERE/ReverseParityCheck.java")"
"$JAVA" -Dfile.encoding=UTF-8 \
  -cp "$CP_WIN;$(cygpath -w "$OUT")" \
  ReverseParityCheck "$(cygpath -w "$OUT/dotnet-export.txt")"

echo
echo ">>> 双向对齐全部通过"
