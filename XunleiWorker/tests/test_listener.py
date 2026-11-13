import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch
from app.config import Config
from app.database import Database
from app.worker import Worker
from app.dashboard import create_app

OLD = 'magnet:?xt=urn:btih:' + 'A' * 40
NEW = 'magnet:?xt=urn:btih:' + 'B' * 40


class ListenerTests(unittest.TestCase):
    def test_pause_resume_ignores_old_clipboard_and_persists(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Config(directory)
            db = Database(Path(directory) / 'tasks.db')
            worker = Worker(config, db, Mock())
            try:
                worker.set_clipboard_enabled(False)
                with patch('app.worker.pyperclip.paste') as paste:
                    worker.poll_clipboard()
                    paste.assert_not_called()
                self.assertFalse(Config(directory).data['clipboard_enabled'])
                worker.set_clipboard_enabled(True)
                with patch('app.worker.pyperclip.paste', side_effect=[OLD, OLD, NEW]):
                    worker.poll_clipboard()
                    worker.poll_clipboard()
                    self.assertEqual(db.conn.execute('SELECT count(*) FROM tasks').fetchone()[0], 0)
                    worker.poll_clipboard()
                self.assertEqual(db.conn.execute('SELECT btih FROM tasks').fetchone()[0].lower(), 'b' * 40)
                self.assertTrue(Config(directory).data['clipboard_enabled'])
                self.assertFalse(worker.paused)
            finally:
                db.close()

    def test_browser_toggle_uses_same_resume_baseline(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Config(directory)
            db = Database(Path(directory) / 'tasks.db')
            worker = Worker(config, db, Mock())
            try:
                client = create_app(config, db, worker).test_client()
                headers = {'X-Action-Token': config.token}
                self.assertEqual(client.post('/api/clipboard', json={'enabled': False}, headers=headers).status_code, 200)
                self.assertEqual(client.post('/api/clipboard', json={'enabled': True}, headers=headers).status_code, 200)
                with patch('app.worker.pyperclip.paste', return_value=OLD):
                    worker.poll_clipboard()
                self.assertEqual(db.conn.execute('SELECT count(*) FROM tasks').fetchone()[0], 0)
            finally:
                db.close()
