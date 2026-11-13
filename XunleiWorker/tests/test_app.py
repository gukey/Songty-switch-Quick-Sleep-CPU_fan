import base64
from contextlib import closing
from copy import deepcopy
import json
from pathlib import Path
import sqlite3
import tempfile
import time
import unittest
from unittest.mock import Mock
import requests
from app.config import Config
from app.database import Database
from app.dashboard import create_app
from app.selection import MIN_BYTES, ParseError, magnet_info, select_main
from app.worker import Worker
from app.xunlei_api import APIError, SubmissionUncertain, XunleiAPI

LINK = 'magnet:?xt=urn:btih:' + 'A'*40


def resource():
    return {'id': 'root', 'name': '电影合集', 'is_dir': True, 'file_count': 5, 'dir': {'resources': [
        {'id': 'root.0', 'name': '主视频.mp4', 'file_size': 900*1024*1024},
        {'id': 'root.1', 'name': '第二视频.mkv', 'file_size': 300*1024*1024, 'file_index': 1},
        {'id': 'root.2', 'name': 'sample.mp4', 'file_size': 1000*1024*1024, 'file_index': 2},
        {'id': 'root.3', 'name': '说明.zip', 'file_size': 2000*1024*1024, 'file_index': 3},
        {'id': 'root.4', 'name': '小视频.mp4', 'file_size': MIN_BYTES, 'file_index': 4},
    ]}}


class SelectionTests(unittest.TestCase):
    def test_only_largest_video_and_zero_index(self):
        result = select_main(resource())
        self.assertEqual(result['selected']['index'], 0)
        self.assertEqual(sum(f['reason']=='主视频' for f in result['files']), 1)
        self.assertEqual(result['files'][4]['reason'], '不大于 200MB')

    def test_strict_boundary(self):
        for size, selected in [(MIN_BYTES-1, False), (MIN_BYTES, False), (MIN_BYTES+1, True)]:
            self.assertEqual(select_main({'name':'test.mp4','file_size':size})['selected'] is not None, selected)

    def test_missing_size_rejected(self):
        root=resource();del root['dir']['resources'][0]['file_size']
        with self.assertRaises(ParseError): select_main(root)

    def test_incomplete_listing_rejected(self):
        root=resource();root['dir']['resources'].pop()
        with self.assertRaises(ParseError): select_main(root)

    def test_duplicate_index_rejected(self):
        root=resource();root['dir']['resources'][1]['file_index']=0
        with self.assertRaises(ParseError): select_main(root)

    def test_unresolved_pagination_rejected(self):
        root=resource();root['dir']['next_page_token']='page2'
        with self.assertRaises(ParseError): select_main(root)

    def test_equivalent_btih(self):
        encoded=base64.b32encode(bytes.fromhex('AA'*20)).decode()
        self.assertEqual(magnet_info(LINK)['btih'],magnet_info('magnet:?xt=urn:btih:'+encoded)['btih'])


class AppTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.folder=Path(self.temp.name)
        self.cfg=Config(self.folder)
        self.db=Database(self.folder/'data'/'tasks.db')
        self.remote=Mock(download_path='/downloads/')
        self.worker=Worker(self.cfg,self.db,self.remote)
        self.client=create_app(self.cfg,self.db,self.worker).test_client()
        self.headers={'X-Action-Token':self.cfg.token}

    def tearDown(self):
        self.db.close();self.temp.cleanup()

    def test_submission_payload_only_one_file(self):
        api=XunleiAPI(self.cfg);api.target='device';api.download_path='/downloads/'
        payload=api.payload(LINK,select_main(resource()))
        self.assertEqual(payload['params']['sub_file_index'],'0')
        self.assertEqual(payload['params']['url'],LINK)
        self.assertNotEqual(payload['params']['sub_file_index'],'-1')

    def test_worker_success_persisted(self):
        self.db.add_text(LINK)
        self.remote.resolve.return_value=select_main(resource());self.remote.submit.return_value='remote-task'
        self.worker.process(self.db.claim())
        row=self.db.records()['items'][0]
        self.assertEqual(row['status'],'imported');self.assertEqual(row['selected_index'],0)
        self.assertEqual(self.db.summary()['day_success'],1)
        self.assertEqual(len(self.db.task(row['id'])['files']),5)

    def test_success_notification_after_record_is_saved(self):
        self.db.add_text(LINK)
        self.remote.resolve.return_value=select_main(resource());self.remote.submit.return_value='remote-task'
        self.worker.notifier=Mock()
        def check_saved(selection):
            self.assertEqual(self.db.task(1)['status'],'imported')
            self.assertEqual(selection['selected']['index'],0)
        self.worker.notifier.success.side_effect=check_saved
        self.worker.process(self.db.claim())
        self.worker.notifier.success.assert_called_once()

    def test_notification_failure_does_not_change_success(self):
        self.db.add_text(LINK)
        self.remote.resolve.return_value=select_main(resource());self.remote.submit.return_value='remote-task'
        self.worker.notifier=Mock()
        self.worker.notifier.success.side_effect=RuntimeError('notifications unavailable')
        self.worker.process(self.db.claim())
        self.assertEqual(self.db.task(1)['status'],'imported')

    def test_skipped_and_uncertain_tasks_do_not_notify_success(self):
        self.db.add_text(LINK);self.worker.notifier=Mock()
        self.remote.resolve.return_value=select_main({'name':'small.mp4','file_size':100})
        self.worker.process(self.db.claim())
        self.worker.notifier.success.assert_not_called()
        self.db.retry(1)
        self.remote.resolve.return_value=select_main(resource())
        self.remote.submit.side_effect=SubmissionUncertain('timeout')
        self.worker.process(self.db.claim())
        self.worker.notifier.success.assert_not_called()

    def test_unknown_metadata_never_submitted(self):
        self.db.add_text(LINK);self.remote.resolve.side_effect=ParseError('清单不完整')
        self.worker.process(self.db.claim())
        self.remote.submit.assert_not_called()
        self.assertEqual(self.db.records()['items'][0]['status'],'retry')

    def test_small_video_skipped(self):
        self.db.add_text(LINK);self.remote.resolve.return_value=select_main({'name':'small.mp4','file_size':100})
        self.worker.process(self.db.claim());self.remote.submit.assert_not_called()
        self.assertEqual(self.db.records()['items'][0]['status'],'skipped')

    def test_uncertain_submission_not_retried(self):
        self.db.add_text(LINK);self.remote.resolve.return_value=select_main(resource())
        self.remote.submit.side_effect=SubmissionUncertain('连接中断')
        self.worker.process(self.db.claim())
        self.assertEqual(self.db.records()['items'][0]['status'],'review');self.assertIsNone(self.db.claim())
        with self.assertRaises(ValueError):self.db.retry(1)
        self.db.retry(1,True);self.assertIsNotNone(self.db.claim())

    def test_duplicate_persists_on_reopen(self):
        self.db.add_text(LINK);path=self.db.path;self.db.close();self.db=Database(path)
        self.assertEqual(self.db.add_text(LINK+'&dn=其他名称')['duplicates'],1)

    def test_interrupted_submission_becomes_review(self):
        self.db.add_text(LINK);task=self.db.claim();self.db.submitting(task['id']);path=self.db.path
        self.db.close();self.db=Database(path)
        self.assertEqual(self.db.task(task['id'])['status'],'review');self.assertIsNone(self.db.claim())

    def test_page_and_shared_database(self):
        self.assertEqual(self.client.get('/').status_code,200)
        with self.client.get('/static/app.js') as response:
            self.assertEqual(response.status_code,200)
        self.assertEqual(self.client.post('/api/tasks',json={'text':LINK},headers=self.headers).json['added'],1)
        self.assertEqual(self.client.get('/api/status').json['total'],1)
        self.assertEqual(self.client.get('/api/tasks').json['items'][0]['link'],LINK)

    def test_web_rule_persists_and_controls_strict_size_boundary(self):
        response=self.client.post('/api/rules',json={'min_video_size_mb':100.5},headers=self.headers)
        self.assertEqual(response.status_code,200)
        cfg=Config(self.folder)
        self.assertEqual(cfg.rules['min_video_size_mb'],100.5)
        self.assertEqual(self.client.get('/api/status').json['rules']['min_video_size_mb'],100.5)
        self.assertIsNone(select_main({'name':'movie.mp4','file_size':100.5*1048576},cfg.rules)['selected'])
        self.assertIsNotNone(select_main({'name':'movie.mp4','file_size':100.5*1048576+1},cfg.rules)['selected'])

    def test_invalid_rules_leave_file_and_runtime_unchanged(self):
        before=self.cfg.rules_path.read_bytes()
        for value in (0,-1,True,None,'200',float('inf'),float('nan'),1048577):
            response=self.client.post('/api/rules',json={'min_video_size_mb':value},headers=self.headers)
            self.assertEqual(response.status_code,400)
            self.assertEqual(self.cfg.rules_path.read_bytes(),before)
            self.assertEqual(self.cfg.rules['min_video_size_mb'],200)
        self.assertEqual(self.client.post('/api/rules',json={'min_video_size_mb':300}).status_code,403)

    def test_rule_write_failure_keeps_active_rule(self):
        from unittest.mock import patch
        with patch('app.config.Path.replace',side_effect=OSError):
            response=self.client.post('/api/rules',json={'min_video_size_mb':300},headers=self.headers)
        self.assertEqual(response.status_code,500)
        self.assertEqual(Config(self.folder).rules['min_video_size_mb'],200)
        self.assertEqual(self.cfg.rules['min_video_size_mb'],200)

    def test_no_token_or_foreign_host_rejected(self):
        self.assertEqual(self.client.post('/api/tasks',json={'text':LINK}).status_code,403)
        self.assertEqual(self.client.get('/',headers={'Host':'evil.example'}).status_code,403)
        self.assertEqual(self.client.post('/api/tasks',json={'text':LINK},headers={**self.headers,'Origin':'http://evil.example'}).status_code,403)

    def test_batch_limit(self):
        self.assertEqual(self.client.post('/api/tasks',json={'text':'\n'.join([LINK]*501)},headers=self.headers).status_code,400)
        self.assertEqual(self.db.summary()['total'],0)

    def test_legacy_import_idempotent_and_readonly(self):
        old_path=self.folder/'legacy.db'
        with closing(sqlite3.connect(old_path)) as old:
            old.executescript(Path(__file__).with_name('legacy_schema.sql').read_text(encoding='utf-8'))
            old.execute("INSERT INTO tasks(link,type,content_hash,status,created_at) VALUES (?,?,?,1,datetime('now','localtime'))",(LINK,'magnet','hash'))
            old.execute("INSERT INTO import_events(task_id,task_kind,event_type,result,created_at) VALUES (1,'link','IMPORT_ATTEMPT','SUCCESS',datetime('now','localtime'))")
            old.commit()
        before=old_path.read_bytes()
        self.db.import_legacy(old_path);self.db.import_legacy(old_path)
        self.assertEqual(self.db.summary()['counts']['imported'],1)
        self.assertEqual(self.db.summary()['day_success'],1)
        self.assertEqual(self.db.summary()['legacy_records'],2)
        self.assertEqual(old_path.read_bytes(),before)
        self.assertEqual(self.db.add_text(LINK)['duplicates'],1)

    def test_api_resolve_pagination(self):
        api=XunleiAPI(self.cfg);api.authenticate=Mock()
        root=resource();rest=root['dir']['resources'][2:];root['dir']['resources']=root['dir']['resources'][:2];root['dir']['next_page_token']='two'
        api._request=Mock(side_effect=[{'list_id':'L','list':{'resources':[root]}},{'list':{'resources':rest}}])
        self.assertEqual(api.resolve(LINK)['selected']['index'],0)
        self.assertEqual(api._request.call_args.kwargs['params']['page_token'],'two')

    def test_submit_timeout_is_uncertain(self):
        api=XunleiAPI(self.cfg);api.base='http://localhost/';api.session.request=Mock(side_effect=requests.Timeout())
        with self.assertRaises(SubmissionUncertain):api._request('POST','drive/v1/task',body={},submitting=True)


if __name__=='__main__':unittest.main()
