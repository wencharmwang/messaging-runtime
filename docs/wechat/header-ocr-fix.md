# 2026-09-04 标题 OCR 与发送结果分类修复

存在两个独立的发送前阻断：窗口尺寸 1296x1388 偏离旧标定 1118x809，被标为 Degraded，旧发送前检查拒绝所有非 Healthy 状态；同时列表中存在唯一 Wencharm，但原始标题 OCR 输出 Wenham，2x/3x 缩放输出 Wencham，后续精确标题验证也会失败。Windows 中文 OCR 对原始标题未给出正确结果。旧 Runtime 把 Provider 异常统一标为 send_uncertain，掩盖了具体原因。

窗口尺寸与 DPI 警告不再直接禁止发送：驱动要求当前动态布局检测成功，并继续在每个事件前验证 HWND/PID、几何位置和前台焦点；最小化、屏幕外及不可用窗口仍拒绝。diagnose_provider 增加 window_health，显示具体诊断原因。

对实际捕获标题局部进行 20 组只读识别实验后，灰度阈值 120 二值化再 3x 双三次放大，使本机 Windows OCR 准确输出 Wencharm。ConversationHeaderReader 为独立 OCR 增加此通用图像预处理，不依赖联系人名称、不替换相似字符、不放宽精确匹配。缓存仍绑定标题像素哈希，标题变化不能复用旧识别结果。

标题校验失败改为 conversation_unverified，准确说明列表行已找到但标题无法验证。Windows 发送驱动在进入提交方法前的异常使用 SendNotAttemptedException 报告；Runtime 持久化 not_sent 与具体原因。提交方法开始后仍统一保守处理为 uncertain。任何终态请求均不自动重试。

验证：148 项现有及新增测试通过，覆盖标题变化后缓存失效、预处理独立识别、不得模糊替换收件人，以及 not_sent / uncertain 的不同处理与幂等。
