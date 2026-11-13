"""Windows 单实例锁：先获取锁，不读取其他进程已锁定的字节。"""
import errno
import msvcrt
from pathlib import Path


def acquire_instance_lock(directory):
    handle = open(Path(directory) / ".instance.lock", "a+b")
    try:
        handle.seek(0)
        # Windows 允许锁定文件末尾之外的字节，无需提前读写初始化。
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError as exc:
        handle.close()
        if exc.errno in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
            return None
        raise
    return handle
