# -*- coding: utf-8 -*-
"""RapidOCR (PaddleOCR onnxruntime) 本地 OCR 服务。
仅监听 127.0.0.1，接收 PNG 二进制，返回 JSON。
用法: python ocr_server.py [port]

GPU 自动检测：有 CUDAExecutionProvider 就用 GPU，否则 CPU。
"""
import io
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer


def _setup_gpu_dlls():
    """Windows 下预加载 nvidia CUDA/cuDNN 的 DLL 目录（onnxruntime-gpu 需要）。
    必须在 import onnxruntime 之前调用，否则 CUDA provider 无法加载。

    onnxruntime 的 CUDA provider 用 LoadLibraryExW 默认搜索路径，
    只认 PATH 环境变量（不完全认 os.add_dll_directory），故两者都要做。"""
    if os.name != "nt":
        return
    try:
        site = os.path.dirname(os.__file__)          # .../Lib
        nvidia_root = os.path.join(site, "site-packages", "nvidia")
        if not os.path.isdir(nvidia_root):
            return
        bin_dirs = []
        for sub in os.listdir(nvidia_root):
            bin_dir = os.path.join(nvidia_root, sub, "bin")
            if os.path.isdir(bin_dir):
                bin_dirs.append(bin_dir)
                try:
                    os.add_dll_directory(bin_dir)
                except OSError:
                    pass
        if bin_dirs:
            os.environ["PATH"] = os.pathsep.join(bin_dirs) + os.pathsep + os.environ.get("PATH", "")
    except Exception:  # noqa: BLE001
        pass


def _detect_gpu():
    """返回 (use_gpu, providers)。"""
    import onnxruntime as ort
    providers = ort.get_available_providers()
    use_gpu = any("CUDA" in p or "Tensorrt" in p for p in providers)
    return use_gpu, providers


def _enable_cuda_in_rapidocr():
    """把 rapidocr 的 config.yaml 中 Det/Cls/Rec 的 use_cuda 置 True。
    rapidocr 1.2.3 的 kwargs 对 cls/rec 的 use_cuda 前缀剥离有 bug，直接改配置最稳。"""
    import rapidocr_onnxruntime
    import yaml
    from pathlib import Path
    root = Path(rapidocr_onnxruntime.__file__).resolve().parent
    cfg_path = root / "config.yaml"
    cfg = yaml.safe_load(cfg_path.read_text(encoding="utf-8"))
    for section in ("Det", "Cls", "Rec"):
        cfg[section]["use_cuda"] = True
    cfg_path.write_text(
        yaml.safe_dump(cfg, allow_unicode=True, sort_keys=False), encoding="utf-8")


class Handler(BaseHTTPRequestHandler):
    engine = None  # 由 main 注入

    def do_POST(self):
        if self.path != "/ocr":
            self.send_response(404)
            self.end_headers()
            return
        try:
            length = int(self.headers.get("Content-Length", 0))
            data = self.rfile.read(length)
            img = Image.open(io.BytesIO(data)).convert("RGB")
            arr = np.array(img)
            result, _ = self.engine(arr)

            lines = []
            if result:
                for box, text, score in result:
                    xs = [p[0] for p in box]
                    ys = [p[1] for p in box]
                    x = int(min(xs))
                    y = int(min(ys))
                    w = int(max(xs) - min(xs))
                    h = int(max(ys) - min(ys))
                    lines.append({"text": text, "score": float(score), "x": x, "y": y, "w": w, "h": h})

            resp = json.dumps({"lines": lines}, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(resp)))
            self.end_headers()
            self.wfile.write(resp)
        except Exception as e:  # noqa: BLE001
            resp = json.dumps({"error": str(e)}, ensure_ascii=False).encode("utf-8")
            self.send_response(500)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(resp)))
            self.end_headers()
            self.wfile.write(resp)

    def log_message(self, *args):
        pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8790

    _setup_gpu_dlls()

    global np, Image
    import numpy as np
    from PIL import Image
    from rapidocr_onnxruntime import RapidOCR

    use_gpu, providers = _detect_gpu()
    if use_gpu:
        _enable_cuda_in_rapidocr()
    engine = RapidOCR()
    Handler.engine = engine

    print(f"RapidOCR server on 127.0.0.1:{port}", flush=True)
    print(f"onnxruntime providers: {providers}", flush=True)
    print(f"推理后端: {'GPU (CUDA)' if use_gpu else 'CPU'}", flush=True)
    if not use_gpu:
        print("提示: 当前 onnxruntime 未支持 GPU；要启用 GPU 请装 onnxruntime-gpu + CUDA12 + cuDNN9", flush=True)
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()


if __name__ == "__main__":
    main()
