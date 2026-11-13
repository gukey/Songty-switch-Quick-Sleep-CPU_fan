"""单一本地任务库；导入旧库只读，不覆盖原始历史。"""
import json
import sqlite3
import threading
import time
from contextlib import closing
from datetime import datetime
from pathlib import Path
from app.selection import extract_magnets, magnet_info


def now():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


class Database:
    def __init__(self, path, recover=True):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.lock = threading.RLock()
        self.conn = sqlite3.connect(self.path, check_same_thread=False, timeout=10)
        self.conn.row_factory = sqlite3.Row
        self.conn.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS tasks(
                id INTEGER PRIMARY KEY, btih TEXT UNIQUE NOT NULL, link TEXT NOT NULL, title TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'queued', selected_name TEXT, selected_size INTEGER,
                selected_index INTEGER, total_files INTEGER, remote_id TEXT, attempts INTEGER DEFAULT 0,
                next_retry REAL DEFAULT 0, error TEXT, source TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS task_queue ON tasks(status,next_retry);
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY, task_id INTEGER, result TEXT NOT NULL, message TEXT,
                selected_name TEXT, selected_size INTEGER, duration_ms INTEGER DEFAULT 0,
                created_at TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'current');
            CREATE INDEX IF NOT EXISTS event_date ON events(created_at);
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY, task_id INTEGER, name TEXT, size INTEGER, file_index INTEGER, reason TEXT);
            CREATE TABLE IF NOT EXISTS duplicates(id INTEGER PRIMARY KEY, task_id INTEGER, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS legacy_records(
                table_name TEXT, row_id INTEGER, data_json TEXT NOT NULL, PRIMARY KEY(table_name,row_id));
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT);
        """)
        if recover:
            with self.conn:
                self.conn.execute("UPDATE tasks SET status='queued' WHERE status='parsing'")
                self.conn.execute("UPDATE tasks SET status='review',error='上次提交被中断，请先在迅雷核对结果' WHERE status='submitting'")

    def add_text(self, text, source="web"):
        links = extract_magnets(text)
        if len(links) > 500:
            raise ValueError("单次最多导入 500 条磁力链接")
        result = {"added": 0, "duplicates": 0, "invalid": 0}
        with self.lock, self.conn:
            for link in links:
                try:
                    info = magnet_info(link)
                except ValueError:
                    result["invalid"] += 1
                    continue
                existing = self.conn.execute("SELECT id FROM tasks WHERE btih=?", (info["btih"],)).fetchone()
                if existing:
                    self.conn.execute("INSERT INTO duplicates(task_id,created_at) VALUES (?,?)", (existing[0], now()))
                    result["duplicates"] += 1
                    continue
                self.conn.execute("INSERT INTO tasks(btih,link,title,source,created_at,updated_at) VALUES (?,?,?,?,?,?)",
                                  (info["btih"], link, info["title"], source, now(), now()))
                result["added"] += 1
        if not links:
            raise ValueError("没有找到磁力链接，请粘贴 magnet:? 开头的链接")
        return result

    def claim(self):
        with self.lock, self.conn:
            row = self.conn.execute("SELECT * FROM tasks WHERE status IN ('queued','retry') AND next_retry<=? ORDER BY id LIMIT 1", (time.time(),)).fetchone()
            if row:
                self.conn.execute("UPDATE tasks SET status='parsing',attempts=attempts+1,updated_at=? WHERE id=?", (now(), row["id"]))
                task = dict(row)
                task["attempts"] += 1
                return task

    def selection(self, task_id, selection):
        chosen = selection["selected"] or {}
        with self.lock, self.conn:
            self.conn.execute("DELETE FROM files WHERE task_id=?", (task_id,))
            self.conn.executemany("INSERT INTO files(task_id,name,size,file_index,reason) VALUES (?,?,?,?,?)",
                                  [(task_id, f["name"], f["size"], f["index"], f["reason"]) for f in selection["files"]])
            self.conn.execute("UPDATE tasks SET selected_name=?,selected_size=?,selected_index=?,total_files=?,title=? WHERE id=?",
                              (chosen.get("name"), chosen.get("size"), chosen.get("index"), len(selection["files"]), selection["root"].get("name", ""), task_id))

    def submitting(self, task_id):
        with self.lock, self.conn:
            self.conn.execute("UPDATE tasks SET status='submitting',updated_at=? WHERE id=?", (now(), task_id))

    def finish(self, task_id, status, message="", remote_id=None, duration_ms=0, delay=0):
        with self.lock, self.conn:
            self.conn.execute("UPDATE tasks SET status=?,error=?,remote_id=COALESCE(?,remote_id),next_retry=?,updated_at=? WHERE id=?",
                              (status, message, remote_id, time.time()+delay, now(), task_id))
            self.conn.execute("""INSERT INTO events(task_id,result,message,selected_name,selected_size,duration_ms,created_at)
                SELECT id,?,?,selected_name,selected_size,?,? FROM tasks WHERE id=?""",
                (status, message, duration_ms, now(), task_id))

    def retry(self, task_id, reviewed=False):
        with self.lock, self.conn:
            task = self.task(task_id)
            if not task:
                raise ValueError("任务不存在")
            if task["status"] == "review" and not reviewed:
                raise ValueError("请先确认迅雷中没有重复任务，再重试")
            if task["status"] not in ("failed", "retry", "review", "skipped"):
                raise ValueError("当前任务状态不允许重试")
            self.conn.execute("UPDATE tasks SET status='queued',attempts=0,next_retry=0,error='',updated_at=? WHERE id=?", (now(), task_id))

    def task(self, task_id):
        with self.lock:
            row = self.conn.execute("SELECT * FROM tasks WHERE id=?", (task_id,)).fetchone()
            if row:
                result = dict(row)
                result["files"] = [dict(f) for f in self.conn.execute("SELECT * FROM files WHERE task_id=? ORDER BY file_index", (task_id,))]
                return result

    def records(self, status="", search="", page=1):
        with self.lock:
            where, args = [], []
            if status:
                where.append("status=?")
                args.append(status)
            if search:
                where.append("(title LIKE ? OR link LIKE ? OR selected_name LIKE ?)")
                args.extend(["%"+search+"%"]*3)
            clause = " WHERE " + " AND ".join(where) if where else ""
            count = self.conn.execute("SELECT COUNT(*) FROM tasks"+clause, args).fetchone()[0]
            rows = self.conn.execute("SELECT * FROM tasks"+clause+" ORDER BY id DESC LIMIT 20 OFFSET ?", args+[(page-1)*20]).fetchall()
            return {"items": [dict(r) for r in rows], "total": count, "page": page}

    def summary(self):
        with self.lock:
            counts = {r[0]: r[1] for r in self.conn.execute("SELECT status,COUNT(*) FROM tasks GROUP BY status")}
            day = {r[0]: r[1] for r in self.conn.execute("SELECT result,COUNT(*) FROM events WHERE created_at>=datetime('now','-1 day','localtime') GROUP BY result")}
            success, failed = day.get("imported", 0), day.get("failed", 0)
            return {"counts": counts, "total": sum(counts.values()), "day_success": success, "day_failed": failed,
                    "success_rate": round(success/(success+failed)*100, 1) if success+failed else None,
                    "duplicates": self.conn.execute("SELECT COUNT(*) FROM duplicates").fetchone()[0],
                    "day_duplicates": self.conn.execute("SELECT COUNT(*) FROM duplicates WHERE created_at>=datetime('now','-1 day','localtime')").fetchone()[0],
                    "filters": {r[0]: r[1] for r in self.conn.execute("SELECT reason,COUNT(*) FROM files WHERE reason!='主视频' GROUP BY reason")},
                    "recent_events": [dict(r) for r in self.conn.execute("SELECT e.*,t.title FROM events e LEFT JOIN tasks t ON t.id=e.task_id ORDER BY e.created_at DESC,e.id DESC LIMIT 20")],
                    "legacy_records": self.conn.execute("SELECT COUNT(*) FROM legacy_records").fetchone()[0]}

    def import_legacy(self, source):
        """完整存档旧表，并映射显示记录；旧库始终只读。"""
        source = Path(source)
        if not source.exists() or source.resolve() == self.path.resolve():
            return
        with self.lock:
            if self.conn.execute("SELECT 1 FROM metadata WHERE key='legacy_imported'").fetchone():
                return
            with closing(sqlite3.connect(source.resolve().as_uri()+"?mode=ro", uri=True)) as old:
                old.row_factory = sqlite3.Row
                names = {r[0] for r in old.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                old.execute("BEGIN")
                tables = {name: [dict(r) for r in old.execute('SELECT * FROM "'+name+'"')]
                          for name in ("tasks", "torrent_tasks", "import_events", "filter_events", "duplicate_events") if name in names}
            with self.conn:
                for name, rows in tables.items():
                    self.conn.executemany("INSERT OR IGNORE INTO legacy_records VALUES (?,?,?)", [(name, r["id"], json.dumps(r, ensure_ascii=False)) for r in rows])
                mapping = {}
                for row in tables.get("tasks", []):
                    try:
                        info = magnet_info(row["link"])
                    except ValueError:
                        # 保留旧版 ED2K 记录，但不进入新磁力提交队列。
                        info = {"btih": "legacy:"+str(row["id"]), "title": row["link"], "link": row["link"]}
                    status = {1: "imported", 3: "failed"}.get(row.get("status"), "review")
                    self.conn.execute("INSERT OR IGNORE INTO tasks(btih,link,title,status,error,source,created_at,updated_at) VALUES (?,?,?,?,?,?,?,?)",
                                      (info["btih"], info["link"], info["title"], status, row.get("last_error") or row.get("error_message") or "", "legacy", row.get("created_at") or now(), row.get("updated_at") or now()))
                    mapping[row["id"]] = self.conn.execute("SELECT id FROM tasks WHERE btih=?", (info["btih"],)).fetchone()[0]
                for row in tables.get("import_events", []):
                    result = {"SUCCESS": "imported", "FAILED": "failed", "RETRY": "retry"}.get(str(row.get("result", "")).upper(), "review")
                    task_id = mapping.get(row.get("task_id")) if row.get("task_kind", "link") != "torrent" else None
                    self.conn.execute("INSERT INTO events(task_id,result,message,selected_name,duration_ms,created_at,source) VALUES (?,?,?,?,?,?,?)",
                                      (task_id, result, row.get("error_message") or row.get("message") or "旧版提交记录", row.get("task_name"), row.get("duration_ms", 0), row.get("created_at") or now(), "legacy"))
                for row in tables.get("duplicate_events", []):
                    self.conn.execute("INSERT INTO duplicates(task_id,created_at) VALUES (?,?)", (mapping.get(row.get("task_id")), row.get("created_at") or now()))
                self.conn.execute("INSERT INTO metadata VALUES ('legacy_imported',?)", (str(source),))

    def backup(self, destination):
        with self.lock, closing(sqlite3.connect(destination)) as copy:
            self.conn.backup(copy)

    def close(self):
        with self.lock:
            self.conn.close()
