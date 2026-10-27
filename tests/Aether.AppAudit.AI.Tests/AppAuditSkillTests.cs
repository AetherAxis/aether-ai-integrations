using Aether.AppAudit.AI;
using Xunit;

namespace Aether.AppAudit.AI.Tests;

/// <summary>
/// 判定标准是 skill，不是代码：首次运行落盘、之后以盘上文件为准、文件没了才回退内置正文。
/// 这条链保证「改口径不用重新编译」，同时「少个 md 也不能判不了」。
/// </summary>
public class AppAuditSkillTests : IDisposable
{
    private readonly string _dir;

    public AppAuditSkillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"aether-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    [Fact]
    public void 首次运行落盘并去掉frontmatter()
    {
        var body = AppAuditSkill.EnsureSeeded(_dir);

        Assert.True(File.Exists(AppAuditSkill.PathFor(_dir)));
        Assert.DoesNotContain("---", body[..Math.Min(20, body.Length)]);      // frontmatter 不进提示词
        Assert.StartsWith("# 应用审查", body);                                 // 正文从标题开始
        Assert.Contains("siteType", body);                                     // 字段口径在里面
        Assert.Contains("SaveAppVerdict", body);                               // 写回路径写在 skill 里
    }

    /// <summary>已存在就不覆盖——用户可能已经调过口径，重启 App 不能把改动冲掉。</summary>
    [Fact]
    public void 已有文件不被覆盖()
    {
        Directory.CreateDirectory(Path.Combine(_dir, AppAuditSkill.Name));
        File.WriteAllText(AppAuditSkill.PathFor(_dir), "---\nname: app-audit\n---\n\n我自己的口径：只认体育博彩。");

        var body = AppAuditSkill.EnsureSeeded(_dir);

        Assert.Equal("我自己的口径：只认体育博彩。", body);
        Assert.Contains("我自己的口径", File.ReadAllText(AppAuditSkill.PathFor(_dir)));
    }

    /// <summary>
    /// 升级也要守住「不覆盖用户改动」：盘上内容若还是**旧版默认正文**（说明用户没改过）就升级成新版；
    /// 内容被改过则一律不动（上面那条测试）。
    /// </summary>
    [Fact]
    public void 旧版默认正文会被升级()
    {
        Directory.CreateDirectory(Path.Combine(_dir, AppAuditSkill.Name));
        File.WriteAllText(AppAuditSkill.PathFor(_dir), AppAuditSkill.PreviousDefaults[0]);

        var body = AppAuditSkill.EnsureSeeded(_dir);

        Assert.Equal(AppAuditSkill.DefaultContent, File.ReadAllText(AppAuditSkill.PathFor(_dir)));
        Assert.Contains("SaveAppVerdict", body);
    }

    /// <summary>文件缺失/目录为空 → 回退内置正文。</summary>
    [Fact]
    public void 文件缺失时回退内置正文()
    {
        Assert.Equal(AppAuditSkill.DefaultContent, AppAuditSkill.LoadOrDefault(_dir));
        Assert.Equal(AppAuditSkill.DefaultContent, AppAuditSkill.LoadOrDefault(null));
    }

    [Theory]
    [InlineData("---\nname: x\n---\n正文", "正文")]
    [InlineData("---\r\nname: x\r\n---\r\n正文", "正文")]
    [InlineData("没有 frontmatter", "没有 frontmatter")]
    [InlineData("---\nname: x\n没有结束分隔符", "---\nname: x\n没有结束分隔符")]
    public void 去frontmatter(string input, string expected)
    {
        Assert.Equal(expected, AppAuditSkill.StripFrontMatter(input));
    }
}
