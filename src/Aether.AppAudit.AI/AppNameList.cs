using System.Text.RegularExpressions;

namespace Aether.AppAudit.AI;

/// <summary>
/// 审查清单的解析：用户**手上有名字**（手机上用 Chrome 装成一类的应用/快捷方式显示名），
/// 一行粘贴一条即可——这页不扫本机，名字是输入，不是发现出来的东西。
/// <para>容忍真实清单的样子：空行、序号前缀（<c>1. </c> / <c>2) </c> / <c>1、</c>）、项目符号（<c>- </c> / <c>• </c>）、
/// 行尾顺带的网址（<c>名字 https://…</c>）——有网址就等于自带一条站点证据，不必只靠网搜猜。</para>
/// </summary>
public static partial class AppNameList
{
    /// <summary>一行一个名字 → (名字, 可选网址)。保持输入顺序，重复名字交给 store 去重。</summary>
    public static IReadOnlyList<(string Name, string? ShortcutUrl)> Parse(string text)
    {
        var list = new List<(string, string?)>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            line = LeadingIndexRegex().Replace(line, string.Empty);   // 去掉 "1. " / "2) " / "1、" / "- " / "• "
            if (line.Length == 0) continue;

            string? url = null;
            var space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space > 0)
            {
                var tail = line[(space + 1)..].Trim();
                if (IsUrl(tail))
                {
                    url = tail;
                    line = line[..space].Trim();
                }
            }

            if (line.Length == 0) continue;
            list.Add((line, url));
        }
        return list;
    }

    private static bool IsUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(\d+[\.\)、]\s*|[-*•]\s*)")]
    private static partial Regex LeadingIndexRegex();
}
