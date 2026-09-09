namespace WeChatDriver.Capture;

/// <summary>定位 RapidOCR 服务脚本 tools/ocr_server.py。</summary>
public static class PaddleOcrLocator
{
    public static string? Find()
    {
        // 从程序集目录向上查找项目根目录下的 tools/ocr_server.py。
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = baseDir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "tools", "ocr_server.py");
            if (File.Exists(candidate)) return candidate;
        }

        // 兜底：当前工作目录。
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "tools", "ocr_server.py");
        return File.Exists(cwd) ? cwd : null;
    }

    /// <summary>
    /// 定位 Python 解释器。优先返回「已安装 rapidocr_onnxruntime」的解释器
    /// （OCR 实际可用的那个），否则返回第一个可用的 python。
    /// 常见位置：workbuddy 自带 → AppData 下 pythoncore → Program Files Python → 系统 PATH。
    /// </summary>
    public static string? FindPython()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        // 0. 产品目录下的便携 Python（交付打包时放在 runtime/python）。
        var portable = Path.Combine(AppContext.BaseDirectory, "runtime", "python");
        AddPython(candidates, portable);
        // 开发时也可能从程序集目录向上若干层找到 runtime/python。
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            AddPython(candidates, Path.Combine(d.FullName, "runtime", "python"));

        // 1. workbuddy 自带 Python（按版本倒序）。
        var versions = Path.Combine(home, ".workbuddy", "binaries", "python", "versions");
        if (Directory.Exists(versions))
            foreach (var dir in Directory.GetDirectories(versions).OrderByDescending(d => d))
                AddPython(candidates, dir);

        // 2. AppData\Local\Python\pythoncore-*（pip 默认装 rapidocr 的常见位置）。
        var localPython = Path.Combine(home, "AppData", "Local", "Python");
        if (Directory.Exists(localPython))
            foreach (var dir in Directory.GetDirectories(localPython, "pythoncore-*").OrderByDescending(d => d))
                AddPython(candidates, dir);

        // 3. AppData\Local\Programs\Python\*（官方安装器默认位置）。
        var programsPython = Path.Combine(home, "AppData", "Local", "Programs", "Python");
        if (Directory.Exists(programsPython))
            foreach (var dir in Directory.GetDirectories(programsPython).OrderByDescending(d => d))
                AddPython(candidates, dir);

        // 4. 系统 PATH 里找 python.exe。
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';'))
            if (!string.IsNullOrWhiteSpace(dir))
                AddPython(candidates, dir.Trim());

        // 优先返回安装了 rapidocr 的解释器。
        foreach (var c in candidates)
            if (HasRapidOcr(c)) return c;

        return candidates.FirstOrDefault();
    }

    private static void AddPython(List<string> list, string dir)
    {
        var exe = Path.Combine(dir, "python.exe");
        if (File.Exists(exe) && !list.Contains(exe, StringComparer.OrdinalIgnoreCase))
            list.Add(exe);
    }

    /// <summary>快速探测：该解释器的 Lib/site-packages 下是否装了 rapidocr_onnxruntime。</summary>
    private static bool HasRapidOcr(string pythonExe)
    {
        var dir = Path.GetDirectoryName(pythonExe);
        if (dir is null) return false;
        var sp = Path.Combine(dir, "Lib", "site-packages");
        if (!Directory.Exists(sp)) return false;

        return Directory.Exists(Path.Combine(sp, "rapidocr_onnxruntime"))
            || Directory.GetDirectories(sp, "rapidocr_onnxruntime*", SearchOption.TopDirectoryOnly).Length > 0;
    }
}
