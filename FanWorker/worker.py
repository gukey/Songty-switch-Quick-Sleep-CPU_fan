import json
import sys
import threading
import time
import ctypes
import os

from fan_controller import FanControllerApp
from sensor_process import SensorProcess


def emit(cpu, gpu, port, connected, error=None, sample_age=None, ack_age=None, heartbeat_age=None):
    print(json.dumps({
        "cpu": cpu, "gpu": gpu, "port": port, "connected": connected, "error": error,
        "sampleAge": sample_age, "ackAge": ack_age,
        "heartbeatAge": heartbeat_age,
    }, allow_nan=False), flush=True)


def stop_when_parent_closes(app):
    # The parent holds the input pipe open. EOF also handles a crashed parent.
    sys.stdin.buffer.read()
    app.stop_event.set()


def run(probe, sensor_stream=False):
    app = FanControllerApp()
    app.update_interval = 1.0
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
            app.serial_bridge.send_temperatures(cpu, gpu)
            app.serial_bridge.send_heartbeat()
            app.serial_bridge.poll_incoming()
            ack = app.serial_bridge.last_ack_monotonic
            pong = app.serial_bridge.last_pong_monotonic
            response = max(ack, pong)
            connected = app.serial_bridge.connected and response > 0 and time.monotonic() - response < app.ack_timeout_seconds
            error = '温度采集延迟或通道缺失，仅发送有效实测温度' if cpu is None or gpu is None else None
            now = time.monotonic()
            sample_age = round(max(0, now - sensors.updated), 3) if sensors.updated else None
            ack_age = round(max(0, now - ack), 3) if ack else None
            heartbeat_age = round(max(0, now - pong), 3) if pong else None
            emit(cpu, gpu, app.serial_bridge.port, connected, error, sample_age, ack_age, heartbeat_age)
            app.stop_event.wait(app.update_interval)
    finally:
        app.serial_bridge.disconnect()
        sensors.close()
    return 0


if __name__ == "__main__":
    sys.exit(run("--probe" in sys.argv, '--sensor-stream' in sys.argv))
