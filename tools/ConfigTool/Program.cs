using System.Text;
using UnifiedPicoModule.Configuration;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("Unified PICO 配置工具（不会安装模块或修改旧配置）\n  validate <已有新配置.json>\n  defaults <未占用输出.json>\n  import <显式旧文件> <sourceVersion> <未占用新配置.json> <未占用报告.txt>\nsourceVersion: " + string.Join(", ", LegacyImporter.SourceVersions));
        return 0;
    }
    switch (args[0])
    {
        case "validate" when args.Length == 2:
            if (!File.Exists(args[1])) throw new FileNotFoundException("validate 只接受已有文件，不创建默认文件。", args[1]);
            var diagnostics = new List<string>();
            ConfigStore.ReadOnly(args[1], diagnostics.Add);
            foreach (var message in diagnostics) Console.WriteLine(message);
            Console.WriteLine("只读校验完成，原文件未重写。非法字段使用回退/禁用规则；诊断须人工确认。");
            return diagnostics.Any(m => !m.StartsWith("启用 ", StringComparison.Ordinal)) ? 2 : 0;
        case "defaults" when args.Length == 2:
            ConfigStore.SaveNew(args[1], new ModuleConfig());
            Console.WriteLine("已写安全默认：" + Path.GetFullPath(args[1]));
            return 0;
        case "import" when args.Length == 5:
            var source = Path.GetFullPath(args[1]);
            var output = Path.GetFullPath(args[3]);
            var reportPath = Path.GetFullPath(args[4]);
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) || string.Equals(source, reportPath, StringComparison.OrdinalIgnoreCase) || string.Equals(output, reportPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输入、新配置和报告必须是三个不同路径。");
            if (File.Exists(output) || File.Exists(reportPath) || Directory.Exists(output) || Directory.Exists(reportPath))
                throw new IOException("输出路径已占用；不会覆盖。请指定两个未占用路径。");
            var imported = LegacyImporter.Import(source, args[2]);
            // Reserve the report first, before any config output. Competing writers cannot overwrite it.
            using (var reportFile = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(reportFile, new UTF8Encoding(false));
                writer.WriteLine(string.Join(Environment.NewLine, imported.Report));
                writer.Flush();
                ConfigStore.SaveNew(output, imported.Config);
                writer.WriteLine("新配置成功写入：" + output);
            }
            Console.WriteLine($"已写新配置：{output}\n迁移报告：{reportPath}\n原文件保留；请先阅读报告，不会自动安装或应用。");
            return 0;
        default:
            throw new ArgumentException("命令或参数数量非法；使用 --help 查看语法。");
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
{
    Console.Error.WriteLine("失败：" + ex.Message + "；未覆盖已有文件。如输出部分完成，请检查指定路径；不会自动删除。");
    return 1;
}
