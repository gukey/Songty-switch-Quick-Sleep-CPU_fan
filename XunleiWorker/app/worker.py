"""串行处理队列，解析可重试，提交结果不明时人工核对。"""
import logging
import threading
import time
import pyperclip
from app.selection import ParseError, extract_magnets
from app.xunlei_api import APIError, SubmissionUncertain

log = logging.getLogger(__name__)


class Worker:
    def __init__(self, config, db, api, notifier=None):
        self.config, self.db, self.api = config, db, api
        self.notifier = notifier
        self.stop_event = threading.Event()
        self.wake = threading.Event()
        self.started_at = time.time()
        self.health = {"ok": None, "message": "正在连接迅雷", "latency_ms": 0}
        self.clipboard_error = ""
        self.paused = False
        self.threads = []
        self.last_clipboard = None
        self.clipboard_lock = threading.Lock()
        self.baseline_pending = True

    def set_clipboard_enabled(self, enabled):
        with self.clipboard_lock:
            # The first read after resuming establishes a baseline, never a task.
            self.baseline_pending = bool(enabled)
            self.config.set_clipboard(enabled)

    def start(self):
        for target in (self.run, self.monitor):
            thread = threading.Thread(target=target, daemon=True)
            thread.start()
            self.threads.append(thread)

    def process(self, task):
        start = time.monotonic()
        submitting = False
        try:
            selection = self.api.resolve(task["link"])
            self.db.selection(task["id"], selection)
            if not selection["selected"]:
                minimum = selection.get("min_video_size_mb", 200)
                self.db.finish(task["id"], "skipped", f"没有严格大于 {minimum:g}MB 的主视频，未创建下载")
                return
            if self.stop_event.is_set():
                self.db.finish(task["id"], "retry", "程序已停止，尚未提交", delay=0)
                return
            self.db.submitting(task["id"])
            submitting = True
            remote_id = self.api.submit(task["link"], selection)
            self.db.finish(task["id"], "imported", "已仅提交主视频", remote_id, round((time.monotonic()-start)*1000))
            if self.notifier:
                try:
                    self.notifier.success(selection)
                except Exception:
                    log.warning("桌面通知发送失败，任务已成功提交")
        except SubmissionUncertain as exc:
            self.db.finish(task["id"], "review", str(exc))
        except (APIError, ParseError) as exc:
            retry = not submitting and task["attempts"] < 3
            self.db.finish(task["id"], "retry" if retry else "failed", str(exc), delay=30*2**(task["attempts"]-1) if retry else 0)
        except Exception:
            # 不记录请求对象，避免认证参数进入日志。
            log.error("任务 %s 处理异常", task["id"])
            self.db.finish(task["id"], "review" if submitting else "failed", "处理异常，请检查本地日志和迅雷任务状态")

    def run(self):
        next_check = 0
        while not self.stop_event.is_set():
            try:
                if time.monotonic() >= next_check:
                    self.health = self.api.health()
                    next_check = time.monotonic()+30
                task = None if self.paused else self.db.claim()
                if task:
                    self.process(task)
                    continue
            except Exception:
                log.error("队列处理异常，稍后继续")
            self.wake.wait(1)
            self.wake.clear()

    def monitor(self):
        while not self.stop_event.wait(1):
            if not self.config.data["clipboard_enabled"]:
                continue
            self.poll_clipboard()

    def poll_clipboard(self):
        with self.clipboard_lock:
            if not self.config.data["clipboard_enabled"]:
                return
            try:
                text = pyperclip.paste()
                self.clipboard_error = ""
                if self.baseline_pending:
                    self.last_clipboard = text
                    self.baseline_pending = False
                    return
                if text != self.last_clipboard:
                    if extract_magnets(text):
                        result = self.db.add_text(text, "clipboard")
                        log.info("剪贴板检测：新增 %s，重复跳过 %s，无效 %s", result["added"], result["duplicates"], result["invalid"])
                        self.wake.set()
                    self.last_clipboard = text
            except Exception:
                self.clipboard_error = "剪贴板暂不可读"

    def stop(self):
        self.stop_event.set()
        self.wake.set()
