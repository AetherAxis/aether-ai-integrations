using System;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aether.SystemTools.AI.Web;

namespace Aether.SystemTools.AI.Tools;

/// <summary>
/// 网页搜索工具 —— 给一个搜索词，返回候选站点（标题 / URL / 摘要）。
/// <para>这是 agent 的「查资料」入口：先搜再 <c>FetchUrl</c> 抓正文，比只凭记忆回答可靠得多。
/// 底层是 <see cref="WebSearchClient"/>（HTML 结果页解析，无需 API key），与应用审查页共用同一份实现。</para>
/// <para>失败以 <c>Success=false</c> 返回而不是抛异常——工具调用失败应该让模型看到并换策略，
/// 不是把整轮对话打断。</para>
/// </summary>
public class WebSearchTool
{
    private readonly WebSearchClient _client;

    public WebSearchTool(HttpClient httpClient)
    {
        _client = new WebSearchClient(httpClient);
    }

    private const int MaxContentLength = 4000;

    [Description("搜索网页，返回候选站点（标题/URL/摘要）。用于查证事实、找官网、确认某个名字对应什么站点")]
    public async Task<Output> WebSearch(
        [Description("搜索词，尽量具体（可带官网/官网地址等限定词）")] string query,
        [Description("返回条数，默认 5，最多 20")] int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new Output { Success = false, Error = "搜索词不能为空" };

        var wanted = maxResults ?? WebSearchClient.DefaultMaxResults;
        try
        {
            var results = await _client.SearchAsync(query, wanted, cancellationToken);
            if (results.Count == 0)
                return new Output { Success = false, Query = query, Error = "没有搜到结果" };

            var sb = new StringBuilder();
            sb.AppendLine($"共 {results.Count} 条（query: {query}）：");
            var i = 1;
            foreach (var r in results)
            {
                sb.AppendLine($"{i++}. {r.Title}");
                sb.AppendLine($"   URL: {r.Url}");
                if (!string.IsNullOrWhiteSpace(r.Snippet)) sb.AppendLine($"   摘要: {r.Snippet}");
            }

            var content = sb.ToString();
            var truncated = content.Length > MaxContentLength;
            if (truncated) content = content[..MaxContentLength] + "\n…（已截断）";

            return new Output
            {
                Success = true,
                Query = query,
                Content = content,
                Count = results.Count,
                Truncated = truncated,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Output { Success = false, Query = query, Error = $"搜索失败：{ex.Message}" };
        }
    }

    public class Output
    {
        public bool Success { get; set; }
        public string Query { get; set; } = string.Empty;
        /// <summary>格式化后的结果（标题 / URL / 摘要，一行条）。</summary>
        public string Content { get; set; } = string.Empty;
        public int Count { get; set; }
        public bool Truncated { get; set; }
        public string? Error { get; set; }
    }
}
