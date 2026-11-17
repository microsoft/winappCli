# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import ctypes
import json
import multiprocessing
import subprocess
import sys
from ctypes import wintypes

from dynwinrt import RoApartment, projected_lifetime_scope
from myapp_winrt.windows.foundation import Uri


def state():
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    get_name = kernel.GetCurrentPackageFullName
    get_name.argtypes = [ctypes.POINTER(wintypes.UINT), wintypes.LPWSTR]
    get_name.restype = wintypes.LONG
    length = wintypes.UINT()
    code = get_name(ctypes.byref(length), None)
    name = None
    if code == 122:
        buffer = ctypes.create_unicode_buffer(length.value)
        code = get_name(ctypes.byref(length), buffer)
        name = buffer.value
    get_image = kernel.GetModuleFileNameW
    get_image.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    get_image.restype = wintypes.DWORD
    image = ctypes.create_unicode_buffer(32768)
    if not get_image(None, image, len(image)):
        raise ctypes.WinError(ctypes.get_last_error())
    with RoApartment(1), projected_lifetime_scope():
        host = Uri("https://example.com/python").host
    return {
        "code": code, "package": name, "process_image": image.value,
        "python_prefix": sys.prefix, "base_prefix": sys.base_prefix,
        "python_version": sys.version.split()[0], "host": host,
        "arguments": [arg for arg in sys.argv[1:] if arg != "--children"],
    }


def worker(connection):
    connection.send(state())
    connection.close()


def worker_state():
    context = multiprocessing.get_context("spawn")
    receive, send = context.Pipe(duplex=False)
    process = context.Process(target=worker, args=(send,))
    process.start()
    send.close()
    try:
        if not receive.poll(30):
            raise RuntimeError("Identity worker did not return its package state")
        result = receive.recv()
        process.join(30)
        if process.is_alive() or process.exitcode != 0:
            raise RuntimeError(f"Identity worker failed: {process.exitcode}")
        return result
    finally:
        if process.is_alive():
            process.terminate()
            process.join()
        receive.close()


def child_state(executable):
    result = subprocess.run(
        [executable, __file__, "child-probe"], capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=30,
    )
    if result.returncode:
        raise RuntimeError(result.stderr or result.stdout)
    lines = [line[len("IDENTITY_JSON:"):] for line in result.stdout.splitlines()
             if line.startswith("IDENTITY_JSON:")]
    if len(lines) != 1:
        raise RuntimeError(f"Unexpected child output: {result.stdout}")
    return json.loads(lines[0])


if __name__ == "__main__":
    multiprocessing.freeze_support()
    output = state()
    if "--children" in sys.argv:
        output["children"] = {
            "multiprocessing": worker_state(),
            "subprocess": child_state(sys.executable),
            "bare_python": child_state("python"),
        }
    print("IDENTITY_JSON:" + json.dumps(output))
