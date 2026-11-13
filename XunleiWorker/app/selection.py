"""磁力链接规范化和主视频选择，未知数据不允许整包下载。"""
import base64
import re
from pathlib import PurePosixPath
from urllib.parse import parse_qs, urlparse

MIN_BYTES = 200 * 1024 * 1024
VIDEO_EXTENSIONS = {".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ts", ".m2ts", ".mts", ".asf", ".ogv", ".rmvb", ".rm", ".vob", ".ogm", ".divx"}


class ParseError(Exception):
    pass


def magnet_info(link):
    link = link.strip()
    parsed = urlparse(link)
    if parsed.scheme.lower() != "magnet":
        raise ValueError("只接受 magnet 磁力链接")
    query = parse_qs(parsed.query)
    for xt in query.get("xt", []):
        if xt.lower().startswith("urn:btih:"):
            value = xt[9:]
            if re.fullmatch(r"[0-9a-fA-F]{40}", value):
                btih = value.upper()
            elif re.fullmatch(r"[A-Z2-7a-z]{32}", value):
                btih = base64.b32decode(value.upper()).hex().upper()
            else:
                continue
            return {"link": link, "btih": btih, "title": query.get("dn", [btih])[0]}
    raise ValueError("磁力链接缺少有效的 BTIH")


def extract_magnets(text):
    return re.findall(r"magnet:\?[^\s<>\"']+", text, flags=re.I)


def select_main(root, rules=None):
    rules = rules or {}
    threshold = rules.get("min_video_size_mb", 200)
    if type(threshold) not in (int, float) or not 0 < threshold <= 1048576:
        raise ParseError("视频大小限制无效，请检查规则配置")
    extensions = {x.lower() for x in rules.get("video_extensions", VIDEO_EXTENSIONS)}
    keywords = rules.get("sample_keywords", ["sample", "trailer", "preview", "预告", "花絮"])
    files = []

    def walk(node):
        directory = node.get("dir") or {}
        if directory.get("next_page_token"):
            raise ParseError("文件列表尚未完整解析")
        if node.get("is_dir") or "resources" in directory:
            for child in directory.get("resources", []):
                walk(child)
        else:
            try:
                size = int(node["file_size"])
                # 迅雷的 protobuf JSON 省略值为 0 的 file_index。
                index = int(node.get("file_index", 0))
                if size < 0 or index < 0:
                    raise ValueError()
            except (KeyError, TypeError, ValueError):
                raise ParseError("文件大小或索引无效，未提交下载")
            files.append({"name": node.get("name", ""), "size": size, "index": index})

    walk(root)
    expected = int(root.get("file_count") or 0)
    if not files or (expected and expected != len(files)):
        raise ParseError("文件清单不完整，未提交下载")
    if len({f["index"] for f in files}) != len(files):
        raise ParseError("文件索引重复，未提交下载")
    candidates = []
    for file in files:
        if PurePosixPath(file["name"].replace("\\", "/")).suffix.lower() not in extensions:
            file["reason"] = "非视频"
        elif rules.get("skip_samples", True) and any(re.search(r"(?:^|[\W_])"+re.escape(k)+r"(?:$|[\W_])", file["name"], re.I) if k.isascii() else k in file["name"] for k in keywords):
            file["reason"] = "样片或预告"
        elif file["size"] <= threshold * 1024 * 1024:
            file["reason"] = f"不大于 {threshold:g}MB"
        else:
            candidates.append(file)
    chosen = max(candidates, key=lambda f: (f["size"], -f["index"]), default=None)
    for file in candidates:
        file["reason"] = "主视频" if file is chosen else "非主视频"
    return {"root": root, "files": files, "selected": chosen, "min_video_size_mb": threshold}
