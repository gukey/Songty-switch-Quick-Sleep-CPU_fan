"""由统一托盘宿主管理的迅雷服务，不创建托盘或自启动项。"""
import argparse
import json
import logging
import sys
import threading
from pathlib import Path
from waitress import create_server
from app.config import Config
from app.database import Database
from app.dashboard import create_app
from app.instance import acquire_instance_lock
from app.worker import Worker
from app.xunlei_api import XunleiAPI


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data-dir', required=True)
    args = parser.parse_args()
    config = Config(Path(args.data_dir))
    lock = acquire_instance_lock(config.directory)
    if lock is None:
        raise RuntimeError('迅雷数据目录正在被其他实例使用。')
    logs = config.resolve_path('log_directory')
    logs.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(filename=logs / 'app.log', level=logging.INFO,
                        format='%(asctime)s %(levelname)s %(message)s')
    db = Database(config.resolve_path('database_path'))
    worker = Worker(config, db, XunleiAPI(config))
    app = create_app(config, db, worker)
    # Fail before starting workers when an old console owns the port.
    server = create_server(app, host='127.0.0.1', port=config.data['port'], threads=4)
    stopping = threading.Event()
    output_lock = threading.Lock()

    def emit(value):
        with output_lock:
            print(json.dumps(value, ensure_ascii=False), flush=True)

    def report():
        emit({'ready': True, 'port': config.data['port'],
              'listening': config.data['clipboard_enabled']})

    class Notifications:
        def success(self, selection):
            file = selection['selected']
            emit({'notification': str(file.get('name', '主视频已提交迅雷'))})

    worker.notifier = Notifications()

    def commands():
        try:
            for line in sys.stdin:
                command = json.loads(line)
                if command.get('action') == 'stop':
                    break
                if command.get('action') == 'listen':
                    worker.set_clipboard_enabled(command['enabled'])
                    report()
        except Exception:
            logging.exception('宿主控制通道中断')
        finally:
            stopping.set()

    worker.start()
    threading.Thread(target=commands, daemon=True).start()
    report()
    last_listening = config.data['clipboard_enabled']
    try:
        while not stopping.is_set():
            server.asyncore.loop(timeout=0.25, count=1, map=server._map)
            if last_listening != config.data['clipboard_enabled']:
                last_listening = config.data['clipboard_enabled']
                report()
    finally:
        worker.stop()
        for thread in worker.threads:
            thread.join(timeout=3)
        server.task_dispatcher.shutdown(timeout=1)
        server.asyncore.close_all(map=server._map)
        lock.close()


if __name__ == '__main__':
    main()
