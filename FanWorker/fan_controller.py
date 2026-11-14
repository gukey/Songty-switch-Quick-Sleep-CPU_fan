# 温度采集与串口逻辑源自现有 CPU_fan PC 控制端。
import os
import sys
import threading
import time
import shutil
import subprocess
import secrets
from dataclasses import dataclass
from typing import List, Optional, Tuple

import serial
import serial.tools.list_ports

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
LIB_DIR = SCRIPT_DIR if getattr(sys, "frozen", False) else os.path.join(SCRIPT_DIR, "lib")
MIN_VALID_TEMP = 0.0
MAX_VALID_TEMP = 120.0


def log(message: str) -> None:
    # 发布版不在 EXE 旁生成日志文件；源码运行时仍可在控制台查看诊断信息。
    try:
        print(message)
    except OSError:
        pass


def normalize_port_name(port: str) -> str:
    return port.strip().upper()


@dataclass
class PortInfo:
    device: str
    description: str
    hwid: str
    manufacturer: str

    @property
    def search_blob(self) -> str:
        return " ".join([self.device, self.description, self.hwid, self.manufacturer]).lower()


def get_serial_ports() -> List[PortInfo]:
    ports: List[PortInfo] = []
    for port_info in serial.tools.list_ports.comports():
        if not port_info.device:
            continue
        ports.append(
            PortInfo(
                device=normalize_port_name(port_info.device),
                description=(port_info.description or "").strip(),
                hwid=(port_info.hwid or "").strip(),
                manufacturer=(getattr(port_info, "manufacturer", "") or "").strip(),
            )
        )
    return ports


class HardwareTemperatureReader:
    def __init__(self) -> None:
        self.computer = None
        self.backend_name = ""
        self.hardware_type = None
        self.sensor_type = None
        self.gpu_types: List[object] = []
        self.target_hardware: List[object] = []

    def _add_reference(self, dll_name: str) -> None:
        import clr
        dll_path = os.path.join(LIB_DIR, dll_name)
        if os.path.exists(dll_path):
            clr.AddReference(dll_path)
        else:
            clr.AddReference(os.path.splitext(dll_name)[0])

    def open(self) -> bool:
        try:
            if hasattr(os, "add_dll_directory"):
                try:
                    os.add_dll_directory(LIB_DIR)
                except Exception:
                    pass
            self._add_reference("HidSharp.dll")
            self._add_reference("RAMSPDToolkit-NDD.dll")
            self._add_reference("OpenHardwareMonitorLib.dll")
            from OpenHardwareMonitor.Hardware import Computer, HardwareType, SensorType
            self.backend_name = "OpenHardwareMonitorLib"

            self.hardware_type = HardwareType
            self.sensor_type = SensorType
            self.gpu_types = [
                hw for hw in [
                    getattr(HardwareType, "GpuNvidia", None),
                    getattr(HardwareType, "GpuAmd", None),
                    getattr(HardwareType, "GpuIntel", None),
                ] if hw is not None
            ]
            self.computer = Computer()
            self.computer.IsCpuEnabled = True
            # NVIDIA Update also queries power through NVML and has crashed under load.
            # Collect only CPU here; GPU temperature is queried in a separate CLI process.
            self.computer.IsGpuEnabled = False
            # portable=False 让 Ring0 驱动释放到系统临时目录，避免在 EXE 旁生成 .sys。
            self.computer.Open(False)
            self.target_hardware = [
                hw for hw in self.computer.Hardware
                if hw.HardwareType == self.hardware_type.Cpu or hw.HardwareType in self.gpu_types
            ]
            log(f"Hardware monitor initialized: {self.backend_name}.")
            return True
        except Exception as exc:
            log(f"Hardware monitor init failed: {exc}")
            self.computer = None
            self.target_hardware = []
            return False

    def close(self) -> None:
        if self.computer is not None:
            try:
                self.computer.Close()
            except Exception:
                pass
        self.computer = None
        self.target_hardware = []

    def reopen(self) -> bool:
        self.close()
        return self.open()

    def read_temperatures(self) -> Tuple[Optional[float], Optional[float]]:
        if not self.target_hardware:
            return None, None
        cpu_temp: Optional[float] = None
        gpu_temp: Optional[float] = None
        try:
            for hardware in self.target_hardware:
                hardware.Update()
                is_cpu = hardware.HardwareType == self.hardware_type.Cpu
                is_gpu = hardware.HardwareType in self.gpu_types
                for sensor in hardware.Sensors:
                    if sensor.SensorType != self.sensor_type.Temperature or sensor.Value is None:
                        continue
                    value = float(sensor.Value)
                    if not (MIN_VALID_TEMP <= value <= MAX_VALID_TEMP):
                        continue
                    if is_cpu:
                        cpu_temp = value if cpu_temp is None else max(cpu_temp, value)
                    elif is_gpu:
                        gpu_temp = value if gpu_temp is None else max(gpu_temp, value)
        except Exception as exc:
            log(f"Read temperature error: {exc}")
            return None, None
        tool = shutil.which("nvidia-smi")
        if tool:
            try:
                result = subprocess.run([tool, '--query-gpu=temperature.gpu', '--format=csv,noheader,nounits'],
                                        capture_output=True, text=True, timeout=3,
                                        creationflags=subprocess.CREATE_NO_WINDOW)
                values = []
                for line in result.stdout.splitlines():
                    try:
                        values.append(float(line.strip()))
                    except ValueError:
                        continue
                valid = [value for value in values if MIN_VALID_TEMP <= value <= MAX_VALID_TEMP]
                gpu_temp = max(valid) if valid else None
            except (OSError, subprocess.TimeoutExpired):
                pass
        return cpu_temp, gpu_temp


