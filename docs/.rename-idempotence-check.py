#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全仓幂等性核查：对每个源码文件调一次 convert()，凡是「还会被改动」的都列出来。

为什么需要：convert() 是从 main() 里抽出来的，抽取动作本身可能引入行为漂移；
而改名已经执行完毕，正确状态下再跑一遍应当**一处都不该变**。
任何"还会变"的文件都要人工判断是遗漏、还是有意保留的规则左侧/史实。

不落盘，只报告。跑法：python docs/.rename-idempotence-check.py
"""
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

# 只扫真正参与编译/运行的源码与配置；文档另有一套判断标准（史实/规则左侧）
CODE_EXT = {'.cs', '.java', '.xml', '.yml', '.yaml', '.json', '.props', '.csproj',
            '.slnx', '.sh', '.ps1', '.sln'}

SKIP_DIRS = {'bin', 'obj', 'target', '.git', 'node_modules', '__pycache__',
             '.workbuddy', 'out', 'out-reverse', '.vs'}

# ---- 有意保留旧名的文件（每条都写清理由，不写理由不许加）----
#
# 这份白名单的存在本身有风险：它会把"真遗漏"也一起放过。
# 所以规则是 —— 只按**文件**豁免，且必须同时给出预期处数；
# 处数一变就重新报警，避免有人往豁免文件里悄悄加新的漏改。
EXPECTED_KEEP = {
    # 规则左侧：bash 版改名脚本自身的 RULES 定义，改了脚本就失效
    r'docs\rename.sh': 16,

    # 测试输入值：这一节验的就是"httpBasePath 可配成旧服务端的 /jdbc-proxy"，
    # 把输入值改成新名，被验证的性质本身就消失了。
    r'dotnet\build\verify\FramingVerify\HttpBasePathChecks.cs': 4,

    # 文档注释举例："对接尚未更名的服务端时填 httpBasePath: /jdbc-proxy"。
    # 这里的旧名是给用户抄的**目标服务端**路径，不是本仓库的名字。
    r'dotnet\src\Cryptunnel.Core\Models\ConfigModels.cs': 1,

    # 打包产物（历史版本的发布快照），不是源码。wsPath: /ws-jdbc-proxy 是
    # 当时真实发出去的默认值，改了就等于篡改已发布内容。
    r'packaging\dist\staging-portable\samples\00-defaults.yaml': 1,
    r'packaging\dist\staging-portable-1.2.12\samples\00-defaults.yaml': 1,
}


def load_rename():
    spec = importlib.util.spec_from_file_location(
        'rename_mod', os.path.join(HERE, 'rename.py'))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main():
    mod = load_rename()
    changed = []
    scanned = 0

    for root, dirs, files in os.walk(REPO):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for fn in files:
            if os.path.splitext(fn)[1].lower() not in CODE_EXT:
                continue
            path = os.path.join(root, fn)
            try:
                with open(path, 'r', encoding='utf-8') as fh:
                    text = fh.read()
            except (UnicodeDecodeError, OSError):
                continue
            scanned += 1
            if mod.hits(text) == 0:
                continue
            new, prot = mod.convert(text)
            if new != text:
                rel = os.path.relpath(path, REPO)
                # 统计实际会变的命中数
                changed.append((rel, mod.hits(text) - mod.hits(new)))

    print('扫描源码/配置文件：%d 个' % scanned)

    # 分成三类：预期内保留 / 意外出现 / 豁免项处数漂移
    unexpected = []
    drifted = []
    matched = []
    for rel, n in changed:
        if rel in EXPECTED_KEEP:
            if EXPECTED_KEEP[rel] == n:
                matched.append((rel, n))
            else:
                drifted.append((rel, n, EXPECTED_KEEP[rel]))
        else:
            unexpected.append((rel, n))

    # 白名单里列了、但实际已经没有旧名了 —— 说明豁免过期，也要报
    stale = [rel for rel in EXPECTED_KEEP
             if rel not in dict(changed)]

    if matched:
        print()
        print('有意保留（处数与预期一致）：')
        for rel, n in sorted(matched):
            print('   %4d 处  %s' % (n, rel))

    ok = True

    if unexpected:
        ok = False
        print()
        print('!! 意外残留 %d 个文件（不在豁免名单里，需人工判断）：' % len(unexpected))
        for rel, n in sorted(unexpected, key=lambda x: -x[1]):
            print('   %4d 处  %s' % (n, rel))

    if drifted:
        ok = False
        print()
        print('!! 豁免文件处数漂移 %d 个（可能被悄悄加进了新的漏改）：' % len(drifted))
        for rel, n, want in drifted:
            print('   %s：实际 %d 处，预期 %d 处' % (rel, n, want))

    if stale:
        ok = False
        print()
        print('!! 豁免名单已过期 %d 条（文件里已无旧名，应删掉这条豁免）：' % len(stale))
        for rel in sorted(stale):
            print('   %s' % rel)

    print()
    if ok:
        print('=== 幂等：全部残留均在豁免名单内且处数吻合 ===')
        return 0
    return 1


if __name__ == '__main__':
    sys.exit(main())
