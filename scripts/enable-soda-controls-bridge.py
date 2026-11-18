"""Extend the already-installed local SodaMusic FavoriteBridge with one fixed command."""

from pathlib import Path
import shutil
import sys


def extend(path: Path) -> None:
    data = path.read_bytes()
    anchor = b"  async function execute(command) {\n"
    marker = b"if (command === 'disable-taskbar-controls')"
    if marker in data:
        return
    if data.count(anchor) != 1 or b"SodaFavorite-" not in data or b"timingSafeEqual" not in data:
        raise ValueError(f"Unrecognized FavoriteBridge: {path}")
    command = b'''    if (command === 'disable-taskbar-controls') {
      const current = await invoke('sharedState', 'get', ['taskbarWidget']);
      if (typeof current?.enabled !== 'boolean' || typeof current?.autoStart !== 'boolean') {
        throw new Error('Taskbar control state unavailable');
      }
      if (current.autoStart) await invoke('taskbarWidget', 'setAutoStart', [false]);
      if (current.enabled) await invoke('taskbarWidget', 'setEnabled', [false]);
      const after = await invoke('sharedState', 'get', ['taskbarWidget']);
      if (after?.enabled !== false || after?.autoStart !== false) {
        throw new Error('Taskbar controls are not fully disabled yet');
      }
      return { ok: true, pid: process.pid };
    }
'''
    backup = path.with_suffix('.before-display-switcher.cjs')
    if not backup.exists():
        shutil.copy2(path, backup)
    temporary = path.with_suffix('.display-switcher.tmp')
    temporary.write_bytes(data.replace(anchor, anchor + command, 1))
    temporary.replace(path)


if __name__ == '__main__':
    for name in sys.argv[1:]:
        extend(Path(name))
