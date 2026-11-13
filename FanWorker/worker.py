import json
import sys
import threading
import time
import ctypes
import os

from fan_controller import FanControllerApp
from sensor_process import SensorProcess


def emit(cpu, gpu, port, connected, error=None):
    print(json.dumps({
        "cpu": cpu, "gpu": gpu, "port": port, "connected": connected, "error": error,
    }, allow_nan=False), flush=True)


def stop_when_parent_closes(app):
    # The parent holds the input pipe open. EOF also handles a crashed parent.
    sys.stdin.buffer.read()
    app.stop_event.set()


def run(probe, sensor_stream=False):
    app = FanControllerApp()
    if probe or sensor_stream:
        if not app.reader.open():
            emit(None, None, None, False, "温度传感器初始化失败")
            return 1
        if sensor_stream:
            threading.Thread(target=stop_when_parent_closes, args=(app,), daemon=True).start()
        try:
            while not app.stop_event.is_set():
                cpu, gpu = app.reader.read_temperatures()
                emit(cpu, gpu, None, False)
                if probe:
                    return 0 if cpu is not None or gpu is not None else 1
                app.stop_event.wait(app.update_interval)
        finally:
            app.reader.close()
        return 0
    command = [sys.executable]
    if os.name == 'nt':
        kernel = ctypes.windll.kernel32
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        kernel.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x8000)
    if not getattr(sys, 'frozen', False):
        command.append(__file__)
    sensors = SensorProcess(command + ['--sensor-stream'])
    try:
        threading.Thread(target=stop_when_parent_closes, args=(app,), daemon=True).start()
        while not app.stop_event.is_set():
            try:
                cpu, gpu = sensors.poll()
            except OSError as error:
                print(f'Sensor process restart failed: {error}', flush=True)
                cpu, gpu = None, None
            app.ensure_serial_connected()
            app.serial_bridge.send_temperatures(cpu if cpu is not None else 120,
                                                gpu if gpu is not None else 120)
            ack = app.serial_bridge.last_ack_monotonic
            connected = app.serial_bridge.connected and ack > 0 and time.monotonic() - ack < app.ack_timeout_seconds
            error = '温度采集失效，缺失通道已发送120°C满速保护值（非实测温度）' if cpu is None or gpu is None else None
            emit(cpu, gpu, app.serial_bridge.port, connected, error)
            app.stop_event.wait(app.update_interval)
    finally:
        app.serial_bridge.disconnect()
        sensors.close()
    return 0


if __name__ == "__main__":
    sys.exit(run("--probe" in sys.argv, '--sensor-stream' in sys.argv))
