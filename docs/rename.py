#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
全量标识符重命名：JdbcProxy / jdbc-proxy / jdbcproxy -> Cryptunnel / cryptunnel / cryptunnel

为什么是 Python 而不是 docs/rename.sh：
    bash 版逻辑正确（已在单文件上验证通过），但每个文件要 spawn ~20 个
    sed/grep 进程，154 个文件就是 3000+ 次进程创建。在 Windows 上这个开销
    足以让脚本跑到超时被 SIGTERM 打断 —— 而打断点在「已 mv 一部分文件」
    的半完成状态，是最难收拾的状态。
    本脚本单进程完成全部替换，逻辑与 bash 版逐条对应。

设计要点（每一条都是踩过的坑）：

1) 三段式：保护 -> 替换 -> 还原。
   历史文档里记录着「旧包名 com.crm.sunrise.jdbcproxy」这类**已发生的事实**，
   含 jdbcproxy 子串会被规则命中，改了就等于伪造历史，
   会凭空造出一个从未存在过的包名 com.crm.sunrise.cryptunnel。

2) PROTECT 必须同时覆盖「点号形态」「正斜杠形态」和「反斜杠形态」三种。
   实测漏掉斜杠形态的后果：docs/sunrise-integration.md:110 是一条要真执行的
   `rm -rf src/main/java/com/crm/sunrise/jdbcproxy`，被改成 .../cryptunnel 后
   指向一个根本不存在的目录 —— 而 `rm -rf` 删不到东西时**不报错**，
   用户以为清理完了，实际旧类还在，要到编译期才炸。

   反斜杠形态是**第二次踩的同一个坑**（本条当时只补了正斜杠就以为覆盖完了）：
   FramingVerify/CloseReasonChecks.cs 里的服务端源码探测路径是 C# 逐字字符串
   @"src\main\java\com\crm\sunrise\jdbcproxy\JdbcProxyWebSocketHandler.java"，
   被改成 .../cryptunnel/CryptunnelWebSocketHandler.java 后指向一个还不存在的
   文件（服务端仓库尚未更名）—— 于是 close reason 覆盖完整性比对
   **从 PASS 静默退化成永久 SKIP，退出码仍是 0**，输出里只剩一行「未找到」，
   看起来还很像「服务端不在本机」这种正常情况。丢了 12 项断言而无人察觉。

   教训：凡是**指向其它仓库**的路径字符串，都不该被本仓库的改名规则触碰。

3) 受保护数必须在「保护段之后」按占位符实际落地数统计，
   不能在原文上逐串 grep：短串是长串的前缀
   (com.crm.sunrise.jdbcproxy ⊂ com.crm.sunrise.jdbcproxy.JdbcProxyProperties)，
   在原文上单独数短串会把长串里的前缀一起数进去 -> 自检误报。

4) 全部改动先在内存里算完并自检，**全部通过才落盘**。
   避免 bash 版那种「改到一半被打断」的半完成状态。

5) 只改内容，不改文件名/目录名（后续步骤单独做，
   混在一起会让「替换了多少处」失去可核对性）。

