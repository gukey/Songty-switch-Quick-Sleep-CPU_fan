"""按照目标迅雷网页的 resource/list 和 sub_file_index 协议提交。"""
import json
import re
import threading
import time
from urllib.parse import urljoin
import requests
from app.selection import ParseError, select_main


class APIError(Exception):
    pass


class SubmissionUncertain(APIError):
    pass


class XunleiAPI:
    def __init__(self, config):
        self.config = config
        self.session = requests.Session()
        self.session.trust_env = False
        self.lock = threading.RLock()
        self.base = ""
        self.target = ""
        self.download_path = ""
        self.auth_at = 0

    def authenticate(self):
        if self.target and time.monotonic() - self.auth_at < 240:
            return
        cfg = self.config.data
        self.session.auth = (cfg["api_username"], cfg["api_password"]) if cfg.get("api_username") else None
        try:
            response = self.session.get(cfg["api_url"], timeout=(5, 15))
            response.raise_for_status()
        except requests.RequestException:
            raise APIError("无法连接迅雷认证页面，请检查地址和凭据") from None
        match = re.search(r"function\s+uiauth\s*\([^)]*\)\s*\{[^}]*return\s+[\"']([^\"']+)", response.text)
        if not match:
            raise APIError("迅雷页面没有返回认证信息")
        self.base = response.url.split("?", 1)[0].rstrip("/") + "/"
        self.session.headers.update({"pan-auth": match.group(1), "Device-Space": "", "Accept": "application/json"})
        data = self._request("GET", "drive/v1/tasks", params={"type": "user#runner"})
        runners = [task for task in data.get("tasks", []) if task.get("params", {}).get("target")]
        if len(runners) != 1:
            raise APIError("未找到唯一可用的迅雷下载设备")
        params = runners[0]["params"]
        self.target = params["target"]
        paths = params.get("downloads") or []
        if isinstance(paths, str):
            paths = json.loads(paths)
        default = next((entry.get("path") for entry in paths if not entry.get("is_root_path")), "")
        self.download_path = cfg.get("download_path") or default
        if not self.download_path:
            raise APIError("迅雷没有可用的下载目录")
        self.auth_at = time.monotonic()

    def _request(self, method, path, *, params=None, body=None, submitting=False):
        query = dict(params or {})
        query.update({"pan_auth": self.session.headers.get("pan-auth", ""), "device_space": ""})
        try:
            response = self.session.request(method, urljoin(self.base, path), params=query, json=body, timeout=(5, 30))
            if response.status_code in (401, 403):
                self.auth_at = 0
            if response.status_code >= 500 and submitting:
                raise SubmissionUncertain("迅雷返回服务器错误，提交结果待核对；不会自动重复提交")
            response.raise_for_status()
            data = response.json()
        except requests.RequestException:
            if submitting:
                raise SubmissionUncertain("提交连接中断，结果待核对；请在迅雷确认后决定是否重试") from None
            raise APIError("迅雷接口请求失败，请稍后重试") from None
        except ValueError:
            if submitting:
                raise SubmissionUncertain("迅雷响应无法识别，提交结果待核对") from None
            raise APIError("迅雷返回了无法识别的数据") from None
        if not isinstance(data, dict):
            raise APIError("迅雷响应格式不正确")
        if data.get("error") or data.get("HttpStatus", 0) not in (0, "0", None):
            raise APIError("迅雷拒绝本次请求，请在迅雷网页检查设备和账号状态")
        return data

    def health(self):
        with self.lock:
            start = time.monotonic()
            try:
                self.auth_at = 0
                self.authenticate()
                return {"ok": True, "message": "认证与下载设备正常", "latency_ms": round((time.monotonic()-start)*1000)}
            except (APIError, ValueError):
                return {"ok": False, "message": "无法取得迅雷下载设备，请检查服务", "latency_ms": 0}

    def resolve(self, link):
        with self.lock:
            self.authenticate()
            data = self._request("POST", "drive/v1/resource/list", body={"page_size": 2000, "urls": link})
            roots = data.get("list", {}).get("resources", [])
            if len(roots) != 1:
                raise ParseError("未能解析出唯一磁力资源")
            seen = set()

            def complete(directory):
                token = directory.get("next_page_token")
                while token:
                    if token in seen or len(seen) >= 100 or not data.get("list_id"):
                        raise ParseError("文件分页异常，未提交下载")
                    seen.add(token)
                    page = self._request("GET", "drive/v1/resource/list/" + data["list_id"], params={"page_token": token})
                    listing = page.get("list") or {}
                    if not isinstance(listing.get("resources"), list):
                        raise ParseError("文件分页内容缺失")
                    directory.setdefault("resources", []).extend(listing["resources"])
                    token = listing.get("next_page_token")
                directory["next_page_token"] = ""
                for node in directory.get("resources", []):
                    if node.get("dir"):
                        complete(node["dir"])

            if data["list"].get("next_page_token"):
                raise ParseError("资源根列表不完整")
            if roots[0].get("dir"):
                complete(roots[0]["dir"])
            return select_main(roots[0], self.config.rules)

    def reload(self):
        with self.lock:
            self.auth_at = 0
            self.target = ""

    def payload(self, link, selection):
        chosen = selection["selected"]
        if chosen is None:
            raise ValueError("没有可提交的主视频")
        return {"type": "user#download-url", "name": selection["root"].get("name") or chosen["name"],
                "file_name": selection["root"].get("name") or chosen["name"], "file_size": str(chosen["size"]),
                "space": self.target, "params": {"target": self.target, "url": link,
                "total_file_count": str(len(selection["files"])), "parent_folder_id": "",
                "parent_folder_path": self.download_path, "sub_file_index": str(chosen["index"]), "mime_type": "", "file_id": ""}}

    def submit(self, link, selection):
        with self.lock:
            self.authenticate()
            data = self._request("POST", "drive/v1/task", body=self.payload(link, selection), submitting=True)
            task = data.get("task") or data.get("data") or data
            task_id = task.get("id") or task.get("task_id")
            if not task_id:
                raise SubmissionUncertain("迅雷未返回任务编号，提交结果待核对")
            return str(task_id)
