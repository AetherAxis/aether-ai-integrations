using System.Reflection;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Aether.AppAudit.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.AppAudit.AI;

/// <summary>
/// 「应用审查」集成模块的注册入口——**一个领域一个模块**（新领域 = ⑭ 新建一个 Aether.&lt;Domain&gt;.AI）。
/// <para>这个模块自带：数据模型、落盘、任务板、判定口径 skill、agent 工具。宿主（任何 app/CLI/服务）
/// 只要 <c>AddAppAuditTools(dataDir, skillsDir)</c> 就能让它的 agent 取任务、写结论；
/// UI 想要「应用审查页」也只是把 <see cref="AppAuditStore"/> 当数据源来显示——**UI 跟工具互不知道对方存在**。</para>
/// <para>判定逻辑不在本模块里：工具只取任务/写结论，怎么判由 agent 自己决定（见
/// <see cref="AppAuditSkill"/> 的正文，可改 Markdown 调口径）。</para>
/// </summary>
public static class AppAuditAIBuilderExtensions
{
    /// <summary>本模块注册的工具名（宿主可据此做工具白名单/能力策略）。</summary>
    public static readonly string[] ToolNames =
        { "ListAuditTasks", "SaveAppVerdict", "ReadAppVerdict" };

    /// <summary>
    /// 注册应用审查工具（仅工具——适配器约定，不碰宿主的 UI）。
    /// </summary>
    /// <param name="dataDirectory">数据目录：记录落 <c>&lt;dataDirectory&gt;/app-audit.json</c>（跨端同一份的关键：两端指同一个目录）。</param>
    /// <param name="skillsDirectory">skills 目录：非空时把内置判定口径种进 <c>&lt;skillsDirectory&gt;/app-audit/SKILL.md</c>
    /// （缺就写、旧版默认值就升级、用户改过的不动）。传 null = 不种文件，skill 由宿主自行分发。</param>
    /// <param name="filePath">直接指定记录文件路径（给了就忽略 <paramref name="dataDirectory"/>；测试/多实例用）。</param>
    public static AetherAIBuilder AddAppAuditTools(this AetherAIBuilder builder,
        string dataDirectory, string? skillsDirectory = null, string? filePath = null)
    {
        var path = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(dataDirectory, "app-audit.json")
            : filePath;

        var s = builder.Services;
        s.AddSingleton(new AppAuditStore(path));
        s.AddSingleton<AppAuditService>();
        s.AddTransient<AppAuditTool>();

        if (!string.IsNullOrWhiteSpace(skillsDirectory))
            AppAuditSkill.EnsureSeeded(skillsDirectory);

        const string cat = "应用审查";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("ListAuditTasks", "读取应用审查页发布的任务（待处理的应用名清单）", cat,
                sp => CreateTool<AppAuditTool>(sp, "ListAuditTasks"), ToolApprovalLevel.Safe);
            reg.Register("SaveAppVerdict", "把判定结论写入应用审查页（追加一次判定，保留历史，该条从任务池完成）", cat,
                sp => CreateTool<AppAuditTool>(sp, "SaveAppVerdict"), ToolApprovalLevel.Safe);
            reg.Register("ReadAppVerdict", "读取应用审查页里某个名字已有的结论与判定次数", cat,
                sp => CreateTool<AppAuditTool>(sp, "ReadAppVerdict"), ToolApprovalLevel.Safe);
        });
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string method) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var m = typeof(T).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(m, tool);
    }
}
