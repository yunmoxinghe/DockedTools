using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DockedAI.功能.工具;

/// <summary>
/// SVG 图标尺寸批量调整工具
/// 只改 &lt;svg&gt; 根标签上的 width / height，不动 viewBox、也不动嵌套元素
/// </summary>
public static class 图标调整工具
{
    // ⚠️ 两条规矩，都是老实现的坑：
    //
    // 一、作用域锁在根标签。老写法是拿 width\s*=\s*"[^"]*" 在【整个文件】上全局替换，
    //     而 "-" 不是单词字符，所以 stroke-width="1" 中间的 width="1" 也算命中，
    //     一并被改成 width="100"。Assets/logos/Alipay.svg 第 28 行那个
    //     stroke="none" stroke-width="100" 就是这么来的 —— stroke 都关了却要 100 的
    //     描边宽度，自相矛盾；Figma/Sketch 导出这里默认是 1。git 里这文件只有一次导入
    //     提交，改前的值取不回来，所以只能判「极可能是当年那次全局替换的连带损伤」，
    //     现在也没有回头改它（stroke="none"，视觉无影响，改了反而是在猜）。
    //
    // 二、属性名前面卡 (?<![-\w])：- 和单词字符都不许紧邻，
    //     这样 stroke-width / minwidth 之类都进不来，只有独立的 width / height 命中。
    // 三个正则都不加 RegexOptions.Compiled：这类单文件脚本走 file-based app，
    // 编译配置由 SDK 决定，Compiled 在 Native AOT 下并非总能如愿（可能直接不支持），
    // 而它在这里省的那点编译时间对一个处理 4 个图标的工具毫无意义 —— 不值得为此留一个雷。
    private static readonly Regex 根SVG标签 =
        new(@"<svg\b[^>]*>", RegexOptions.IgnoreCase);

    private static readonly Regex 宽度属性 =
        new(@"(?<![-\w])width\s*=\s*""[^""]*""", RegexOptions.IgnoreCase);

    private static readonly Regex 高度属性 =
        new(@"(?<![-\w])height\s*=\s*""[^""]*""", RegexOptions.IgnoreCase);

