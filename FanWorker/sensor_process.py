"""Hardware reads are isolated from the persistent serial worker."""
import json
import os
import subprocess
import sys
import threading
import time


class SensorProcess:
    def __init__(self, command, timeout=30):
        self.command = command
        self.timeout = timeout
        self.process = None
        self.started = 0
        self.sample = (None, None)
        self.updated = 0
        self.lock = threading.Lock()
        self.generation = 0
        self.lifecycle = threading.Lock()
        self.restarting = False
        self.closed = False

    def poll(self):
        now = time.monotonic()
        failed = self.process is None or self.process.poll() is not None or now - max(self.started, self.updated) > self.timeout
        if failed and not self.restarting and not self.closed:
            self.restarting = True
            self.generation += 1
            with self.lock:
                self.sample = (None, None)
                self.updated = 0
            threading.Thread(target=self._restart, daemon=True).start()
        with self.lock:
            if now - self.updated > 6:
                return None, None
            return self.sample

    def _restart(self):
        try:
            with self.lifecycle:
                if self.closed:
                    return
                if self.process is not None:
                    print(f'Sensor collector restart: exit={self.process.poll()}', file=sys.stderr, flush=True)
                self._close_process()
                if self.closed:
                    return
                self.started = time.monotonic()
                flags = subprocess.CREATE_NO_WINDOW | subprocess.NORMAL_PRIORITY_CLASS if os.name == 'nt' else 0
                self.process = subprocess.Popen(self.command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                                stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
                                                creationflags=flags, env=dict(os.environ, PYTHONUTF8='1'))
                threading.Thread(target=self._receive, args=(self.process, self.generation), daemon=True).start()
        except OSError as error:
            print(f'Sensor collector launch failed: {error}', file=sys.stderr, flush=True)
        finally:
            self.restarting = False

    def _receive(self, process, generation):
        try:
            for line in process.stdout:
                try:
                    sample = json.loads(line)
                    cpu, gpu = sample['cpu'], sample['gpu']
                    if any(value is not None and (not isinstance(value, (int, float)) or not 0 <= value <= 120)
                           for value in (cpu, gpu)):
                        continue
                    with self.lock:
                        if generation == self.generation:
                            self.sample = (cpu, gpu)
                            self.updated = time.monotonic()
                except (ValueError, KeyError, TypeError):
                    continue
        finally:
            process.stdout.close()

    def close(self):
        self.closed = True
        with self.lifecycle:
            self._close_process()

    def _close_process(self):
        process, self.process = self.process, None
        self.generation += 1
        with self.lock:
            self.sample = (None, None)
            self.updated = 0
        if process is None:
            return
        try:
            process.stdin.close()
            if process.poll() is None:
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/pid', str(process.pid), '/t', '/f'],
                                   capture_output=True, timeout=5, creationflags=subprocess.CREATE_NO_WINDOW)
                else:
                    process.kill()
            process.wait(timeout=5)
        except (OSError, subprocess.TimeoutExpired):
            pass
