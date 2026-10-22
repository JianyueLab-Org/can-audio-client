"""C# 客户端的 JSON Lines 语音进程；stdout 仅用于协议，日志写 stderr。"""
import json
import logging
import os
from pathlib import Path
import sys
import threading
from connectionstatus import snapshot as connection_snapshot

sys.stdin.reconfigure(encoding='utf-8')
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
ROOT = Path(__file__).resolve().parent
os.environ['PATH'] = str(ROOT) + os.pathsep + os.environ.get('PATH', '')
os.environ['PYGAME_HIDE_SUPPORT_PROMPT'] = '1'
_output = sys.stdout
sys.stdout = sys.stderr
logging.basicConfig(stream=sys.stderr, level=logging.INFO)
_lock = threading.Lock()


def emit(**data):
    with _lock:
        _output.write(json.dumps(data, ensure_ascii=False) + '\n')
        _output.flush()


def main():
    import pyaudio
    import ptt
    import radiostack
    from voice import VoiceClient
    client = None
    watcher = None
    capture = None
    stop_status = threading.Event()

    def publish_status():
        while not stop_status.wait(1):
            try:
                emit(event='connection', **connection_snapshot(client))
            except Exception:
                logging.exception('Could not read connection status')

    status_thread = threading.Thread(target=publish_status, daemon=True)
    status_thread.start()
    try:
        emit(event='ready')
        for line in sys.stdin:
            request = {}
            try:
                request = json.loads(line)
                command = request['command']
                data = request.get('data') or {}
                result = None
                if command == 'devices':
                    audio = pyaudio.PyAudio()
                    try:
                        result = [dict(index=i, name=audio.get_device_info_by_index(i)['name'],
                                       input=bool(audio.get_device_info_by_index(i)['maxInputChannels']),
                                       output=bool(audio.get_device_info_by_index(i)['maxOutputChannels']))
                                  for i in range(audio.get_device_count())]
                    finally:
                        audio.terminate()
                elif command == 'connect':
                    if client:
                        client.disconnect()
                    client = VoiceClient(data['host'], data['cid'], data['password'], port=data['port'],
                        on_state=lambda state, message: emit(event='state', state=state, message=message),
                        on_rx=lambda khz, active, callsign: emit(event='rx', khz=khz, active=active, callsign=callsign),
                        on_tx=lambda active: emit(event='tx', active=active))
                    client._input_device = data.get('input')
                    client._output_device = data.get('output')
                    result = client.connect()
                elif command == 'sync' and client:
                    stack = radiostack.RadioStack()
                    stack.load(data['radios'])
                    stack.selected_khz = data.get('selected')
                    stack.set_locked(data.get('locked', []))
                    stack.set_transmit_allowed(data['allowed'])
                    if not data['allowed'] or not stack.tx_frequencies():
                        client.stop_transmit()
                    # 频道同步会等待网络往返，不能挡住 PTT 松开指令。
                    threading.Thread(target=client.sync, args=(stack,), daemon=True).start()
                elif command == 'audio' and client:
                    client.set_mic_volume(data['mic'])
                    client.set_speaker_volume(data['speaker'])
                    client.set_mic_denoise(data['denoise'])
                    client.setup_audio(data.get('input'), data.get('output'))
                elif command == 'ptt':
                    if client:
                        (client.start_transmit if data['active'] else client.stop_transmit)()
                elif command == 'watch':
                    if watcher:
                        watcher.stop()
                    watcher = ptt.PttWatcher(ptt.load(data['bindings']),
                        on_change=lambda active: emit(event='ptt', active=active))
                    watcher.start()
                elif command == 'capture':
                    if watcher:
                        watcher.stop()
                    if capture:
                        capture.stop()
                    capture = ptt.PttCapture(lambda binding: emit(event='captured', binding=binding.to_dict()))
                    capture.start()
                elif command == 'cancel_capture':
                    if capture:
                        capture.stop()
                        capture = None
                elif command == 'disconnect':
                    if client:
                        client.disconnect()
                        client = None
                elif command == 'quit':
                    break
                else:
                    raise ValueError('未知命令或尚未连接: ' + command)
                emit(id=request['id'], result=result)
            except Exception as error:
                logging.exception('Bridge command failed')
                emit(id=request.get('id'), error=str(error))
    finally:
        stop_status.set()
        status_thread.join(timeout=2)
        if capture:
            capture.stop()
        if watcher:
            watcher.stop()
        if client:
            client.disconnect()


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        emit(event='fatal', message=str(error))
        raise
