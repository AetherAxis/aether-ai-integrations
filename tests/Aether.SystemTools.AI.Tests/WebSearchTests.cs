using Aether.SystemTools.AI.Web;
using Xunit;

namespace Aether.SystemTools.AI.Tests;

/// <summary>
/// 网搜的解析层：搜索的**唯一实现**在这里（⑭），应用审查页与 agent 的 WebSearch 工具共用，
/// 所以这里的回归直接决定两边结果对不对。
/// </summary>
public class WebSearchTests
{
    /// <summary>
    /// 按 b_algo 块分段解析。真实 Bing 页面里第一个块常常只是样式/广告壳（没有 h2）——
    /// 老写法（`&lt;li …&gt;.*?&lt;h2&gt;` 跨块匹配 + 摘要按全页索引配对）会让标题/摘要错位。
    /// </summary>
    [Fact]
    public void 解析Bing结果_标题与摘要按块配对()
    {
        var html = """
            <ol id="b_results">
            <li class="b_algo" data-id iid=SERP.1><link rel="stylesheet" href="/rp/x.css"/></li>
            <li class="b_algo" data-id iid=SERP.2><div class="b_tpcn"><a class="tilk" href="https://baike.baidu.com/item/a">baidu</a></div><h2><a target="_blank" href="https://sunwin-sh.com/cn/">上海三问家居 &amp; sunwin-sh.com</a></h2><div class="b_caption"><p class="b_lineclamp2">公司主营家用纺织品出口。</p></div></li>
            <li class="b_algo" data-id iid=SERP.3><h2><a href="https://sun.win/">Sun Win</a></h2><div class="b_caption"><p class="b_lineclamp2">真人视讯 · 体育投注 · 百家乐</p></div></li>
            </ol>
            """;

        var list = WebSearchClient.ParseBing(html, 5);

        Assert.Equal(2, list.Count);                                  // 壳块不产出结果
        Assert.Equal("https://sunwin-sh.com/cn/", list[0].Url);
        Assert.Equal("上海三问家居 & sunwin-sh.com", list[0].Title);   // 实体已解码、标签已去
        Assert.Equal("公司主营家用纺织品出口。", list[0].Snippet);
        Assert.Equal("bing", list[0].Source);
        Assert.Equal("https://sun.win/", list[1].Url);                 // 没有与上一条串位
        Assert.Contains("真人视讯", list[1].Snippet);
    }

    /// <summary>条数上限生效；非 http(s) 的站内跳转被丢弃。</summary>
    [Fact]
    public void 解析Bing结果_遵守上限且丢弃站内跳转()
    {
        var html = """
            <li class="b_algo"><h2><a href="/ck/a?u=abc">站内跳转</a></h2><p class="b_lineclamp2">x</p></li>
            <li class="b_algo"><h2><a href="https://a.example/">A</a></h2></li>
            <li class="b_algo"><h2><a href="https://b.example/">B</a></h2></li>
            <li class="b_algo"><h2><a href="https://c.example/">C</a></h2></li>
            """;

        var list = WebSearchClient.ParseBing(html, 2);

        Assert.Equal(2, list.Count);
        Assert.Equal("https://a.example/", list[0].Url);
        Assert.Null(list[0].Snippet);                                  // 没有摘要就是 null，不是上一条的
        Assert.Equal("https://b.example/", list[1].Url);
    }

    /// <summary>DDG 的跳转链要解出真实地址（它的 href 全是 //duckduckgo.com/l/?uddg=…）。</summary>
    [Fact]
    public void 解析DuckDuckGo结果_解开跳转链()
    {
        var html = """
            <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fsun.win%2F&rut=x">Sun Win</a>
            <a class="result__snippet" href="//duckduckgo.com/l/?uddg=x">真人视讯 体育投注</a>
            """;

        var list = WebSearchClient.ParseDuckDuckGo(html, 5);

        var one = Assert.Single(list);
        Assert.Equal("https://sun.win/", one.Url);
        Assert.Contains("真人视讯", one.Snippet);
        Assert.Equal("duckduckgo", one.Source);
    }
}
