#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
rename.py 的 PROTECT 定点变异验证。

复刻真实事故现场：FramingVerify/CloseReasonChecks.cs 里指向 sunrise 仓库的
C# 逐字字符串路径，被改名脚本替换成一个还不存在的文件名，导致
close reason 覆盖比对从 PASS 静默退化成永久 SKIP（退出码仍是 0，丢了 12 项断言）。

验证两件事：
  1) 正向：现在的 PROTECT 能保住反斜杠形态的 sunrise 路径 + 服务端类名。
  2) 变异（反向）：把补进去的三条 PROTECT 摘掉，同一样本必须被改坏 ——
     否则说明这三条其实没起作用，测试是假的。

跑法：python docs/.rename-protect-selftest.py
"""
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def load_rename():
    """把 rename.py 作为模块载入（文件名不是合法标识符，走 spec 方式）。"""
    path = os.path.join(HERE, 'rename.py')
    spec = importlib.util.spec_from_file_location('rename_mod', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# 事故现场原文（C# 逐字字符串里就是单个反斜杠）
SAMPLE = r'''
        @"D:\Sunrise\Coding\CRM\sunrise",
    private const string HandlerRelativePath =
        @"src\main\java\com\crm\sunrise\jdbcproxy\JdbcProxyWebSocketHandler.java";
    // POSIX 形态也要保住
    // rm -rf src/main/java/com/crm/sunrise/jdbcproxy
    // 包名形态
    // com.crm.sunrise.jdbcproxy.JdbcProxyProperties
    // 下面这些是本仓库自己的名字，必须被改掉
    var cfg = new JdbcProxyConfig();   // -> CryptunnelConfig
    const string Key = "jdbc-proxy.cipher";   // -> cryptunnel.cipher
    using io.github.objectyan.jdbcproxy.core;  // -> cryptunnel.core
'''

MUST_KEEP = [
    r'com\crm\sunrise\jdbcproxy',
    'JdbcProxyWebSocketHandler',
    'com/crm/sunrise/jdbcproxy',
    'com.crm.sunrise.jdbcproxy.JdbcProxyProperties',
]

MUST_CHANGE = [
    ('JdbcProxyConfig', 'CryptunnelConfig'),
    ('jdbc-proxy.cipher', 'cryptunnel.cipher'),
    ('objectyan.jdbcproxy.core', 'objectyan.cryptunnel.core'),
]

# 变异实验要摘掉的三条（本次补丁新增的）
MUTATE_REMOVE = [
    'com\\crm\\sunrise\\jdbcproxy',
    'com\\crm\\sunrise\\proxy',
    'JdbcProxyWebSocketHandler',
]


def run():
    mod = load_rename()
    convert = getattr(mod, 'convert', None)
    if convert is None:
        print('！rename.py 里找不到 convert()，请核对函数名')
        return 2

    passed = failed = 0

    def check(label, ok):
        nonlocal passed, failed
        if ok:
            passed += 1
            print('  [PASS] ' + label)
        else:
            failed += 1
            print('  [FAIL] ' + label)

    # ---------- 1) 正向：当前 PROTECT 必须保住这些 ----------
    print('[1] 当前 PROTECT 能保住指向 sunrise 仓库的路径与类名')
    out, _ = convert(SAMPLE)
    for s in MUST_KEEP:
        check('保留 ' + s, s in out)

    print()
    print('[2] 本仓库自己的名字必须照常被改（保护不能过度）')
    for old, new in MUST_CHANGE:
        check(old + ' -> ' + new, new in out and old not in out)

    # ---------- 3) 变异：摘掉补丁后必须失败 ----------
    print()
    print('[3] 变异验证：摘掉本次补进的 3 条 PROTECT 后，样本必须被改坏')
    original = list(mod.PROTECT)
    mod.PROTECT = [p for p in original if p not in MUTATE_REMOVE]
    removed = len(original) - len(mod.PROTECT)
    check('确实摘掉了 3 条（否则变异实验本身无效）', removed == 3)

    mutated, _ = convert(SAMPLE)
    mod.PROTECT = original  # 还原

    check(r'反斜杠形态被改坏（com\crm\sunrise\cryptunnel 出现）',
          'com\\crm\\sunrise\\cryptunnel' in mutated)
    check('服务端类名被改坏（CryptunnelWebSocketHandler 出现）',
          'CryptunnelWebSocketHandler' in mutated)
    check('正斜杠形态仍被保住（那条 PROTECT 没摘，不应受影响）',
          'com/crm/sunrise/jdbcproxy' in mutated)

    print()
    print('=== 通过 {} 项，失败 {} 项 ==='.format(passed, failed))
    return 0 if failed == 0 else 1


if __name__ == '__main__':
    sys.exit(run())
