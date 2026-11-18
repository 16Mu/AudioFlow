using System.Text.Json;

namespace AudioFlow.Core;

public static class UserError
{
    public static string Describe(Exception error) => error switch
    {
        GraphValidationException => error.Message,
        FileNotFoundException or DirectoryNotFoundException => "找不到这个文件或文件夹，请重新选择。",
        UnauthorizedAccessException => "Windows 不允许访问这里，请换一个保存位置再试。",
        JsonException => "这个设置文件打不开，可能已损坏，请换一个文件。",
        IOException => "文件暂时无法读写，请关闭正在使用它的软件，或换一个保存位置。",
        OperationCanceledException => "操作已取消。",
        InvalidOperationException or NotSupportedException when error.Message.Any(c => c is >= '\u4e00' and <= '\u9fff') => error.Message,
        _ => "请检查软件和设备是否已打开，再试一次。"
    };
}
