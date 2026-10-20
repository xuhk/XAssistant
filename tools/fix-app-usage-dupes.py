#!/usr/bin/env python3
"""清掉 app_usage.db 里「同一次连续运行被记了两遍」的重复会话行。

背景（2026-09-29 实测）
--------------------
ProcessUsageTracker.Stop() 在程序退出时会把活跃会话按 EndTime 关掉；下次启动
CaptureExistingProcesses() 又拿进程的 **OS 启动时间** 当新会话的开始时间插一条 ——
于是同一个 StartTime 出现两行，当天时长按两遍算。
实测 chrome 08:23:40 启动、10:41:35 退出程序、10:41:35 再启动：
两行分别是 08:23:40→10:41:35（8,274 秒）和 08:23:40→11:47:29（12,229 秒）。

采集端的修复见 Services/ProcessUsageTracker.cs（_resumableSessions / OpenSessionFor / ResumeSession）。
这个脚本只负责收拾已经写进库里的重复行。

判据
----
同一个 (ProcessName, StartTime) 出现多行 = 同一次运行被记了多遍（同一个进程实例不可能
有两次相同的毫秒级启动时间）。**保留累计秒数最大的那一条**（它一直记到最晚的结束时间），
其余删掉。

用法
----
    python tools/fix-app-usage-dupes.py            # 干跑，只打印
    python tools/fix-app-usage-dupes.py --apply    # 备份后真的删

只动开发库（%APPDATA%\\XAssistant_Dev\\app_usage.db）。生产库一律不碰。
"""

from __future__ import annotations

import os
import shutil
import sqlite3
import sys
from collections import defaultdict
from datetime import datetime

APPLY = "--apply" in sys.argv

APPDATA = os.environ.get("APPDATA") or os.path.expanduser("~")
DEV_DIR = os.path.join(APPDATA, "XAssistant_Dev")
DB = os.path.join(DEV_DIR, "app_usage.db")

GROUPS_SQL = """
SELECT Id, ProcessName, StartTime, EndTime, AccumulatedSeconds, Date
FROM ProcessSession
WHERE (ProcessName, StartTime) IN (
    SELECT ProcessName, StartTime
    FROM ProcessSession
    GROUP BY ProcessName, StartTime
    HAVING COUNT(*) > 1
)
"""


def main() -> int:
    if not os.path.isfile(DB):
        print(f"[FAIL] 找不到开发库：{DB}")
        return 1

    con = sqlite3.connect(f"file:{DB.replace(os.sep, '/')}?mode=ro", uri=True)
    rows = con.execute(GROUPS_SQL).fetchall()
    con.close()

    groups: dict[tuple[str, str], list[tuple]] = defaultdict(list)
    for r in rows:
        groups[(r[1], r[2])].append(r)

    if not groups:
        print("没有重复行，不用处理。")
        return 0

    doomed: list[tuple] = []
    print(f"== 重复组 {len(groups)} 个 ==")
    for (name, start), items in sorted(groups.items(), key=lambda kv: kv[1][0][5], reverse=True):
        keep = max(items, key=lambda r: r[4] or 0)
        print(f"\n  {name}  start={start}  （{items[0][5]}）")
        for r in items:
            mark = "保留" if r is keep else "删除"
            if r is not keep:
                doomed.append(r)
            end = r[3] or "（未结束）"
            print(f"    [{mark}] Id={r[0]:<6} {r[2]} -> {end}  {((r[4] or 0) / 3600):6.2f}h")

    print(
        f"\n待删 {len(doomed)} 行，重复计入的时长合计 "
        f"{sum((r[4] or 0) for r in doomed) / 3600:.2f}h"
    )

    if not APPLY:
        print("\n（干跑结束，什么都没改。加 --apply 才落库）")
        return 0

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_dir = os.path.join(APPDATA, f"XAssistant_Dev.bak-{stamp}")
    os.makedirs(backup_dir, exist_ok=True)
    for suffix in ("", "-wal", "-shm"):
        src = DB + suffix
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(backup_dir, os.path.basename(src)))
    print(f"\n备份完成 -> {backup_dir}")

    con = sqlite3.connect(DB)
    try:
        with con:  # 事务：要么全删，要么一条不动
            for r in doomed:
                cur = con.execute("DELETE FROM ProcessSession WHERE Id = ?", (r[0],))
                if cur.rowcount != 1:
                    raise RuntimeError(f"Id={r[0]} 期望删 1 行，实际 {cur.rowcount} 行")
    finally:
        con.close()

    con = sqlite3.connect(f"file:{DB.replace(os.sep, '/')}?mode=ro", uri=True)
    left = con.execute(GROUPS_SQL).fetchall()
    total = con.execute("SELECT COUNT(*) FROM ProcessSession").fetchone()[0]
    con.close()

    print(f"已删除 {len(doomed)} 行；库内剩余 {total} 行；复查重复组剩 {len(left)} 组")
    if left:
        print("[FAIL] 还有残留重复组")
        return 1
    print("[OK] 复查干净")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