    /// <summary>
    /// 批量调整指定目录中所有 SVG 文件的根标签尺寸
    /// </summary>
    /// <param name="directoryPath">目标目录（不递归）</param>
    /// <param name="targetWidth">目标宽度（像素）</param>
    /// <param name="targetHeight">目标高度（像素）</param>
    /// <param name="dryRun">只报告会改哪些文件，不写盘</param>
    /// <returns>失败的文件数，供入口当退出码用</returns>
    public static int 批量调整SVG尺寸(string directoryPath, int targetWidth = 100, int targetHeight = 100, bool dryRun = false)
    {
        var svgFiles = Directory.GetFiles(directoryPath, "*.svg");

        Console.WriteLine($"找到 {svgFiles.Length} 个 SVG 文件{(dryRun ? "（-DryRun：只预演，不写文件）" : "")}");
        Console.WriteLine();

        var changed = 0;
        var failed = 0;

        foreach (var filePath in svgFiles)
        {
            try
            {
                if (调整单个SVG尺寸(filePath, targetWidth, targetHeight, dryRun))
                {
                    Console.WriteLine($"✅ 已处理: {Path.GetFileName(filePath)}");
                    changed++;
                }
                else
                {
                    // 已经是目标尺寸 —— 老实现会把这种文件也重写一遍，现在原样留着
                    Console.WriteLine($"⏭  无需改动: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 处理失败 {Path.GetFileName(filePath)}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(dryRun
            ? $"预演结束！共 {svgFiles.Length} 个：会改 {changed}，失败 {failed}。"
            : $"批量调整完成！共 {svgFiles.Length} 个：改动 {changed}，失败 {failed}。");

        return failed;
    }

    /// <summary>
    /// 调整单个 SVG 文件根标签上的 width / height
    /// </summary>
    /// <returns>内容变了才返回 true；本来就是目标尺寸返回 false，调用方据此决定要不要写盘</returns>
    private static bool 调整单个SVG尺寸(string filePath, int targetWidth, int targetHeight, bool dryRun)
    {
        // 先按字节读一次，只为了看清有没有 BOM：
        // File.WriteAllText 不带编码参数时写的是「无 BOM 的 UTF-8」，
        // 原来带 BOM 的文件会被顺手改掉编码。实测这批 logo（4 个）都不带 BOM，
        // 但工具不该拿别人的文件练手 —— 原来是什么编码就写回什么编码。
        var raw = File.ReadAllBytes(filePath);
        var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var content = Encoding.UTF8.GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));

        var root = 根SVG标签.Match(content);
        if (!root.Success)
        {
            // 读不懂就别动：宁可报错跳过，也不要凭猜写坏一个图标
            throw new InvalidDataException("没找到 <svg> 根标签，为安全起见不改这个文件");
        }

        var 原标签 = root.Value;
        var 新标签 = 替换或追加属性(原标签, 宽度属性, "width", targetWidth);
        新标签 = 替换或追加属性(新标签, 高度属性, "height", targetHeight);

        if (新标签 == 原标签)
        {
            return false;
        }

        content = content.Remove(root.Index, root.Length).Insert(root.Index, 新标签);

        if (!dryRun)
        {
            File.WriteAllText(filePath, content, new UTF8Encoding(hasBom));
        }

        return true;
    }

    /// <summary>
    /// 根标签里有这个属性就替换，没有就插到标签名后面（其余属性一律不动）
    /// </summary>
    private static string 替换或追加属性(string 标签, Regex 属性, string 名称, int 值)
    {
        var 替换成 = $"{名称}=\"{值}\"";

        if (属性.IsMatch(标签))
        {
            return 属性.Replace(标签, 替换成);
        }

        // 用 ^<svg\b 而不是 <svg\s+：后者要求标签名后面必须紧跟空白，
        // 碰到 <svg> / <svg/> 这种一个属性都没有的根标签就插不进去，
        // 于是新标签 == 原标签 → 报「无需改动」，明明缺尺寸却被静默跳过。
        return Regex.Replace(标签, @"(?i)^<svg\b", "<svg " + 替换成);
    }

    /// <summary>
    /// 命令行入口。在仓库根目录下执行：
    ///
    ///   dotnet run --file 脚本工具\图标调整工具.cs
    ///   dotnet run --file 脚本工具\图标调整工具.cs -- -DryRun
    ///   dotnet run --file 脚本工具\图标调整工具.cs -- DockedTools\Assets\logos 128 128
    ///
    /// 【为什么把 Main 放回来】这一段原先被整段注释掉，理由是「避免与主应用程序冲突」，
    /// 结果 README 里文档化的 dotnet run --file 根本没有入口点，跑不起来。
    /// 冲突其实不存在：本文件在仓库根的 脚本工具\ 下，DockedTools 工程在 DockedTools\ 下，
    /// 默认 Compile glob 收不到隔壁目录，DockedTools.slnx 里也只有那一个工程。
    /// </summary>
    public static int Main(string[] args)
    {
        var dryRun = false;
        var positional = new List<string>();

        foreach (var arg in args)
        {
            if (string.Equals(arg, "-DryRun", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else
            {
                positional.Add(arg);
            }
        }

        Console.WriteLine("SVG 图标批量调整工具");
        Console.WriteLine("====================");

        // 目标目录：位置参数 > 从当前目录能找到的两个已知位置。
        // 老写法只拼 cwd\Assets\logos，可在仓库根执行时真实路径是 DockedTools\Assets\logos，
        // 那个目录并不存在 —— 默认参数本身就指错了。
        string logosPath;

        if (positional.Count > 0)
        {
            logosPath = Path.GetFullPath(positional[0]);
        }
        else
        {
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "DockedTools", "Assets", "logos"),
                Path.Combine(cwd, "Assets", "logos"),
            };

            var found = Array.Find(candidates, p => Directory.Exists(p));

            if (found == null)
            {
                Console.WriteLine("❌ 找不到 logos 目录。把它当第一个位置参数传进来：");
                Console.WriteLine("   dotnet run --file 脚本工具\\图标调整工具.cs -- <目录> [宽] [高]");
                return 2;
            }

            logosPath = found;
        }

        // 位置参数给的目录只做了 GetFullPath，不检查一下存在性就会让
        // Directory.GetFiles 抛 DirectoryNotFoundException 把堆栈直接甩给用户 ——
        // 参数错误应该走退出码 2，不是未处理异常。
        if (!Directory.Exists(logosPath))
        {
            Console.WriteLine($"❌ 目录不存在: {logosPath}");
            Console.WriteLine("   用法: dotnet run --file 脚本工具\\图标调整工具.cs -- <目录> [宽] [高]");
            return 2;
        }

        var width = 100;
        var height = 100;

        if (positional.Count >= 2 && !int.TryParse(positional[1], out width))
        {
            Console.WriteLine($"❌ 宽度不是整数: {positional[1]}");
            return 2;
        }

        if (positional.Count >= 3 && !int.TryParse(positional[2], out height))
        {
            Console.WriteLine($"❌ 高度不是整数: {positional[2]}");
            return 2;
        }

        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"❌ 尺寸必须是正整数，收到 {width}×{height}");
            return 2;
        }

        Console.WriteLine($"目标目录: {logosPath}");
        Console.WriteLine($"目标尺寸: {width}×{height} 像素");
        Console.WriteLine();

        // 返回值 = 失败文件数，直接当进程退出码；不再 Console.ReadKey()
        // （结尾等按键会让它在管道 / 非交互环境下卡住）
        return 批量调整SVG尺寸(logosPath, width, height, dryRun);
    }
}
