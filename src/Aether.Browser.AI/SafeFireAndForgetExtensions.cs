using System.Diagnostics;

namespace Aether.Browser.AI;

/// <summary>fire-and-forget Task 扩展（模块自持——⑭ 不依赖 ⑮ 的 App.Core 同名件）：
/// await + 记异常到 trace 通道，防 UnobservedTaskException 静默吞错误。</summary>
internal static class SafeFireAndForgetExtensions
{
    public static async void SafeFireAndForget(this Task task)
    {
        try { await task; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SafeFireAndForget] {ex}");
        }
    }
}
