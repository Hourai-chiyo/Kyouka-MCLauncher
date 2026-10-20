# -*- coding: utf-8 -*-
"""
mcroute_gui.py —— mcroute 的图形界面（tkinter，零依赖，不用装任何东西）

运行方式：
    python mcroute_gui.py

设计原则：
    1. 所有耗时操作（下载、启动）都丢到后台线程，界面不会卡死
    2. 命令行那套逻辑一行都不重写，直接复用 mcroute 模块
       （所以 CLI 和 GUI 永远是同一份行为，不会两边跑偏）
    3. 输出统一进底部日志框
"""

import argparse
import os
import queue
import re
import sys
import threading
import tkinter as tk
from tkinter import messagebox, ttk

import mcroute


class Catcher:
    """把 CLI 里 print 的内容接进 GUI 的日志框"""

    def __init__(self, sink):
        self.sink = sink

    def write(self, text):
        if text and text.strip():
            self.sink(text.rstrip())

    def flush(self):
        pass


def autodetect_java():
    """
    按顺序找一个能用的 java.exe：
      1. mcdata/java_versions（以后如果自己做 Java 管理会放这里）
      2. Modrinth App 的 java 目录（你这台机器上有）
      3. 最后退回 PATH 里的 java
    找到多个时，优先用名字里带 jre21/jdk21 的（1.21 需要 Java 21）。
    """
    found = []

    for base in (
        os.path.join(mcroute.DEFAULT_DATA_DIR, "java_versions"),
        os.path.join(os.environ.get("APPDATA", ""), "ModrinthApp", "meta", "java_versions"),
    ):
        if not os.path.isdir(base):
            continue
        for name in sorted(os.listdir(base)):
            exe = os.path.join(base, name, "bin", "java.exe")
            if os.path.isfile(exe):
                found.append(exe)

    if not found:
        return "java"

    for path in found:
        if "jre21" in path or "jdk21" in path:
            return path
    return found[-1]


