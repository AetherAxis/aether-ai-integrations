using System.Reflection;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.SystemTools.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.SystemTools.AI;

/// <summary>
/// 系统工具适配器：文件/搜索/HTTP/命令 15 个工具，注册进 <c>ToolRegistry</c>。
/// 元数据与工厂在 DI 期登记，<see cref="AIFunction"/> 只在 agent 解析工具时才实例化——
/// 缺少依赖的宿主不会在启动阶段失败。
/// </summary>
public static class SystemToolsAIBuilderExtensions
{
    public static readonly string[] ToolNames =
    {
        "ReadFile", "WriteFile", "EditFile", "LineEditFile", "DeleteFile", "MoveFile",
        "Grep", "Glob", "ExecuteCommand", "ExploreDirectory",
        "ExecuteShellCommand", "FetchUrl", "DiffFile",
        "WebSearch",
        "ApplyPatch", "ApplyPatchWithDelete",
    };

    /// <summary>命令执行类工具名（<c>ExecuteCommand</c> / <c>ExecuteShellCommand</c>）——spawn 子进程，
    /// iOS 真机沙盒禁止 fork，故标记 <see cref="HostCapability.DesktopOnly"/>，手机端自动跳过。</summary>
    public static readonly string[] CommandToolNames = { "ExecuteCommand", "ExecuteShellCommand" };

    public static AetherAIBuilder AddSystemTools(this AetherAIBuilder builder)
        => AddSystemTools(builder, includeCommandTools: true);

    /// <summary>
    /// 注册系统工具（文件/搜索/HTTP/命令）。
    /// <paramref name="includeCommandTools"/>=false 时连注册都跳过 <c>ExecuteCommand</c>/<c>ExecuteShellCommand</c>——
    /// 手机端（iOS 沙盒禁止 fork）用此入口只拿文件/搜索/HTTP 的可移植子集。
    /// 保留命令工具时它们仍标 <see cref="HostCapability.DesktopOnly"/>，由 <c>ToolRegistry</c> 按当前宿主过滤。
    /// </summary>
    public static AetherAIBuilder AddSystemTools(this AetherAIBuilder builder, bool includeCommandTools)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        services.AddHttpClient();
        services.AddTransient<WebFetchTool>(sp => new WebFetchTool(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
        // 搜索与抓取共用宿主的连接池（重定向/超时策略由宿主统一配）。
        services.AddTransient<WebSearchTool>(sp => new WebSearchTool(sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
        services.AddTransient<FileReadTool>();
        services.AddTransient<FileWriteTool>();
        services.AddTransient<FileEditTool>();
        services.AddTransient<FileLineEditTool>();
        services.AddTransient<FileDeleteTool>();
        services.AddTransient<FileMoveTool>();
        services.AddTransient<GrepTool>();
        services.AddTransient<GlobTool>();
        services.AddTransient<DirectoryExplorerTool>();
        services.AddTransient<FileDiffTool>();
        services.AddTransient<ApplyPatchTool>();
        if (includeCommandTools)
        {
            services.AddTransient<CommandExecutorTool>();
            services.AddTransient<ShellCommandTool>();
        }

        const string category = "系统工具";
        builder.ConfigureToolRegistry(registry =>
        {
            registry.Register("ReadFile", "读取文件内容（文本/图片）", category,
                sp => CreateTool<FileReadTool>(sp, "ReadFile"));
            registry.Register("WriteFile", "写入文件内容", category,
                sp => CreateTool<FileWriteTool>(sp, "WriteFile"));
            registry.Register("EditFile", "编辑文件（字符串替换）", category,
                sp => CreateTool<FileEditTool>(sp, "EditFile"));
            registry.Register("LineEditFile", "按行编辑文件", category,
                sp => CreateTool<FileLineEditTool>(sp, "LineEditFile"));
            registry.Register("DeleteFile", "删除文件", category,
                sp => CreateTool<FileDeleteTool>(sp, "DeleteFile"), ToolApprovalLevel.Dangerous);
            registry.Register("MoveFile", "移动或重命名文件", category,
                sp => CreateTool<FileMoveTool>(sp, "MoveFile"), ToolApprovalLevel.Confirm);
            registry.Register("Grep", "正则搜索文件内容", category,
                sp => CreateTool<GrepTool>(sp, "Grep"));
            registry.Register("Glob", "按模式匹配文件（如 *.cs）", category,
                sp => CreateTool<GlobTool>(sp, "Glob"));
            registry.Register("ExploreDirectory", "浏览目录结构", category,
                sp => CreateTool<DirectoryExplorerTool>(sp, "ExploreDirectory"));
            registry.Register("FetchUrl", "抓取网页内容", category,
                sp => CreateTool<WebFetchTool>(sp, "FetchUrl"));
            // 搜索：只读网络请求，两端同源可用（不 spawn 进程、不写本地）。
            registry.Register("WebSearch", "搜索网页，返回候选站点（标题/URL/摘要）", category,
                sp => CreateTool<WebSearchTool>(sp, "WebSearch"));
            registry.Register("DiffFile", "比较文件差异", category,
                sp => CreateTool<FileDiffTool>(sp, "DiffFile"));

            // 多文件批量编辑：纯 create/edit/lineEdit → Safe；含 delete 的批次走
            // ApplyPatchWithDelete（Dangerous）——delete 不可逆、无内容 diff。
            registry.Register("ApplyPatch", "批量编辑多个文件（create/edit/lineEdit，不含删除）", category,
                sp => CreateTool<ApplyPatchTool>(sp, "ApplyPatch"));
            registry.Register("ApplyPatchWithDelete", "批量编辑多个文件（含删除，危险）", category,
                sp => CreateTool<ApplyPatchTool>(sp, "ApplyPatchWithDelete"), ToolApprovalLevel.Dangerous);

            if (includeCommandTools)
            {
                registry.Register("ExecuteCommand", "执行命令行程序", category,
                    sp => CreateTool<CommandExecutorTool>(sp, "ExecuteCommand"),
                    ToolApprovalLevel.Dangerous, HostCapability.DesktopOnly);
                registry.Register("ExecuteShellCommand", "执行 Shell 命令", category,
                    sp => CreateTool<ShellCommandTool>(sp, "ExecuteShellCommand"),
                    ToolApprovalLevel.Dangerous, HostCapability.DesktopOnly);
            }
        });

        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider services, string method) where T : class
    {
        var tool = services.GetRequiredService<T>();
        var target = typeof(T).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(target, tool);
    }
}
