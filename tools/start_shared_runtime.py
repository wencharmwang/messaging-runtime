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

# A Host owns port 8766 until it exits, and a child inherits its parent's window
# station. A supervisor started from a non-interactive window station (early logon,
# service or agent context) would therefore spawn a Host whose EnumWindows sees no
# user windows at all: every read fails with provider_unavailable, yet the process
# keeps the port, so a healthy supervisor only ever sees "another host is already
# running" and waits forever. Refuse to host until this desktop is usable, and wait
# for it instead of failing fast so an at-logon start recovers on its own.
DESKTOP_RETRY_SECONDS = 15


class DesktopNotReady(RuntimeError):
    """This window station cannot see user windows yet; retry later."""


def describe_exit_code(code):
    """Say *why* the Host ended, so a routine restart does not read as a crash.

    The Host's own code only ever returns 0 (clean) or 1 (a caught startup or
    configuration error) and never -1. So 0xFFFFFFFF can only come from
    TerminateProcess(-1) — which is exactly what Stop-Process and .NET
    Process.Kill() write. That is the footprint of restart.ps1 reaping the old
    Host, not a fault inside the Host. Logged as a bare "Host exited with code
    4294967295" it reads like a crash and sends the next debugging session
    chasing a bug that does not exist.
    """
    unsigned = code & 0xFFFFFFFF
    if unsigned == 0:
        return 'Host stopped cleanly (exit code 0).'
    if unsigned == 0xFFFFFFFF:
        return ('Host was terminated from outside (exit code 0xFFFFFFFF = -1) — '
                'that is Stop-Process/taskkill, i.e. a restart, not a Host fault.')
    return f'Host exited with code {code} (0x{unsigned:08X}).'


class HostExited(RuntimeError):
    """The Host this supervisor started is gone; carries the decoded reason."""

    def __init__(self, code):
        self.code = code & 0xFFFFFFFF
        super().__init__(describe_exit_code(code))


def active_console_session():
    """Session id of the interactive console, or None when nobody is logged on."""
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.WTSGetActiveConsoleSessionId.restype = wintypes.DWORD
    session = kernel.WTSGetActiveConsoleSessionId()
    return None if session == 0xFFFFFFFF else session


def current_session():
    """Session id of this process, or None when it cannot be read."""
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcessId.restype = wintypes.DWORD
    kernel.ProcessIdToSessionId.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    kernel.ProcessIdToSessionId.restype = wintypes.BOOL
    session = wintypes.DWORD()
    if not kernel.ProcessIdToSessionId(kernel.GetCurrentProcessId(), ctypes.byref(session)):
        return None
    return session.value


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


def _count_visible_user_windows():
    user32 = ctypes.WinDLL('user32', use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user32.EnumWindows.restype = wintypes.BOOL
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.IsWindowVisible.restype = wintypes.BOOL
    user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
    user32.GetWindowTextLengthW.restype = ctypes.c_int

    seen = []

    def visit(hwnd, _lparam):
        if user32.IsWindowVisible(hwnd) and user32.GetWindowTextLengthW(hwnd) > 0:
            seen.append(hwnd)
        return True

    callback = callback_type(visit)  # keep a reference for the whole call
    user32.EnumWindows(callback, 0)
    return len(seen)


def desktop_capability():
    """(ok, detail). Can a Host started by this process enumerate user windows?

    Mirrors what the Windows driver needs: an interactive console session plus a
    desktop that exposes user windows. See tools/start_shared_runtime.py notes.
    """
    if os.name != 'nt':
        return True, 'not Windows'
    try:
        session = current_session()
        if session is None:
            return False, 'cannot read the current Windows session id'
        console = active_console_session()
        if console is None:
            return False, 'no interactive console session is active'
        if console != session:
            return False, f'session {session} is not the active console session {console}'
        windows = _count_visible_user_windows()
        if not windows:
            return False, 'the current desktop exposes no visible windows yet'
        return True, f'session {session}, {windows} visible windows'
    except Exception as error:
        return False, f'{type(error).__name__}: {error}'


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
            # Only claim the port from a window station that can actually see user
            # windows; an adopted-but-blind Host is worse than no Host at all.
            capable, detail = desktop_capability()
            if not capable:
                raise DesktopNotReady(detail)
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
    if sys.argv[1:] not in ([], ['--supervise'], ['--check-desktop']):
        raise SystemExit('Expected no arguments, --supervise or --check-desktop.')
    if '--check-desktop' in sys.argv:
        capable, detail = desktop_capability()
        print(('interactive desktop ready' if capable else 'not ready to host') + ': ' + detail)
        raise SystemExit(0 if capable else 1)
    supervise = '--supervise' in sys.argv
    if supervise:
        SUPERVISOR_JOB = create_supervisor_job()
    consecutive_failures = 0
    desktop_note = None
    while True:
        started_at = time.monotonic()
        try:
            process = main()
            desktop_note = None
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
            raise HostExited(code)
        except DesktopNotReady as error:
            # Transient by definition: a logon-time or otherwise non-interactive
            # window station usually becomes interactive shortly. Wait it out and
            # never claim port 8766 in the meantime, so a healthy Host can.
            if str(error) != desktop_note:
                desktop_note = str(error)
                with (DATA / 'shared-service-launcher.log').open('a', encoding='utf-8') as log:
                    log.write(f'{time.strftime("%Y-%m-%d %H:%M:%S")} DesktopNotReady: {error}. '
                              f'Waiting {DESKTOP_RETRY_SECONDS}s for an interactive desktop; not hosting.\n')
            time.sleep(DESKTOP_RETRY_SECONDS)
            continue
        except Exception as error:
            consecutive_failures += 1
            retry = supervise and consecutive_failures <= 3
            action = ' Restarting in 5 seconds.' if retry else ' Stopping; inspect configuration before restarting.'
            with (DATA / 'shared-service-launcher.log').open('a', encoding='utf-8') as log:
                log.write(f'{time.strftime("%Y-%m-%d %H:%M:%S")} {type(error).__name__}: {error}{action}\n')
            if not retry:
                raise SystemExit(1)
            time.sleep(5)
