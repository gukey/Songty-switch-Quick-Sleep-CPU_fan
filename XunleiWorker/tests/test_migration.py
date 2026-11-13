import hashlib
import tempfile
import unittest
from pathlib import Path
from app.config import Config
from app.database import Database
from migrate import migrate


class MigrationTests(unittest.TestCase):
    def test_keeps_old_data_and_holds_pending_tasks(self):
        with tempfile.TemporaryDirectory() as directory:
            source, target = Path(directory) / 'old', Path(directory) / 'new'
            Config(source)
            db = Database(source / 'data/tasks.db')
            db.add_text('magnet:?xt=urn:btih:' + 'A' * 40)
            db.close()
            before = hashlib.sha256((source / 'data/tasks.db').read_bytes()).digest()
            self.assertEqual(migrate(source, target), 1)
            self.assertEqual(hashlib.sha256((source / 'data/tasks.db').read_bytes()).digest(), before)
            migrated = Database(target / 'data/tasks.db')
            try:
                self.assertEqual(migrated.conn.execute('SELECT status FROM tasks').fetchone()[0], 'review')
                self.assertTrue((target / 'migration-backup/tasks.db').exists())
                with self.assertRaises(ValueError):
                    migrate(source, target)
            finally:
                migrated.close()
