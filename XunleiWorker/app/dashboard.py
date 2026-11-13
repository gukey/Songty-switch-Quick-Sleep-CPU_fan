"""全部页面使用同一本地 SQLite 数据源，无外部前端依赖。"""
import csv
import hmac
import io
import time
from urllib.parse import urlsplit
from pathlib import Path
from flask import Flask, abort, jsonify, render_template, request, Response, send_file


def create_app(config, db, worker):
    app = Flask(__name__)
    app.config["MAX_CONTENT_LENGTH"] = 2 * 1024 * 1024

    @app.before_request
    def local_only():
        hostname = urlsplit("http://"+request.host).hostname
        if hostname not in ("localhost", "127.0.0.1", "::1"):
            abort(403)
        if request.method == "POST":
            if request.headers.get("Origin") and request.headers["Origin"] != request.host_url.rstrip("/"):
                abort(403)
            if not hmac.compare_digest(request.headers.get("X-Action-Token", ""), config.token):
                abort(403)

    @app.after_request
    def headers(response):
        response.headers["Cache-Control"] = "no-store"
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "DENY"
        response.headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'"
        response.headers["X-XunleiAutoImport"] = "4"
        return response

    @app.errorhandler(ValueError)
    def invalid(exc):
        return jsonify(error=str(exc)), 400

    @app.errorhandler(413)
    def too_large(exc):
        return jsonify(error="文本过大，单次最多 2MB"), 413

    @app.get("/")
    def index():
        return render_template("index.html", token=config.token)

    @app.get("/icon.svg")
    def icon():
        return send_file(Path(__file__).parent / "assets" / "app.svg", mimetype="image/svg+xml")

    @app.get("/api/status")
    def status():
        result = db.summary()
        result.update(api=worker.health, api_url=config.data["api_url"], clipboard=config.data["clipboard_enabled"],
                      clipboard_error=worker.clipboard_error, paused=worker.paused,
                      uptime=int(time.time()-worker.started_at), version="4.0.0",
                      database=str(db.path), config=str(config.path), download_path=worker.api.download_path,
                      rules=config.rules, auto_start=config.data["auto_start"])
        return jsonify(result)

    @app.get("/api/tasks")
    def tasks():
        return jsonify(db.records(request.args.get("status", ""), request.args.get("q", "")[:300], max(1, int(request.args.get("page", 1)))))

    @app.get("/api/tasks/<int:task_id>")
    def task(task_id):
        result = db.task(task_id)
        if result is None:
            abort(404)
        return jsonify(result)

    @app.post("/api/tasks")
    def add():
        body = request.get_json() or {}
        text = body.get("text", "")
        if not isinstance(text, str):
            raise ValueError("请输入文本")
        result = db.add_text(text, "file" if body.get("source") == "file" else "web")
        worker.wake.set()
        return jsonify(result)

    @app.post("/api/tasks/<int:task_id>/retry")
    def retry(task_id):
        db.retry(task_id, (request.get_json() or {}).get("reviewed") is True)
        worker.wake.set()
        return jsonify(ok=True)

    @app.post("/api/clipboard")
    def clipboard():
        enabled = (request.get_json() or {}).get("enabled")
        if not isinstance(enabled, bool):
            raise ValueError("监听状态必须是布尔值")
        worker.set_clipboard_enabled(enabled)
        return jsonify(ok=True)

    @app.post("/api/pause")
    def pause():
        worker.paused = (request.get_json() or {}).get("paused") is True
        worker.wake.set()
        return jsonify(ok=True)

    @app.post("/api/rules")
    def rules():
        body = request.get_json()
        if not isinstance(body, dict) or set(body) != {"min_video_size_mb"}:
            raise ValueError("请填写视频大小限制，单位 MB")
        try:
            config.set_minimum_size(body["min_video_size_mb"])
        except OSError:
            return jsonify(error="规则文件保存失败，请检查目录是否可写"), 500
        return jsonify(ok=True, rules=config.rules)

    @app.get("/api/export")
    def export():
        stream = io.StringIO()
        writer = csv.writer(stream)
        writer.writerow(["编号", "名称", "状态", "磁力链接", "主视频", "字节数", "迅雷任务编号", "说明", "创建时间"])
        with db.lock:
            rows = db.conn.execute("SELECT id,title,status,link,selected_name,selected_size,remote_id,error,created_at FROM tasks ORDER BY id").fetchall()
        for row in rows:
            writer.writerow(["'"+v if isinstance(v, str) and v.startswith(("=", "+", "-", "@", "\t", "\r")) else v for v in row])
        return Response("\ufeff"+stream.getvalue(), mimetype="text/csv", headers={"Content-Disposition": "attachment; filename=tasks.csv"})

    return app
