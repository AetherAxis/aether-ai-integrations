using System.Net;
using System.Text.RegularExpressions;

namespace Aether.SystemTools.AI.Web;

/// <summary>一条搜索结果（纯数据：URL + 标题 + 摘要 + 来源引擎）。</summary>
public sealed record WebSearchResult(string Url, string? Title, string? Snippet, string Source);

/// <summary>
/// 网页搜索的**唯一实现**：抓搜索引擎的 HTML 结果页再解析，不需要任何 API key。
/// <para>两个消费者共用它：⑭ 的 <see cref="Tools.WebSearchTool"/>（agent 在对话里调用）
/// 与 ⑮ 应用审查页的反查（<c>WebSiteSearch</c>）——一处改解析，两边同时受益。</para>
/// <para>先 Bing 再 DuckDuckGo：任一可用即返回；都失败才抛异常（调用方决定怎么呈现，
/// 不假装搜到了）。每个引擎各自 8 秒上限——某个引擎在特定网络下不可达时不能拖住整轮。</para>
/// </summary>
public sealed partial class WebSearchClient
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly TimeSpan PerEngineTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;

    /// <param name="http">外部可注入（宿主连接池）；不给就自己造一个——只用于搜索，连接复用足够。</param>
    public WebSearchClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>默认返回条数（够判定用，再多只是噪音）。</summary>
    public const int DefaultMaxResults = 5;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("搜索词不能为空。", nameof(query));
        var encoded = Uri.EscapeDataString(query);
        var max = Math.Clamp(maxResults, 1, 20);

        Exception? last = null;
        foreach (var (engine, url, parse) in new (string, string, Func<string, int, IReadOnlyList<WebSearchResult>>)[]
                 {
                     ("bing", $"https://www.bing.com/search?q={encoded}&setlang=zh-CN", ParseBing),
                     ("duckduckgo", $"https://html.duckduckgo.com/html/?q={encoded}", ParseDuckDuckGo),
                 })
        {
            try
            {
                using var engineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                engineCts.CancelAfter(PerEngineTimeout);
                var html = await GetHtmlAsync(url, engineCts.Token);
                var results = parse(html, max);
                if (results.Count > 0) return results;
                last = new InvalidOperationException($"{engine} 没有解析出结果（可能被反爬拦截）");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                last = ex;
            }
        }
        throw new InvalidOperationException($"网搜失败：{last?.Message ?? "两个引擎都没有结果"}");
    }

    private async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // UA / 语言按请求带：共享的 HttpClient 可能被别的用途改过，不该被我们污染或反过来被它影响。
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    // —— 解析 ——
    //
    // Bing 的结果页是「一个 <li class="b_algo"> 装一条结果」，里面有 <h2><a href=…>标题</a></h2>
    // 和 <p class="b_lineclamp*">摘要</p>。**不能**用一个 `<li …>.*?<h2>` 的正则跨块匹配：
    // 真实页面里第一个 b_algo 有时只是样式/广告壳（没有 h2），跨块匹配会把它的开头和**下一条**的标题
    // 配成一对；摘要按全页索引配对更糟——遇到没有摘要的结果，后面整体串位。
    // 这里先按块起点切成段，段内各自找标题和摘要，配对天然不会串。

    [GeneratedRegex(@"<li class=""b_algo""", RegexOptions.IgnoreCase)]
    private static partial Regex BingBlockRegex();

    [GeneratedRegex(
        @"<h2[^>]*>\s*<a[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BingTitleRegex();

    [GeneratedRegex(@"<p[^>]*class=""b_lineclamp[^""]*""[^>]*>(?<snippet>.*?)</p>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BingSnippetRegex();

    internal static IReadOnlyList<WebSearchResult> ParseBing(string html, int max)
    {
        var list = new List<WebSearchResult>();
        var blocks = BingBlockRegex().Matches(html).Select(m => m.Index).ToList();
        for (var b = 0; b < blocks.Count && list.Count < max; b++)
        {
            var start = blocks[b];
            var end = b + 1 < blocks.Count ? blocks[b + 1] : html.Length;
            var block = html[start..end];

            var title = BingTitleRegex().Match(block);
            if (!title.Success) continue;                     // 壳块/广告块：没有标题就跳过

            var url = WebUtility.HtmlDecode(title.Groups["url"].Value);
            if (!IsHttp(url)) continue;                       // 站内跳转（/ck/a?…）之类不要

            var snippet = BingSnippetRegex().Match(block);
            list.Add(new WebSearchResult(
                url,
                Clean(title.Groups["title"].Value),
                snippet.Success ? Clean(snippet.Groups["snippet"].Value) : null,
                "bing"));
        }
        return list;
    }

    [GeneratedRegex(@"<a[^>]*class=""result__a""[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DdgResultRegex();

    [GeneratedRegex(@"class=""result__snippet""[^>]*>(?<snippet>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DdgSnippetRegex();

    internal static IReadOnlyList<WebSearchResult> ParseDuckDuckGo(string html, int max)
    {
        var list = new List<WebSearchResult>();
        var snippets = DdgSnippetRegex().Matches(html).Select(m => Clean(m.Groups["snippet"].Value)).ToList();
        var i = 0;
        foreach (Match m in DdgResultRegex().Matches(html))
        {
            var url = WebUtility.HtmlDecode(m.Groups["url"].Value);
            // DDG 的跳转链（//duckduckgo.com/l/?uddg=<真实地址>）——解出真实目标。
            if (url.StartsWith("//duckduckgo.com/l/", StringComparison.OrdinalIgnoreCase))
            {
                var uddg = Regex.Match(url, @"uddg=(?<u>[^&]+)");
                if (uddg.Success) url = Uri.UnescapeDataString(uddg.Groups["u"].Value);
            }
            if (!IsHttp(url)) continue;
            list.Add(new WebSearchResult(
                url,
                Clean(m.Groups["title"].Value),
                i < snippets.Count ? snippets[i] : null,
                "duckduckgo"));
            i++;
            if (list.Count >= max) break;
        }
        return list;
    }

    private static bool IsHttp(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>去标签 + 解 HTML 实体 + 压空白（候选标题/摘要要能在界面上直接显示）。</summary>
    private static string Clean(string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
