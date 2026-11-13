"""停用旧实例后执行的一次性只读迁移。原数据库保持不变。"""
import argparse
import json
import sqlite3
from contextlib import closing
from pathlib import Path
from app.config import Config
from app.database import Database


def migrate(source, destination):
    source, destination = Path(source), Path(destination)
    if (destination / 'settings.json').exists() or (destination / 'data/tasks.db').exists():
        raise ValueError('目标已存在数据，禁止覆盖。')
    settings = json.loads((source / 'settings.json').read_text(encoding='utf-8-sig'))
    rules = json.loads((source / 'rules.json').read_text(encoding='utf-8-sig'))
    Config.validate(settings, rules)
    old_db = Path(settings.get('database_path', 'data/tasks.db'))
    if not old_db.is_absolute():
        old_db = source / old_db
    backup = destination / 'migration-backup/tasks.db'
    backup.parent.mkdir(parents=True, exist_ok=True)
    with closing(sqlite3.connect(old_db.resolve().as_uri() + '?mode=ro', uri=True)) as original:
        with closing(sqlite3.connect(backup)) as snapshot:
            original.backup(snapshot)
    new_db = destination / 'data/tasks.db'
    new_db.parent.mkdir(parents=True, exist_ok=True)
    with closing(sqlite3.connect(backup)) as snapshot:
        with closing(sqlite3.connect(new_db)) as output:
            snapshot.backup(output)
    database = Database(new_db)
    with database.conn:
        database.conn.execute("UPDATE tasks SET status='review', error='整合迁移，请先核对旧任务结果再重试' "
                              "WHERE status IN ('queued','retry','parsing','submitting')")
    count = database.conn.execute('SELECT count(*) FROM tasks').fetchone()[0]
    database.close()
    config = Config(destination)
    settings.update(database_path='data/tasks.db', log_directory='logs', legacy_database='', auto_start=False)
    config.data = settings
    config.save()
    config.rules_path.write_text(json.dumps(rules, ensure_ascii=False, indent=2), encoding='utf-8')
    return count


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source')
    parser.add_argument('destination')
    args = parser.parse_args()
    print('已迁移记录数', migrate(args.source, args.destination))