class SerialBridge:
    def __init__(self, preferred_port: str, baudrate: int, ack_timeout_seconds: float, port_scan_keywords: List[str], target_hwid_contains: str, target_description_contains: str) -> None:
        self.preferred_port = normalize_port_name(preferred_port)
        self.port = self.preferred_port
        self.baudrate = baudrate
        self.ack_timeout_seconds = ack_timeout_seconds
        self.port_scan_keywords = port_scan_keywords
        self.target_hwid_contains = target_hwid_contains
        self.target_description_contains = target_description_contains
        self.conn: Optional[serial.Serial] = None
        self.last_tx_monotonic = 0.0
        self.last_rx_monotonic = 0.0
        self.last_ack_monotonic = 0.0
        self.pending_since_monotonic = 0.0
        self.failed_ports_until = {}
        self.last_pong_monotonic = 0.0
        self.heartbeat_sent = 0.0
        self.heartbeat_token = None

    @property
    def connected(self) -> bool:
        return self.conn is not None and self.conn.is_open

    def disconnect(self) -> None:
        if self.conn is not None:
            try:
                if self.conn.is_open:
                    self.conn.close()
            except Exception:
                pass
        self.conn = None
        self.last_tx_monotonic = 0.0
        self.last_rx_monotonic = 0.0
        self.last_ack_monotonic = 0.0
        self.pending_since_monotonic = 0.0
        self.last_pong_monotonic = 0.0
        self.heartbeat_sent = 0.0
        self.heartbeat_token = None

    def is_auto_mode(self) -> bool:
        return self.preferred_port in {"", "AUTO"}

    def port_matches_target(self, port: PortInfo) -> bool:
        return (
            (self.target_hwid_contains and self.target_hwid_contains in port.hwid.lower())
            or (self.target_description_contains and self.target_description_contains in port.description.lower())
        )

    def candidate_ports(self) -> List[PortInfo]:
        ports = get_serial_ports()
        if not self.is_auto_mode():
            # Explicitly selected output port must not fall through to debug/input ports.
            now = time.monotonic()
            return [port for port in ports if port.device == self.preferred_port
                    and self.failed_ports_until.get(port.device, 0.0) <= now]
        # 配对设备的固定蓝牙地址在出站 SPP 设备路径中；入站口没有此地址。
        # 不扫描 USB、SOL 或其他已配对的蓝牙设备。
        if self.target_hwid_contains:
            now = time.monotonic()
            return sorted([port for port in ports
                           if 'bthenum\\' in port.hwid.lower()
                           and '{00001101-0000-1000-8000-00805f9b34fb}' in port.hwid.lower()
                           and self.target_hwid_contains.lower() + '_' in port.hwid.lower()
                           and self.failed_ports_until.get(port.device, 0.0) <= now],
                          key=lambda port: port.device != self.port)
        targeted = [port for port in ports if self.port_matches_target(port)]
        preferred = [port for port in ports if port.device == self.preferred_port]
        matched = [
            port for port in ports
            if port not in targeted
            and port.device != self.preferred_port
            and any(keyword in port.search_blob for keyword in self.port_scan_keywords)
        ]
        ordered: List[PortInfo] = []
        seen = set()
        for group in ([preferred, targeted, matched, ports] if not self.is_auto_mode() else [targeted, matched, ports]):
            for port in group:
                if port.device in seen:
                    continue
                ordered.append(port)
                seen.add(port.device)
        now = time.monotonic()
        return [port for port in ordered if self.failed_ports_until.get(port.device, 0.0) <= now]

    def mark_port_temporarily_failed(self, port: str) -> None:
        normalized_port = normalize_port_name(port)
        if normalized_port and normalized_port != "AUTO":
            cooldown = 3.0 if self.is_auto_mode() or normalized_port == self.preferred_port else max(30.0, self.ack_timeout_seconds * 3.0)
            self.failed_ports_until[normalized_port] = time.monotonic() + cooldown
            log(f"Port {normalized_port} paused for {cooldown:.0f}s.")

    def connect(self) -> bool:
        self.disconnect()
        candidates = self.candidate_ports()
        if not candidates:
            log("No matching serial port found yet.")
            return False
        for candidate in candidates:
            try:
                self.conn = serial.Serial(
                    port=candidate.device,
                    baudrate=self.baudrate,
                    parity=serial.PARITY_NONE,
                    stopbits=serial.STOPBITS_ONE,
                    bytesize=serial.EIGHTBITS,
                    timeout=1,
                    write_timeout=1,
                )
                time.sleep(0.1)
                self.conn.reset_input_buffer()
                self.conn.reset_output_buffer()
                self.port = candidate.device
                self.last_rx_monotonic = time.monotonic()
                self.pending_since_monotonic = 0.0
                if self.is_auto_mode():
                    # 仅在匹配设备回复本次随机数后，允许向此口发送温度。
                    if not self.send_heartbeat():
                        raise OSError('ESP32 heartbeat write failed')
                    deadline = time.monotonic() + 1.5
                    while time.monotonic() < deadline and self.heartbeat_token is not None:
                        self.poll_incoming()
                        time.sleep(0.02)
                    if not self.connected or not self.last_pong_monotonic:
                        raise OSError('ESP32 heartbeat verification timed out')
                log(f"Serial connected: {candidate.device}")
                return True
            except Exception as exc:
                self.disconnect()
                self.mark_port_temporarily_failed(candidate.device)
                log(f"Serial connect failed on {candidate.device}: {exc}")
        return False

    def poll_incoming(self) -> None:
        if not self.connected:
            return
        try:
            while self.conn.in_waiting > 0:
                line = self.conn.readline().decode("utf-8", errors="ignore").strip()
                if line:
                    self.last_rx_monotonic = time.monotonic()
                    if self.heartbeat_token is not None and line == 'PONG:' + self.heartbeat_token:
                        self.last_pong_monotonic = self.last_rx_monotonic
                        self.heartbeat_token = None
                    if line.upper() == "ACK":
                        self.last_ack_monotonic = self.last_rx_monotonic
                        self.pending_since_monotonic = 0.0
                        self.failed_ports_until.pop(self.port, None)
                    log(f"Serial RX: {line}")
        except Exception as exc:
            log(f"Serial read failed: {exc}")
            self.disconnect()

    def health_check(self) -> bool:
        if not self.connected:
            return False
        self.poll_incoming()
        now = time.monotonic()
        # 独立心跳用于识别通道故障；旧固件仍可通过温度 ACK 保持兼容。
        if (self.heartbeat_token is not None and now - self.heartbeat_sent >= self.ack_timeout_seconds
                and self.last_ack_monotonic < self.heartbeat_sent):
            log('Serial heartbeat timeout: forcing reconnect.')
            self.disconnect()
            return False
        if self.pending_since_monotonic and now - self.pending_since_monotonic >= self.ack_timeout_seconds:
            log("Serial link stale: forcing reconnect.")
            self.mark_port_temporarily_failed(self.port)
            self.disconnect()
            return False
        return self.connected

    def send_heartbeat(self) -> bool:
        if not self.connected:
            return False
        now = time.monotonic()
        if self.heartbeat_token is not None:
            if now - self.heartbeat_sent < self.ack_timeout_seconds:
                return True
            self.heartbeat_token = None
        if now - self.heartbeat_sent < 2:
            return True
        token = str(secrets.randbits(32))
        try:
            self.conn.write(('PING:' + token + '\n').encode('ascii'))
            self.heartbeat_token = token
            self.heartbeat_sent = time.monotonic()
            return True
        except (OSError, serial.SerialException):
            self.disconnect()
            return False

    def send_temperatures(self, cpu: Optional[float], gpu: Optional[float]) -> bool:
        if not self.connected:
            return False
        payload = ""
        if cpu is not None:
            payload += f"CPU{cpu:.1f}\n"
        if gpu is not None:
            payload += f"GPU{gpu:.1f}\n"
        if not payload:
            return True
        try:
            self.conn.write(payload.encode("ascii"))
            self.conn.flush()
            self.last_tx_monotonic = time.monotonic()
            if not self.pending_since_monotonic:
                self.pending_since_monotonic = self.last_tx_monotonic
            return True
        except Exception as exc:
            log(f"Serial write failed: {exc}")
            self.mark_port_temporarily_failed(self.port)
            self.disconnect()
            return False


