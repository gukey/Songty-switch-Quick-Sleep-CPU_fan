import sys
import time
import unittest
from unittest.mock import Mock, patch
import worker
from sensor_process import SensorProcess
from fan_controller import HardwareTemperatureReader
from types import SimpleNamespace


class ResilienceTests(unittest.TestCase):
    def wait_process(self, sensor, previous=None):
        deadline = time.monotonic() + 6
        while (sensor.process is None or sensor.process is previous) and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertIsNotNone(sensor.process)
        self.assertIsNot(sensor.process, previous)
    def test_decimal_gpu_temperature_from_driver_cli(self):
        reader = HardwareTemperatureReader()
        reader.hardware_type = SimpleNamespace(Cpu='CPU')
        reader.sensor_type = SimpleNamespace(Temperature='Temperature')
        hardware = Mock(HardwareType='CPU', Sensors=[SimpleNamespace(SensorType='Temperature', Value=91.5)])
        reader.target_hardware = [hardware]
        with patch('fan_controller.shutil.which', return_value='nvidia-smi'), \
             patch('fan_controller.subprocess.run', return_value=SimpleNamespace(stdout='67.20\nN/A\n')):
            self.assertEqual(reader.read_temperatures(), (91.5, 67.2))

    def test_sensor_failure_keeps_serial_running_with_protection(self):
        app = Mock(update_interval=2, ack_timeout_seconds=10)
        app.stop_event.is_set.side_effect = [False, False, False, True]
        app.serial_bridge.connected = True
        app.serial_bridge.last_ack_monotonic = time.monotonic()
        app.serial_bridge.port = 'TEST'
        sensors = Mock()
        sensors.poll.side_effect = [(95, 88), (None, None), (92, 86)]
        with patch.object(worker, 'FanControllerApp', return_value=app), \
             patch.object(worker, 'SensorProcess', return_value=sensors), \
             patch.object(worker.threading, 'Thread'), patch.object(worker, 'emit') as emit:
            self.assertEqual(worker.run(False), 0)
        self.assertEqual(app.ensure_serial_connected.call_count, 3)
        self.assertEqual(app.serial_bridge.send_temperatures.call_args_list[1].args, (120, 120))
        self.assertEqual(emit.call_args_list[1].args[:2], (None, None))
        app.handle_wake_resume.assert_not_called()
        sensors.close.assert_called_once()

    def test_hung_collector_is_replaced(self):
        sensor = SensorProcess([sys.executable, '-u', '-c',
                                'import time; print(\'{"cpu":91,"gpu":85}\', flush=True); time.sleep(20)'], timeout=0.6)
        try:
            sensor.poll()
            self.wait_process(sensor)
            deadline = time.monotonic() + 5
            while not sensor.updated and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertEqual(sensor.poll(), (91, 85))
            original = sensor.process
            time.sleep(0.7)
            sensor.poll()
            self.wait_process(sensor, original)
            self.assertNotEqual(original.pid, sensor.process.pid)
            self.assertIsNotNone(original.poll())
        finally:
            sensor.close()

    def test_crashed_collector_is_replaced(self):
        sensor = SensorProcess([sys.executable, '-c', 'import os; os._exit(7)'])
        try:
            sensor.poll()
            self.wait_process(sensor)
            original = sensor.process
            original.wait(timeout=5)
            self.assertEqual(sensor.poll(), (None, None))
            self.wait_process(sensor, original)
            self.assertNotEqual(original.pid, sensor.process.pid)
        finally:
            sensor.close()
