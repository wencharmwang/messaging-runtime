"""Start the shared local MCP host once, without opening a console window."""
import json
import ctypes
from ctypes import wintypes
import msvcrt
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / '.runtime-data'

# The scheduled supervisor owns a Windows job so stopping the task also stops its
# host and OCR descendants. Standalone one-shot starts intentionally do not own it.
SUPERVISOR_JOB = None

class BasicLimitInformation(ctypes.Structure):
    _fields_ = [('PerProcessUserTimeLimit',ctypes.c_longlong),('PerJobUserTimeLimit',ctypes.c_longlong),
                ('LimitFlags',wintypes.DWORD),('MinimumWorkingSetSize',ctypes.c_size_t),
                ('MaximumWorkingSetSize',ctypes.c_size_t),('ActiveProcessLimit',wintypes.DWORD),
                ('Affinity',ctypes.c_size_t),('PriorityClass',wintypes.DWORD),('SchedulingClass',wintypes.DWORD)]

class IoCounters(ctypes.Structure):
    _fields_ = [(name,ctypes.c_ulonglong) for name in ('ReadOperationCount','WriteOperationCount',
                 'OtherOperationCount','ReadTransferCount','WriteTransferCount','OtherTransferCount')]

class ExtendedLimitInformation(ctypes.Structure):
    _fields_ = [('BasicLimitInformation',BasicLimitInformation),('IoInfo',IoCounters),
                ('ProcessMemoryLimit',ctypes.c_size_t),('JobMemoryLimit',ctypes.c_size_t),
                ('PeakProcessMemoryUsed',ctypes.c_size_t),('PeakJobMemoryUsed',ctypes.c_size_t)]

def create_supervisor_job():
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateJobObjectW.argtypes=[ctypes.c_void_p,wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype=wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes=[wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD]
    handle=kernel.CreateJobObjectW(None,None)
    limits=ExtendedLimitInformation()
    limits.BasicLimitInformation.LimitFlags=0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if not handle or not kernel.SetInformationJobObject(handle,9,ctypes.byref(limits),ctypes.sizeof(limits)):
        raise ctypes.WinError(ctypes.get_last_error())
    return handle

def supervise_child(process):
    if SUPERVISOR_JOB is None:
        return
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
    kernel.OpenProcess.restype=wintypes.HANDLE
    kernel.AssignProcessToJobObject.argtypes=[wintypes.HANDLE,wintypes.HANDLE]
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.OpenProcess(0x0101,False,process.pid)
    try:
        if not handle or not kernel.AssignProcessToJobObject(SUPERVISOR_JOB,handle):
            process.terminate()
            process.wait(timeout=10)
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        if handle: kernel.CloseHandle(handle)


def main():
    settings = json.loads((DATA / 'shared-service.json').read_text(encoding='utf-8'))
    endpoint = f"http://127.0.0.1:{settings['port']}/mcp"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def healthy():
        body = json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
            'protocolVersion': '2025-03-26', 'capabilities': {},
            'clientInfo': {'name': 'runtime-launcher', 'version': '1.0'}}}).encode()
        request = urllib.request.Request(endpoint, body, headers={
            'Authorization': 'Bearer ' + settings['token'],
            'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'})
        try:
            with opener.open(request, timeout=2) as response:
                result = json.load(response)
            if result.get('result', {}).get('serverInfo', {}).get('name') != 'messaging-runtime':
                raise RuntimeError('The configured port belongs to a different service.')
            return True
        except urllib.error.HTTPError:
            raise RuntimeError('The configured service rejected authentication or protocol.') from None
        except (urllib.error.URLError, TimeoutError):
            return False

    with (DATA / 'shared-service.lock').open('a+b') as lock:
        lock.seek(0, os.SEEK_END)
        if lock.tell() == 0:
            lock.write(b'0')
            lock.flush()
        deadline = time.monotonic() + 30
        while True:
            try:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise RuntimeError('Timed out waiting for another launcher.') from None
                time.sleep(0.2)
        try:
            if healthy():
                return
            env = os.environ.copy()
            env['MESSAGING_RUNTIME_TOKEN'] = settings['token']
            command = [settings['dotnet'], str(ROOT / 'artifacts/app/messaging-runtime.dll'),
                       '--http', '--port', str(settings['port']),
                       '--config', settings['config']]
            with (DATA / 'shared-service.log').open('ab') as log:
                process = subprocess.Popen(command, cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                                           stdout=log, stderr=log, close_fds=True,
                                           creationflags=subprocess.CREATE_NO_WINDOW)
            supervise_child(process)
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError('Host exited; see shared-service.log.')
                if healthy():
                    (DATA / 'shared-service.pid').write_text(str(process.pid), encoding='ascii')
                    return process
                time.sleep(0.2)
            raise RuntimeError('Host startup timed out; see shared-service.log.')
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


if __name__ == '__main__':
    if sys.argv[1:] not in ([], ['--supervise']):
        raise SystemExit('Expected no arguments or --supervise.')
    supervise = '--supervise' in sys.argv
    if supervise:
        SUPERVISOR_JOB = create_supervisor_job()
    consecutive_failures = 0
    while True:
        started_at = time.monotonic()
        try:
            process = main()
            if not supervise:
                break
            if process is None:
                # Another verified host is already running; watch its availability.
                consecutive_failures = 0
                time.sleep(5)
                continue
            code = process.wait()
            if time.monotonic() - started_at >= 60:
                consecutive_failures = 0
            raise RuntimeError(f'Host exited with code {code}.')
        except Exception as error:
            consecutive_failures += 1
            retry = supervise and consecutive_failures <= 3
            action = ' Restarting in 5 seconds.' if retry else ' Stopping; inspect configuration before restarting.'
            with (DATA / 'shared-service-launcher.log').open('a', encoding='utf-8') as log:
                log.write(f'{time.strftime("%Y-%m-%d %H:%M:%S")} {type(error).__name__}: {error}{action}\n')
            if not retry:
                raise SystemExit(1)
            time.sleep(5)
