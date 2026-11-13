"""本地配置；不向浏览器暴露凭据。"""
import json
import secrets
import threading
from copy import deepcopy
from pathlib import Path
from urllib.parse import urlsplit
from app.selection import VIDEO_EXTENSIONS

DEFAULTS = {"api_url": "http://192.168.1.5", "api_username": "", "api_password": "",
            "download_path": "", "port": 8787, "clipboard_enabled": True, "auto_start": True,
            "database_path": "data/tasks.db", "log_directory": "logs", "legacy_database": ""}
DEFAULT_RULES = {"min_video_size_mb": 200, "video_extensions": sorted(VIDEO_EXTENSIONS),
                 "skip_samples": True, "sample_keywords": ["sample", "trailer", "preview", "samplevideo", "预告", "花絮"]}


class Config:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.path = self.directory / "settings.json"
        self.rules_path = self.directory / "rules.json"
        self.lock = threading.RLock()
        self.data = deepcopy(DEFAULTS)
        if self.path.exists():
            self.data.update(json.loads(self.path.read_text(encoding="utf-8")))
        self.rules = deepcopy(DEFAULT_RULES)
        if self.rules_path.exists():
            self.rules.update(json.loads(self.rules_path.read_text(encoding="utf-8")))
        self.validate(self.data, self.rules)
        self.token = secrets.token_urlsafe(32)
        if not self.path.exists():
            self.save()
        if not self.rules_path.exists():
            self.rules_path.write_text(json.dumps(self.rules, ensure_ascii=False, indent=2), encoding="utf-8")

    @staticmethod
    def validate(data, rules):
        if urlsplit(data["api_url"]).scheme not in ("http", "https"):
            raise ValueError("api_url 必须是 HTTP 或 HTTPS 地址")
        if not isinstance(data["port"], int) or not 1 <= data["port"] <= 65535:
            raise ValueError("port 必须是 1 至 65535 的整数")
        for key in ("clipboard_enabled", "auto_start"):
            if not isinstance(data[key], bool):
                raise ValueError(key+" 必须为 true 或 false")
        if type(rules["min_video_size_mb"]) not in (int, float) or not 0 < rules["min_video_size_mb"] <= 1048576:
            raise ValueError("主视频大小限制必须大于 0 且不超过 1048576 MB")
        extensions = rules["video_extensions"]
        if not isinstance(extensions, list) or not extensions or any(not isinstance(x, str) or x.lower() not in VIDEO_EXTENSIONS for x in extensions):
            raise ValueError("video_extensions 只能填写支持的视频扩展名")
        if not isinstance(rules["skip_samples"], bool) or not isinstance(rules["sample_keywords"], list) or any(not isinstance(x, str) or not x.strip() for x in rules["sample_keywords"]):
            raise ValueError("样片过滤规则格式不正确")

    def reload(self):
        data, rules = deepcopy(DEFAULTS), deepcopy(DEFAULT_RULES)
        data.update(json.loads(self.path.read_text(encoding="utf-8")))
        rules.update(json.loads(self.rules_path.read_text(encoding="utf-8")))
        self.validate(data, rules)
        for key in ("port", "database_path", "log_directory"):
            if data[key] != self.data[key]:
                raise ValueError("端口、数据库或日志目录有变化，请退出并重新运行 EXE")
        with self.lock:
            self.data, self.rules = data, rules

    def resolve_path(self, key):
        path = Path(self.data[key])
        return path if path.is_absolute() else self.directory / path

    def save(self):
        with self.lock:
            temp = self.path.with_suffix(".tmp")
            temp.write_text(json.dumps(self.data, ensure_ascii=False, indent=2), encoding="utf-8")
            temp.replace(self.path)

    def set_clipboard(self, enabled):
        with self.lock:
            self.data["clipboard_enabled"] = bool(enabled)
            self.save()

    def set_minimum_size(self, value):
        with self.lock:
            rules = deepcopy(DEFAULT_RULES)
            rules.update(json.loads(self.rules_path.read_text(encoding="utf-8")))
            rules["min_video_size_mb"] = value
            self.validate(self.data, rules)
            temp = self.rules_path.with_suffix(".tmp")
            temp.write_text(json.dumps(rules, ensure_ascii=False, indent=2), encoding="utf-8")
            temp.replace(self.rules_path)
            self.rules = rules
