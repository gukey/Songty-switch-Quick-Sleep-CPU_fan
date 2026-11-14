import sys
import time
import unittest
from unittest.mock import Mock, patch
import worker
from sensor_process import SensorProcess
from fan_controller import HardwareTemperatureReader, SerialBridge, PortInfo
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

    def test_sensor_failure_keeps_serial_running_without_fabricated_temperatures(self):
        app = Mock(update_interval=2, ack_timeout_seconds=10)
        app.stop_event.is_set.side_effect = [False, False, False, True]
        app.serial_bridge.connected = True
        app.serial_bridge.last_ack_monotonic = time.monotonic()
        app.serial_bridge.last_pong_monotonic = 0
        app.serial_bridge.port = 'TEST'
        sensors = Mock()
        sensors.updated = time.monotonic()
        sensors.poll.side_effect = [(95, 88), (None, None), (92, 86)]
        with patch.object(worker, 'FanControllerApp', return_value=app), \
             patch.object(worker, 'SensorProcess', return_value=sensors), \
             patch.object(worker.threading, 'Thread'), patch.object(worker, 'emit') as emit:
            self.assertEqual(worker.run(False), 0)
        self.assertEqual(app.ensure_serial_connected.call_count, 3)
        self.assertEqual(app.serial_bridge.send_temperatures.call_args_list[1].args, (None, None))
        self.assertEqual(emit.call_args_list[1].args[:2], (None, None))
        app.handle_wake_resume.assert_not_called()
        sensors.close.assert_called_once()

    def test_selected_port_never_opens_unrelated_ports(self):
        bridge = SerialBridge('COM5', 115200, 10, ['bluetooth'], '', '')
        ports = [PortInfo('COM3','SOL','',''), PortInfo('COM5','Bluetooth output','',''),
                 PortInfo('COM6','USB debug','','')]
        with patch('fan_controller.get_serial_ports', return_value=ports):
            self.assertEqual([p.device for p in bridge.candidate_ports()], ['COM5'])
            bridge.mark_port_temporarily_failed('COM5')
            self.assertEqual(bridge.candidate_ports(), [])

    def test_heartbeat_checks_nonce_without_faking_temperature_ack(self):
        bridge = SerialBridge('COM5', 115200, 10, [], '', '')
        connection = Mock(is_open=True)
        bridge.conn = connection
        with patch('fan_controller.secrets.randbits', return_value=123):
            self.assertTrue(bridge.send_heartbeat())
        self.assertEqual(connection.write.call_args.args[0], b'PING:123\n')
        connection.in_waiting = 1
        def read():
            connection.in_waiting = 0
            return b'PONG:123\n'
        connection.readline.side_effect = read
        bridge.poll_incoming()
        self.assertIsNone(bridge.heartbeat_token)
        self.assertGreater(bridge.last_pong_monotonic, 0)
        self.assertEqual(bridge.last_ack_monotonic, 0)

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
