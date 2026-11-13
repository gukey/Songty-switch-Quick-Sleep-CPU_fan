a = Analysis(['host.py'], pathex=[], binaries=[],
             datas=[('app/templates', 'app/templates'), ('app/static', 'app/static'),
                    ('app/assets', 'app/assets')], hiddenimports=['waitress', 'pyperclip'])
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, a.binaries, a.datas, [], name='XunleiWorker',
          console=True, debug=False, strip=False, upx=False)
