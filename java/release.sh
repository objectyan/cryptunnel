#!/usr/bin/env bash
#
# cryptunnel 发布脚本 —— 每个新版本同时发 javax + jakarta。
#
# 用法（在 Git Bash 里运行）：
#     bash java/release.sh 1.0.1
#
# 做了什么：
#   1) 用 mvn versions:set 把 parent + 4 个子模块的版本号统一改成新版本
#      （javax/jakarta 互斥于 boot25/jakarta 两个 profile，不能同 reactor 编译；
#       但 versions:set 只改写 pom、不编译，所以分两个 profile 各跑一次即可覆盖全 5 个 pom）
#   2) 发 javax   : mvn -Prelease,boot25 clean deploy                              （JDK 8）
#   3) 发 jakarta : mvn -Prelease,jakarta -pl cryptunnel-starter-jakarta clean deploy （JDK 17）
#      —— 第 3 步必须带 -pl 只发 jakarta，否则会重发已发布的 parent/core/common，
#         触发 Central "already exists" FAILED（见项目 MEMORY.md「发布流程」一节）
#
# 前置：
#   - ~/.m2/settings.xml 里 <server id=central> 与 <server id=gpg> 已配好
#   - GPG 密钥（keyname 880A1A81030EA764109E2BAC7D055743CE30D5EF）已在 %APPDATA%\gnupg
#   - 网络可达 Maven Central（本脚本已 unset 代理）
#
set -euo pipefail

NEW_VERSION="${1:-}"
if [[ -z "$NEW_VERSION" ]]; then
  echo "用法: $0 <新版本号>   例如: $0 1.0.1" >&2
  exit 1
fi

# ---- 环境（沙箱每次新 shell 都要整段 export；不能用 env 前缀）----
export PATH="/c/Users/OY/.workbuddy/binaries/PortableGit/versions/1.2.0/bin:/c/Windows/System32:/c/Windows:/usr/bin:/bin:$PATH"
unset HTTPS_PROXY HTTP_PROXY
export GNUPGHOME="C:/Users/OY/AppData/Roaming/gnupg"
MVN="/c/tools/maven_3/bin/mvn.cmd"
JAVA8="C:/Users/OY/.jdks/corretto-1.8.0_504"
JAVA17="C:/Users/OY/.jdks/graalvm-ce-17.0.9"

# 脚本放在 java/ 下，定位到它所在目录作为 Maven 基目录
BASE="$(cd "$(dirname "$0")" && pwd)"
POM="$BASE/pom.xml"

# ---- 0) 当前版本核对 ----
CUR_VERSION="$(grep -m1 '<version>' "$POM" | sed -E 's/.*<version>([0-9]+\.[0-9]+\.[0-9]+)<\/version>.*/\1/')"
echo "当前版本: ${CUR_VERSION:-?}  ->  目标版本: ${NEW_VERSION}"
if [[ "$CUR_VERSION" == "$NEW_VERSION" ]]; then
  echo "错误：目标版本与当前版本相同，请换个版本号。" >&2
  exit 1
fi

# ---- 1) 统一改版本号（parent + 4 子模块，靠 versions:set 一次性改全）----
echo "==> [1/3] 版本号 ${CUR_VERSION} -> ${NEW_VERSION}"
"$MVN" -f "$POM" -Pboot25   versions:set -DnewVersion="$NEW_VERSION" -DgenerateBackupPoms=false -B -q
"$MVN" -f "$POM" -Pjakarta versions:set -DnewVersion="$NEW_VERSION" -DgenerateBackupPoms=false -B -q
if ! grep -q "<version>${NEW_VERSION}</version>" "$POM"; then
  echo "错误：版本号未写入 parent pom，中止。" >&2
  exit 1
fi
echo "    版本号已更新（全 5 个 pom）。"

# ---- 2) 发 javax（parent+core+common+javax，JDK 8）----
echo "==> [2/3] 发布 javax (JDK8 corretto-1.8.0_504)"
export JAVA_HOME="$JAVA8"
"$JAVA_HOME/bin/java" -version 2>&1 | head -1
"$MVN" -f "$POM" -Prelease,boot25 clean deploy -B
echo "    javax 发布完成。"

# ---- 3) 发 jakarta（只发 jakarta，JDK 17）----
echo "==> [3/3] 发布 jakarta (JDK17 graalvm-ce-17.0.9, -pl)"
export JAVA_HOME="$JAVA17"
"$JAVA_HOME/bin/java" -version 2>&1 | head -1
"$MVN" -f "$POM" -Prelease,jakarta -pl cryptunnel-starter-jakarta clean deploy -B
echo "    jakarta 发布完成。"

echo ""
echo "✅ 发布流程结束：${NEW_VERSION} 已提交 Central（javax + jakarta 同版本）。"
echo "   待 Central 校验通过（通常几分钟）后即可在 https://repo1.maven.org/maven2/io/github/objectyan/ 看到。"
echo "   如某步报 already exists -> 按 MEMORY.md『发布流程』处理（通常是 jakarta 漏了 -pl）。"
