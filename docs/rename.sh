#!/usr/bin/env bash
# 全量标识符重命名：jdbc-proxy / JdbcProxy / jdbcproxy  ->  cryptunnel / Cryptunnel / cryptunnel
#
# 设计要点（每一条都是踩过的坑）：
#
# 1) 三段式：保护 -> 替换 -> 还原。
#    历史文档里记录着「旧包名 com.crm.sunrise.jdbcproxy」这类**已发生的事实**，
#    它们含 jdbcproxy 子串会被规则命中，改了就等于伪造历史，
#    会凭空造出一个从未存在过的包名 com.crm.sunrise.cryptunnel。
#
# 2) 用 sed 不用 perl —— 本机 Git Bash 没有 perl，
#    而 perl 缺失时只在 stderr 打 "command not found"，
#    脚本若不校验就会**报告成功但一个字都没改**。
#
# 3) 每个文件替换后必须自检：剩余命中数 == 受保护串贡献的命中数。
#    对不上就地失败，不静默放过。
#
# 4) 只改内容，不改文件名/目录名（后续步骤单独做，
#    混在一起会让「替换了多少处」失去可核对性）。
#
# 用法：rename.sh <文件清单> [--dry-run]

set -u
LIST="${1:?用法: rename.sh <文件清单> [--dry-run]}"
DRY="${2:-}"

# ---- 需要原样保留的历史字符串（越长越靠前，避免短串先命中吃掉长串）----
#
# ⚠ 必须同时覆盖「点号形态」和「路径分隔符形态」两种写法。
#    实测漏掉斜杠形态的后果：docs/sunrise-integration.md:110 是一条要真执行的
#    `rm -rf src/main/java/com/crm/sunrise/jdbcproxy`，被改成 .../cryptunnel 后
#    指向一个根本不存在的目录 —— 而 `rm -rf` 删不到东西时**不报错**，
#    用户以为清理完了，实际旧类还在，要到编译期才炸。
PROTECT=(
  'com.crm.sunrise.jdbcproxy.starter.JdbcProxyProperties'
  'com.crm.sunrise.jdbcproxy.JdbcProxyProperties'
  'com.crm.sunrise.jdbcproxy.starter'
  'com.crm.sunrise.jdbcproxy.core'
  'com.crm.sunrise.jdbcproxy'
  'com.crm.sunrise.proxy'
  'com/crm/sunrise/jdbcproxy'
  'com/crm/sunrise/proxy'
)

# sed 的 BRE 里这些字符要转义
esc() { printf '%s' "$1" | sed 's/[.[\*^$\/]/\\&/g'; }

# 统计一个字符串里有多少处会被三条规则命中
hits_in_str() {
  printf '%s' "$1" | grep -o -E 'JdbcProxy|jdbc-proxy|jdbcproxy' | wc -l
}

changed=0; failed=0
total_before=0; total_after=0; total_protected=0

while IFS= read -r f; do
  [ -f "$f" ] || continue

  before=$(grep -o -E 'JdbcProxy|jdbc-proxy|jdbcproxy' "$f" 2>/dev/null | wc -l)
  total_before=$((total_before + before))
  [ "$before" -eq 0 ] && continue

  if [ "$DRY" = "--dry-run" ]; then
    printf '%6s  %s\n' "$before" "$f"
    continue
  fi

  tmp="$f.__rn__"
  cp "$f" "$tmp"

  # --- 段 1：保护（\x01 控制字符正常文本不会出现）---
  i=0
  for p in "${PROTECT[@]}"; do
    sed -i "s/$(esc "$p")/$(printf '\001')PROT${i}$(printf '\001')/g" "$tmp"
    i=$((i+1))
  done

  # 受保护串贡献了多少处命中（这些处不该被改）
  #
  # ⚠ 必须在段 1 之后、按**占位符实际落地数**统计，不能在原文上逐串 grep。
  #    原因：短串是长串的前缀（com.crm.sunrise.jdbcproxy ⊂
  #    com.crm.sunrise.jdbcproxy.JdbcProxyProperties），在原文上单独 grep 短串
  #    会把长串里的前缀一起数进去 → prot_hits 偏大 → 自检 2 误报失败。
  #    段 1 是长串优先替换的，长串先变成占位符，短串就吃不到了，
  #    所以占位符计数天然与实际保护量一致。
  prot_hits=0
  i=0
  for p in "${PROTECT[@]}"; do
    c=$(grep -o -F "$(printf '\001')PROT${i}$(printf '\001')" "$tmp" 2>/dev/null | wc -l)
    i=$((i+1))
    [ "$c" -eq 0 ] && continue
    per=$(hits_in_str "$p")
    prot_hits=$((prot_hits + c * per))
  done

  # --- 段 2：替换 ---
  sed -i -e 's/JdbcProxy/Cryptunnel/g' \
         -e 's/jdbc-proxy/cryptunnel/g' \
         -e 's/jdbcproxy/cryptunnel/g' "$tmp"

  # --- 段 3：还原 ---
  i=0
  for p in "${PROTECT[@]}"; do
    sed -i "s/$(printf '\001')PROT${i}$(printf '\001')/$(esc "$p")/g" "$tmp"
    i=$((i+1))
  done

  # --- 自检 1：占位符必须还原干净 ---
  if grep -q "$(printf '\001')" "$tmp"; then
    echo "!! 占位符残留，跳过: $f" >&2
    rm -f "$tmp"; failed=$((failed+1)); continue
  fi

  after=$(grep -o -E 'JdbcProxy|jdbc-proxy|jdbcproxy' "$tmp" 2>/dev/null | wc -l)

  # --- 自检 2：剩余命中数必须恰好等于受保护串的贡献 ---
  #     不等 => 要么没替换成功（工具缺失），要么误伤了该保留的内容
  if [ "$after" -ne "$prot_hits" ]; then
    echo "!! 自检失败，跳过: $f  (替换后剩 $after 处，预期 $prot_hits 处受保护)" >&2
    rm -f "$tmp"; failed=$((failed+1)); continue
  fi

  mv "$tmp" "$f"
  total_after=$((total_after + after))
  total_protected=$((total_protected + prot_hits))
  changed=$((changed+1))
done < "$LIST"

if [ "$DRY" = "--dry-run" ]; then
  echo "--- DRY RUN：共 $total_before 处待替换 ---"
  exit 0
fi

echo "--------------------------------------------"
echo "改动文件      : $changed"
echo "替换前命中    : $total_before 处"
echo "替换后剩余    : $total_after 处"
echo "其中受保护    : $total_protected 处（历史包名，有意保留）"
echo "实际替换      : $((total_before - total_after)) 处"
echo "自检失败      : $failed 个文件"
echo "--------------------------------------------"
[ "$failed" -eq 0 ] || exit 1