用法：rename.py <文件清单> [--dry-run]
"""
import re
import sys
import os

# ---- 需要原样保留的历史字符串（越长越靠前，避免短串先命中吃掉长串）----
# 三种分隔符形态都要列：'.'（包名）、'/'（POSIX 路径）、'\'（Windows 路径 / C# 逐字字符串）。
# 漏掉任一种都会让指向 sunrise 仓库的路径被改成不存在的目标，且失败方式是静默的。
PROTECT = [
    'com.crm.sunrise.jdbcproxy.starter.JdbcProxyProperties',
    'com.crm.sunrise.jdbcproxy.JdbcProxyProperties',
    'com.crm.sunrise.jdbcproxy.starter',
    'com.crm.sunrise.jdbcproxy.core',
    'com.crm.sunrise.jdbcproxy',
    'com.crm.sunrise.proxy',
    'com/crm/sunrise/jdbcproxy',
    'com/crm/sunrise/proxy',
    # Windows / C# 逐字字符串形态（下面写的是**单个**反斜杠字符）
    'com\\crm\\sunrise\\jdbcproxy',
    'com\\crm\\sunrise\\proxy',
    # 服务端类名：即使包路径被拆开写，类名本身也不能改
    'JdbcProxyWebSocketHandler',
]

RULES = [
    ('JdbcProxy', 'Cryptunnel'),
    ('jdbc-proxy', 'cryptunnel'),
    ('jdbcproxy', 'cryptunnel'),
]

HIT = re.compile(r'JdbcProxy|jdbc-proxy|jdbcproxy')

SENTINEL = '\x01'


def hits(s):
    return len(HIT.findall(s))


def convert(text):
    """
    对一段文本执行「保护 -> 替换 -> 还原」，返回 (新文本, 受保护命中数)。

    抽成纯函数是为了可测：PROTECT 列表的正确性只能靠对照样本验证，
    而逻辑内联在 main() 里时唯一的验证方式是真跑一遍全仓替换 ——
    那种验证既不可重复也不可变异，等于没有验证。
    读的是模块级 PROTECT/RULES，故变异测试可临时替换它们。
    """
    work = text
    for i, p in enumerate(PROTECT):
        work = work.replace(p, '%sPROT%d%s' % (SENTINEL, i, SENTINEL))

    # 按占位符实际落地数统计受保护命中（见文件头说明 3）
    prot_hits = 0
    for i, p in enumerate(PROTECT):
        c = work.count('%sPROT%d%s' % (SENTINEL, i, SENTINEL))
        if c:
            prot_hits += c * hits(p)

    for old, new in RULES:
        work = work.replace(old, new)

    for i, p in enumerate(PROTECT):
        work = work.replace('%sPROT%d%s' % (SENTINEL, i, SENTINEL), p)

    return work, prot_hits


def main():
    if len(sys.argv) < 2:
        print('用法: rename.py <文件清单> [--dry-run]', file=sys.stderr)
        return 2
    list_path = sys.argv[1]
    dry = len(sys.argv) > 2 and sys.argv[2] == '--dry-run'

    with open(list_path, 'r', encoding='utf-8') as fh:
        files = [ln.strip() for ln in fh if ln.strip()]

    total_before = total_after = total_prot = 0
    pending = []      # (path, new_text)
    failures = []
    skipped_binary = []
    missing = []      # 清单里列了但磁盘上找不到的

    for f in files:
        if not os.path.isfile(f):
            # ⚠ 绝不能静默 continue。
            #    实测踩过：清单路径被 MSYS 转义损坏（\ -> //）后所有文件都
            #    "不存在"，脚本却报告「改动 0 个文件」并 exit 0 —— 一份
            #    彻头彻尾的假成功报告。找不到文件是**硬错误**，必须终止。
            missing.append(f)
            continue
        try:
            with open(f, 'r', encoding='utf-8') as fh:
                text = fh.read()
        except UnicodeDecodeError:
            # 非 UTF-8 文件不猜编码：猜错会静默损坏内容
            skipped_binary.append(f)
            continue

        before = hits(text)
        total_before += before
        if before == 0:
            continue

        if dry:
            print('%6d  %s' % (before, f))
            continue

        # --- 段 1~3：保护 -> 替换 -> 还原（抽为 convert() 以便单测/变异验证）---
        work, prot_hits = convert(text)

        # --- 自检 1：占位符必须还原干净 ---
        if SENTINEL in work:
            failures.append((f, '占位符残留'))
            continue

        after = hits(work)

        # --- 自检 2：剩余命中数必须恰好等于受保护串的贡献 ---
        #     不等 => 要么没替换成功，要么误伤了该保留的内容
        if after != prot_hits:
            failures.append((f, '替换后剩 %d 处，预期 %d 处受保护' % (after, prot_hits)))
            continue

        total_after += after
        total_prot += prot_hits
        pending.append((f, work))

    if dry:
        print('--- DRY RUN：共 %d 处待替换 ---' % total_before)
        if missing:
            print('!! 清单中 %d 个文件不存在' % len(missing), file=sys.stderr)
            return 1
        return 0

    # 清单里的文件找不到 —— 硬错误，整体不落盘
    if missing:
        print('!! 清单中 %d 个文件在磁盘上不存在，**未写入任何文件**：' % len(missing),
              file=sys.stderr)
        for f in missing[:10]:
            print('   %s' % f, file=sys.stderr)
        if len(missing) > 10:
            print('   ...（共 %d 个）' % len(missing), file=sys.stderr)
        return 1

    # 有任何自检失败就整体不落盘 —— 半完成状态比完全没做更难收拾
    if failures:
        print('!! 自检失败 %d 个文件，**未写入任何文件**：' % len(failures), file=sys.stderr)
        for f, why in failures:
            print('   %s  (%s)' % (f, why), file=sys.stderr)
        return 1

    # 一处都没改 = 异常，不允许报成功
    # （替换脚本"什么都没做"却 exit 0，是最容易被当成完成的失败形态）
    if not pending:
        print('!! 没有任何文件被改动 —— 清单为空或规则未命中，视为失败', file=sys.stderr)
        return 1

    for f, new_text in pending:
        with open(f, 'w', encoding='utf-8', newline='') as fh:
            fh.write(new_text)

    print('-' * 44)
    print('改动文件      : %d' % len(pending))
    print('替换前命中    : %d 处' % total_before)
    print('替换后剩余    : %d 处' % total_after)
    print('其中受保护    : %d 处（历史包名，有意保留）' % total_prot)
    print('实际替换      : %d 处' % (total_before - total_after))
    if skipped_binary:
        print('跳过非UTF8    : %d 个文件（需人工确认）' % len(skipped_binary))
        for f in skipped_binary:
            print('   %s' % f)
    print('-' * 44)
    return 0


if __name__ == '__main__':
    sys.exit(main())
