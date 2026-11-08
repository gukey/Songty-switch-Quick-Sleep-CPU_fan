import json
import sys
import threading
import time

from fan_controller import FanControllerApp


def emit(cpu, gpu, port, connected, error=None):
    print(json.dumps({
        "cpu": cpu, "gpu": gpu, "port": port, "connected": connected, "error": error,
    }, allow_nan=False), flush=True)


def stop_when_parent_closes(app):
    # The parent holds the input pipe open. EOF also handles a crashed parent.
    sys.stdin.buffer.read()
    app.stop_event.set()


def run(probe):
    app = FanControllerApp()
    if not app.reader.open():
        emit(None, None, None, False, "温度传感器初始化失败")
        return 1
    try:
        if probe:
            cpu, gpu = app.reader.read_temperatures()
            emit(cpu, gpu, None, False)
            return 0 if cpu is not None or gpu is not None else 1

        threading.Thread(target=stop_when_parent_closes, args=(app,), daemon=True).start()
        while not app.stop_event.is_set():
            now = time.monotonic()
            gap = now - app.last_loop_monotonic
            app.last_loop_monotonic = now
            if gap >= app.wake_gap_seconds:
                emit(None, None, None, False)
                app.handle_wake_resume(gap)
            cpu, gpu = app.reader.read_temperatures()
            app.ensure_serial_connected()
            app.serial_bridge.send_temperatures(cpu, gpu)
            ack = app.serial_bridge.last_ack_monotonic
            connected = app.serial_bridge.connected and ack > 0 and time.monotonic() - ack < app.ack_timeout_seconds
            emit(cpu, gpu, app.serial_bridge.port, connected)
            app.stop_event.wait(app.update_interval)
    finally:
        app.serial_bridge.disconnect()
        app.reader.close()
    return 0


if __name__ == "__main__":
    sys.exit(run("--probe" in sys.argv))
