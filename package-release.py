"""生成仅包含 C# 程序与打包语音引擎的便携 ZIP。"""
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parent
VERSION = '0.0.1'
DIST = ROOT / 'dist'
PUBLISH = DIST / 'CanAudio-singlefile'
STAGE = DIST / f'can-audio-client-{VERSION}-win-x64'


def main():
    subprocess.run(['pwsh', '-File', str(ROOT / 'build.ps1')], check=True, cwd=ROOT)
    command = [sys.executable, '-m', 'PyInstaller', '--noconfirm', '--clean', '--onefile',
               '--name', 'can-voice', '--icon', str(ROOT / 'ui' / 'src' / 'ControllerClient' / 'app.ico'),
               '--distpath', str(DIST / 'voice'),
               '--workpath', str(ROOT / 'build' / 'voice'), '--specpath', str(ROOT / 'build'),
               '--paths', str(ROOT / 'controller'), '--collect-all', 'pymumble_py3',
               '--hidden-import', 'pynput.keyboard._win32', '--hidden-import', 'pynput.mouse._win32',
               '--add-binary', str(ROOT / 'controller' / 'opus.dll') + ';.',
               str(ROOT / 'controller' / 'bridge.py')]
    rnnoise = ROOT / 'controller' / 'rnnoise.dll'
    if rnnoise.exists():
        command[-1:-1] = ['--add-binary', str(rnnoise) + ';.']
    subprocess.run(command, check=True, cwd=ROOT)
    STAGE.mkdir(parents=True, exist_ok=True)
    (STAGE / 'controller').mkdir(exist_ok=True)
    shutil.copy2(PUBLISH / 'ControllerClient.exe', STAGE / 'ControllerClient.exe')
    shutil.copy2(DIST / 'voice' / 'can-voice.exe', STAGE / 'controller' / 'can-voice.exe')
    shutil.copy2(ROOT / 'README.md', STAGE / 'README.md')
    license_file = ROOT / 'controller' / 'RNNOISE-COPYING'
    if rnnoise.exists() and license_file.exists():
        shutil.copy2(license_file, STAGE / license_file.name)
    archive = DIST / f'can-audio-client-{VERSION}-win-x64.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
        for file in [STAGE / 'ControllerClient.exe', STAGE / 'controller' / 'can-voice.exe', STAGE / 'README.md']:
            output.write(file, file.relative_to(STAGE.parent))
        if rnnoise.exists() and license_file.exists():
            output.write(STAGE / license_file.name, STAGE.name + '/' + license_file.name)
    checksum = archive.with_suffix('.zip.sha256')
    checksum.write_text(hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest() + '  ' + archive.name + '\n', encoding='utf-8')
    print(archive)


if __name__ == '__main__':
    main()
