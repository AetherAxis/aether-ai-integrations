using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// Web 内容获取工具 — 获取指定 URL 的内容，用于查阅 API 文档、NuGet 包说明、代码示例等。
/// 自动将 HTML 转为纯文本，截断超长响应。
/// </summary>
public class WebFetchTool
{
    private readonly HttpClient _httpClient;

    public WebFetchTool(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [System.ComponentModel.Description("获取 URL 内容，用于查阅 API 文档和代码示例")]
    public async Task<Output> FetchUrl(
        [System.ComponentModel.Description("URL 地址")] string url,
        [System.ComponentModel.Description("最大返回内容长度（字符数），默认10000")] int? maxLength = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new Output { Success = false, Error = "URL 不能为空" };

        var effectiveMaxLength = maxLength ?? 10000;

        try
        {
            // 确保 URL 有协议
            var effectiveUrl = url;
            if (!effectiveUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !effectiveUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                effectiveUrl = $"https://{url}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, effectiveUrl);
            request.Headers.Add("User-Agent", "AlphaAgent/1.0 (AI Agent Tool)");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var rawContent = await response.Content.ReadAsStringAsync(cancellationToken);

            // 简易 HTML → 纯文本转换
            var text = IsHtmlContent(contentType, effectiveUrl)
                ? StripHtml(rawContent)
                : rawContent;

            // 截断
            var truncated = text.Length > effectiveMaxLength;
            var resultText = truncated
                ? text[..effectiveMaxLength] + $"\n\n... (已截断，总长度 {text.Length} 字符)"
                : text;

            return new Output
            {
                Success = true,
                Content = resultText,
                Url = effectiveUrl,
                ContentType = contentType,
                ContentLength = text.Length,
                Truncated = truncated,
            };
        }
        catch (HttpRequestException ex)
        {
            return new Output { Success = false, Error = $"HTTP 请求失败: {ex.Message}" };
        }
        catch (TaskCanceledException)
        {
            return new Output { Success = false, Error = "请求超时" };
        }
        catch (Exception ex)
        {
            return new Output { Success = false, Error = $"获取 URL 内容失败: {ex.Message}" };
        }
    }

    private static bool IsHtmlContent(string contentType, string url)
    {
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return true;
        // 无 Content-Type 时，根据 URL 扩展名判断
        if (url.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// 简易 HTML 标签剥离 — 移除所有 &lt;...&gt; 标签，解码常见实体，
    /// 将 block 元素转为换行。不依赖第三方 HTML 解析库。
    /// </summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        var sb = new StringBuilder(html.Length);
        var inTag = false;
        var inScript = false;

        for (var i = 0; i < html.Length; i++)
        {
            var c = html[i];

            if (inTag)
            {
                if (c == '>')
                {
                    inTag = false;
                    // 检查是否是 block 结束标签 → 换行
                    if (i >= 3)
                    {
                        var tagEnd = html.Substring(Math.Max(0, i - 20), Math.Min(20, i));
                        if (tagEnd.Contains("/p>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/div>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/li>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/h1>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/h2>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/h3>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/pre>", StringComparison.OrdinalIgnoreCase) ||
                            tagEnd.Contains("/code>", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine();
                        }
                    }
                }
                continue;
            }

            if (c == '<')
            {
                inTag = true;
                // 检查 script/style 标签
                if (i + 7 < html.Length)
                {
                    var peek = html.Substring(i + 1, Math.Min(6, html.Length - i - 1)).ToLowerInvariant();
                    if (peek.StartsWith("script") || peek.StartsWith("style"))
                        inScript = true;
                    if (peek.StartsWith("/script") || peek.StartsWith("/style"))
                        inScript = false;
                }
                continue;
            }

            if (inScript)
                continue;

            sb.Append(c);
        }

        var result = sb.ToString();

        // 解码常见 HTML 实体
        result = result
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");

        // 压缩连续空行为最多两个
        var lines = result.Split('\n');
        var compressed = new StringBuilder();
        var emptyCount = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                emptyCount++;
                if (emptyCount <= 2)
                    compressed.AppendLine();
            }
            else
            {
                emptyCount = 0;
                compressed.AppendLine(line.TrimEnd());
            }
        }

        return compressed.ToString().Trim();
    }

    public class Output
    {
        public bool Success { get; set; }
        public string Content { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public int ContentLength { get; set; }
        public bool Truncated { get; set; }
        public string? Error { get; set; }
    }
}
