CREATE TABLE tasks(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              link TEXT UNIQUE NOT NULL,
              type TEXT NOT NULL,
              source TEXT,
              content_hash TEXT UNIQUE NOT NULL,
              status INTEGER DEFAULT 0,
              retry_count INTEGER DEFAULT 0,
              next_retry_at TEXT,
              last_error TEXT,
              raw_text TEXT,
              created_at TEXT,
              updated_at TEXT,
              imported_at TEXT
            );
CREATE TABLE torrent_tasks(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              local_file_path TEXT,
              remote_file_path TEXT,
              file_name TEXT,
              sha256 TEXT UNIQUE NOT NULL,
              status INTEGER DEFAULT 0,
              retry_count INTEGER DEFAULT 0,
              next_retry_at TEXT,
              last_error TEXT,
              created_at TEXT,
              updated_at TEXT,
              imported_at TEXT,
              local_moved_to TEXT,
              remote_moved_to TEXT
            );
CREATE TABLE import_events(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              task_id INTEGER,
              task_kind TEXT NOT NULL,
              event_type TEXT NOT NULL,
              source TEXT,
              task_name TEXT,
              result TEXT NOT NULL,
              attempt_no INTEGER DEFAULT 1,
              file_count INTEGER DEFAULT 0,
              selected_count INTEGER DEFAULT 0,
              filtered_count INTEGER DEFAULT 0,
              duration_ms INTEGER DEFAULT 0,
              pre_filter_supported INTEGER DEFAULT 0,
              config_generation INTEGER DEFAULT 0,
              error_type TEXT,
              error_message TEXT,
              created_at TEXT,
              finished_at TEXT
            );
CREATE TABLE filter_events(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              import_event_id INTEGER,
              task_id INTEGER,
              task_kind TEXT NOT NULL,
              resource_name TEXT,
              file_name TEXT,
              file_size INTEGER DEFAULT 0,
              file_index INTEGER,
              rule_name TEXT NOT NULL,
              created_at TEXT
            );
CREATE TABLE duplicate_events(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              task_id INTEGER,
              task_kind TEXT NOT NULL,
              task_name TEXT,
              reason TEXT NOT NULL,
              source TEXT,
              created_at TEXT
            );
