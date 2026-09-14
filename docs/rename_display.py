#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
补充替换：带空格的「显示名」变体。

为什么需要第二个脚本：
    rename.py 处理的是标识符形态（JdbcProxy / jdbc-proxy / jdbcproxy），
    但产品名在给人看的地方是带空格写的 —— pom 的 <name>、日志前缀、
    文档标题、UI 文案。这些不含上面任何一种形态，规则完全够不着，
    结果就是「代码全改完了，用户看到的还是旧品牌」。

    实测漏网 58 处，其中 34 处是日志前缀 "JDBC Proxy: ..." ——
    而 docs/sunrise-integration.md 里写着验收标准
    「启动日志含 Jdbc Proxy: WebSocket endpoint registered at ...」，
    两边不一致会让验收步骤直接失效。

分类处置（不能一刀切）：
    A. 产品名   "JDBC Proxy" / "Jdbc Proxy"     -> "Cryptunnel"
    B. 中文描述 "JDBC 代理隧道" -> "Cryptunnel 隧道"
                "JDBC 代理"     -> "Cryptunnel"
       中文这两条必须长的在前，否则 "JDBC 代理" 会先吃掉 "JDBC 代理隧道"
       的前半截，剩一个孤零零的「隧道」。

用法：rename_display.py <文件清单> [--dry-run]
"""
import io
import os
import re
import sys

# 顺序敏感：长串必须在短串之前
RULES = [
    ('JDBC 代理隧道', 'Cryptunnel 隧道'),
    ('JDBC 代理', 'Cryptunnel'),
    ('JDBC Proxy', 'Cryptunnel'),
    ('Jdbc Proxy', 'Cryptunnel'),
    ('JDBC-Proxy', 'Cryptunnel'),
    ('JDBC_Proxy', 'Cryptunnel'),
]

HIT = re.compile(r'JDBC[ _-]Proxy|Jdbc[ _]Proxy|JDBC 代理')


def main():
    if len(sys.argv) < 2:
        print('用法: rename_display.py <文件清单> [--dry-run]', file=sys.stderr)
        return 2
    dry = len(sys.argv) > 2 and sys.argv[2] == '--dry-run'

    files = [l.strip() for l in io.open(sys.argv[1], encoding='utf-8') if l.strip()]

    missing = []
    pending = []
    total_before = total_after = 0

    for f in files:
        if not os.path.isfile(f):
            missing.append(f)          # 硬错误，不静默跳过
            continue
        try:
            text = io.open(f, encoding='utf-8').read()
        except UnicodeDecodeError:
            missing.append(f + '  (非UTF-8)')
            continue

        before = len(HIT.findall(text))
        total_before += before
        if before == 0:
            continue
        if dry:
            print('%4d  %s' % (before, f))
            continue

        work = text
        for old, new in RULES:
            work = work.replace(old, new)

        after = len(HIT.findall(work))
        # 这批没有需要保护的历史串，替换后必须归零
        if after != 0:
            print('!! 自检失败: %s 替换后仍剩 %d 处' % (f, after), file=sys.stderr)
            return 1
        total_after += after
        pending.append((f, work))

    if missing:
        print('!! %d 个文件不存在或无法读取，**未写入任何文件**' % len(missing), file=sys.stderr)
        for m in missing:
            print('   %s' % m, file=sys.stderr)
        return 1

    if dry:
        print('--- DRY RUN：共 %d 处待替换 ---' % total_before)
        return 0

    if not pending:
        print('!! 没有任何文件被改动，视为失败', file=sys.stderr)
        return 1

    for f, new_text in pending:
        io.open(f, 'w', encoding='utf-8', newline='').write(new_text)

    print('-' * 44)
    print('改动文件      : %d' % len(pending))
    print('替换前命中    : %d 处' % total_before)
    print('替换后剩余    : %d 处' % total_after)
    print('实际替换      : %d 处' % (total_before - total_after))
    print('-' * 44)
    return 0


if __name__ == '__main__':
    sys.exit(main())