class App:
    def __init__(self, root):
        self.root = root
        self.q = queue.Queue()
        self.busy = False

        root.title("mcroute — Minecraft 启动器")
        root.geometry("980x620")
        root.minsize(820, 520)

        self._build_ui()
        self.refresh_local()
        self.refresh_account()
        self.root.after(120, self._poll)

    # ------------------------------------------------------------------ 界面
    def _build_ui(self):
        # ---- 顶部：账号 ----
        top = ttk.Frame(self.root, padding=(10, 8, 10, 4))
        top.pack(fill="x")

        self.account_label = ttk.Label(top, text="...")
        self.account_label.pack(side="left")

        ttk.Button(top, text="退出登录", command=self.do_logout).pack(side="right", padx=(6, 0))
        ttk.Button(top, text="登录", command=self.do_login).pack(side="right")
        ttk.Separator(self.root, orient="horizontal").pack(fill="x")

        # ---- 中部：左边版本列表，右边操作区 ----
        mid = ttk.Frame(self.root, padding=(10, 8))
        mid.pack(fill="both", expand=True)

        left = ttk.Frame(mid)
        left.pack(side="left", fill="both", expand=True)

        ttk.Label(left, text="本地已安装版本").pack(anchor="w")
        self.listbox = tk.Listbox(left, height=12, exportselection=False)
        self.listbox.pack(fill="both", expand=True, pady=(4, 6))

        row = ttk.Frame(left)
        row.pack(fill="x")
        ttk.Button(row, text="刷新列表", command=self.refresh_local).pack(side="left")
        ttk.Button(row, text="启动选中版本", command=self.do_launch).pack(side="left", padx=6)

        right = ttk.Frame(mid)
        right.pack(side="left", fill="y", padx=(12, 0))

        ttk.Label(right, text="下载版本").pack(anchor="w")
        self.ver_entry = ttk.Entry(right, width=24)
        self.ver_entry.pack(anchor="w", pady=(4, 4))
        self.ver_entry.insert(0, "1.21.1")

        self.skip_assets = tk.BooleanVar(value=False)
        ttk.Checkbutton(right, text="跳过资源（快，但没有音效贴图）",
                        variable=self.skip_assets).pack(anchor="w")

        ttk.Button(right, text="开始下载", command=self.do_download).pack(anchor="w", pady=(6, 0))

        ttk.Separator(right, orient="horizontal").pack(fill="x", pady=10)

        ttk.Label(right, text="Java 路径").pack(anchor="w")
        self.java_entry = ttk.Entry(right, width=44)
        self.java_entry.pack(anchor="w", pady=(4, 0), fill="x")
        self.java_entry.insert(0, autodetect_java())

        # ---- 底部：日志 ----
        ttk.Separator(self.root, orient="horizontal").pack(fill="x")
        bottom = ttk.Frame(self.root, padding=(10, 6))
        bottom.pack(fill="both")

        # ---- 进度条：下载时显示 x/y ----
        prog_row = ttk.Frame(bottom)
        prog_row.pack(fill="x", pady=(0, 4))
        self.progress = ttk.Progressbar(prog_row, mode="determinate", maximum=100, value=0)
        self.progress.pack(side="left", fill="x", expand=True)
        self.progress_label = ttk.Label(prog_row, text="", width=22)
        self.progress_label.pack(side="left", padx=(8, 0))

        ttk.Label(bottom, text="日志").pack(anchor="w")
        self.log_box = tk.Text(bottom, height=9, wrap="word")
        self.log_box.pack(fill="both", expand=True, pady=(4, 0))

    # ------------------------------------------------------------------ 日志
    def log(self, text):
        self.q.put(text)

    PROGRESS_RE = re.compile(r"进度\s+(\d+)/(\d+)")

    def _poll(self):
        while True:
            try:
                line = self.q.get_nowait()
            except queue.Empty:
                break
            self.log_box.insert("end", line + "\n")
            self.log_box.see("end")
            self._update_progress(line)
        self.root.after(120, self._poll)

    def _update_progress(self, line):
        """从日志里认出「进度 200/3982」这样的行，同步到进度条"""
        match = self.PROGRESS_RE.search(line)
        if not match:
            return
        done = int(match.group(1))
        total = max(int(match.group(2)), 1)
        self.progress.configure(maximum=total, value=done)
        self.progress_label.config(
            text="{}/{}   {}%".format(done, total, int(done * 100 / total))
        )

    def _reset_progress(self):
        self.progress.configure(maximum=100, value=0)
        self.progress_label.config(text="")

    # ------------------------------------------------------------------ 状态
    def refresh_local(self):
        self.listbox.delete(0, "end")
        vroot = os.path.join(mcroute.DEFAULT_DATA_DIR, "versions")
        if not os.path.isdir(vroot):
            self.log("还没有本地版本，先在右边输入版本号下载（例如 1.21.1）。")
            return
        names = [n for n in sorted(os.listdir(vroot))
                 if os.path.isdir(os.path.join(vroot, n))]
        for name in names:
            self.listbox.insert("end", name)
        self.log("找到 {} 个本地版本".format(len(names)))

    def refresh_account(self):
        auth = mcroute.auth_load()
        if auth and auth.get("name"):
            self.account_label.config(text="账号：" + str(auth.get("name")) + "（在线）")
        else:
            self.account_label.config(text="未登录 · 离线模式（可单机游玩）")

    # ------------------------------------------------------------------ 任务
    def _run(self, func, ns, done=None):
        """在后台线程里跑一个 CLI 函数，输出进日志框"""
        if self.busy:
            messagebox.showinfo("稍等", "上一个任务还没结束")
            return
        self.busy = True

        def worker():
            old_stdout = sys.stdout
            sys.stdout = Catcher(self.q.put)
            try:
                func(ns)
            except Exception as exc:
                self.q.put("[异常] " + str(exc))
            finally:
                sys.stdout = old_stdout
                self.busy = False
                if done:
                    self.root.after(0, done)

        threading.Thread(target=worker, daemon=True).start()

    def do_login(self):
        self._run(mcroute.cmd_login, argparse.Namespace(), done=self.refresh_account)

    def do_logout(self):
        self._run(mcroute.cmd_logout, argparse.Namespace(), done=self.refresh_account)

    def do_download(self):
        ver = self.ver_entry.get().strip()
        if not ver:
            messagebox.showinfo("提示", "先填一个版本号，例如 1.21.1")
            return
        ns = argparse.Namespace(
            version=ver,
            dir=mcroute.DEFAULT_DATA_DIR,
            workers=16,
            no_assets=bool(self.skip_assets.get()),
        )
        self._reset_progress()
        self.log("开始下载 {} ...".format(ver))
        self._run(mcroute.cmd_fetch, ns, done=self.refresh_local)

    def do_launch(self):
        picked = self.listbox.curselection()
        if not picked:
            messagebox.showinfo("提示", "先在左边选一个版本")
            return
        ver = self.listbox.get(picked[0])
        java = self.java_entry.get().strip() or "java"
        ns = argparse.Namespace(
            version=ver,
            dir=mcroute.DEFAULT_DATA_DIR,
            java=java,
            game_dir=None,
            username="Player",
            force=False,
            wait=False,
        )
        self.log("启动 {} ...".format(ver))
        self._run(mcroute.cmd_launch, ns)


def main():
    # 让窗口在高分屏上不糊（失败就算了，不影响功能）
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass

    root = tk.Tk()
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