class FanControllerApp:
    def __init__(self) -> None:
        self.update_interval = 2.0
        self.reconnect_interval = 3.0
        self.wake_gap_seconds = 8.0
        self.wake_recovery_seconds = 20.0
        self.ack_timeout_seconds = 10.0
        self.reader = HardwareTemperatureReader()
        self.serial_bridge = SerialBridge(
            "AUTO", 115200, self.ack_timeout_seconds, ["bluetooth", "蓝牙", "bthenum"], "ac1518d5cf62", ""
        )
        self.stop_event = threading.Event()
        self.last_loop_monotonic = time.monotonic()
        self.last_reconnect_attempt = 0.0

    def handle_wake_resume(self, gap_seconds: float) -> None:
        log(f"Wake detected ({gap_seconds:.1f}s).")
        self.serial_bridge.disconnect()
        self.reader.reopen()
        deadline = time.monotonic() + self.wake_recovery_seconds
        while not self.stop_event.is_set() and time.monotonic() < deadline:
            if self.serial_bridge.connect():
                return
            self.stop_event.wait(1.0)

    def ensure_serial_connected(self) -> None:
        self.serial_bridge.health_check()
        if self.serial_bridge.connected:
            return
        now = time.monotonic()
        if now - self.last_reconnect_attempt < self.reconnect_interval:
            return
        self.last_reconnect_attempt = now
        self.serial_bridge.connect()
