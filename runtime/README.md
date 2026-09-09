# 本地 OCR 依赖

`python/` 是从原项目迁移并校验的便携 Python + RapidOCR/ONNX Runtime。微信 Provider 的 OCR 服务需要它；它是运行依赖，不是可随意删除的构建缓存。

源码仓库忽略这一第三方二进制目录。部署到新机器时复制该目录，或安装带 `rapidocr_onnxruntime` 的 Python。`PaddleOcrLocator` 会沿程序集目录向上寻找项目内 `runtime/python/python.exe` 和 `tools/ocr_server.py`，无需依赖特定工作目录或旧项目路径。

原有第三方 LICENSE / dist-info / 模型文件均保留。生成包可用 `tools/package.ps1` 复制便携运行环境。
