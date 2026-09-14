#!/usr/bin/env bash
# 重复运行 SunriseParityCheck（JDK 8，本机路径）。
# 用途：改名后 cryptunnel-core 与老服务端 com.crm.sunrise.jdbcproxy.AesUtil 的字节级互通验证。
# 红线：只读引用 sunrise 的 AesUtil.class（见 ./lib 副本），绝不修改 sunrise 源。
set -e
cd "$(dirname "$0")/../.."   # 仓库根 jdbc-proxy-client
REPO_ROOT="$PWD"

JAVAC="${JAVA8:-/c/Users/OY/.jdks/corretto-1.8.0_504/bin/javac}"
JAVA="${JAVA8:-/c/Users/OY/.jdks/corretto-1.8.0_504/bin/java}"

CORE_W=$(cygpath -w "/c/Users/OY/.m2/repository/io/github/objectyan/cryptunnel-core/1.0.0/cryptunnel-core-1.0.0.jar")
BC_W=$(cygpath -w   "/c/Users/OY/.m2/repository/org/bouncycastle/bcprov-jdk18on/1.78.1/bcprov-jdk18on-1.78.1.jar")
LIB_W=$(cygpath -w "$REPO_ROOT/verify/lib")

rm -rf out && mkdir -p out
"$JAVAC" -encoding UTF-8 -cp "$CORE_W;$BC_W;$LIB_W" -d out verify/sunrise-parity/SunriseParityCheck.java
"$JAVA"  -cp "out;$CORE_W;$BC_W;$LIB_W" SunriseParityCheck
