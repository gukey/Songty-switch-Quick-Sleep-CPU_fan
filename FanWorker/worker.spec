# -*- mode: python ; coding: utf-8 -*-
from pathlib import Path
import os

root = Path(SPECPATH)
library = Path(os.environ.get('DISPLAY_SWITCHER_FAN_LIB_DIR', root / 'lib'))
required = (
    'HidSharp.dll',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'Microsoft.Bcl.HashCode.dll',
    'OpenHardwareMonitorLib.dll',
    'RAMSPDToolkit-NDD.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Threading.AccessControl.dll',
    'System.Threading.Tasks.Extensions.dll',
)
missing = [name for name in required if not (library / name).is_file()]
if missing:
    raise FileNotFoundError(f"Missing fan monitor dependencies in {library}: {', '.join(missing)}")
analysis = Analysis(
    [str(root / 'worker.py')],
    pathex=[str(root)],
    binaries=[(str(library / name), '.') for name in required],
    datas=[], hiddenimports=[], hookspath=[], hooksconfig={}, runtime_hooks=[],
    excludes=[], noarchive=False,
)
archive = PYZ(analysis.pure)
exe = EXE(
    archive, analysis.scripts, analysis.binaries, analysis.datas, [],
    name='FanWorker', debug=False, strip=False, upx=False, console=True,
)
