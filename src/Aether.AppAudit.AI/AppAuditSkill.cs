namespace Aether.AppAudit.AI;

/// <summary>
/// 「应用审查」的**判定标准**，以 skill（SKILL.md）形式存在，而不是硬编码在代码里。
/// <para>为什么：判定口径会变——哪些词算规避词、类型怎么分、同名站怎么处理，这些是**知识**不是逻辑。
/// 写成 <c>&lt;skillsDir&gt;/app-audit/SKILL.md</c> 后：① 改口径 = 改 Markdown，不用重新编译；
/// ② 同一份标准在聊天里也能被 agent 加载（skill 目录对两端同源）。</para>
/// <para>关键：正文里写的是**给 agent 的操作指引**（读任务 → 找证据 → 调 <c>SaveAppVerdict</c> 写回），
/// 不是一个交给代码解析的输出格式——判定的执行者是 agent，不是页面。</para>
/// <para>首次运行把内置正文写盘（<see cref="EnsureSeeded"/>），之后**以盘上文件为准**；
/// 文件被删/读不出来时回退内置正文。</para>
/// </summary>
public static class AppAuditSkill
{
    /// <summary>skill 名（= 目录名）。</summary>
    public const string Name = "app-audit";

    /// <summary>内置判定标准（首次运行的种子内容；也是文件缺失时的兜底）。</summary>
    public const string DefaultContent = """
        ---
        name: app-audit
        description: 应用审查任务的判定标准：给定一个应用名（多为 Chrome 网页应用的显示名），反查它对应的网站，判断站点类型与是否博彩，并把结论回写到「应用审查」页。
        ---

        # 应用审查：名字 → 站点 → 类型 → 是否博彩

        ## 分工（先看清自己的位置）
        「应用审查」页只做两件事：**发布任务**（一份应用名清单）和**显示结论**。它不判定。
        判定是你的活：读任务 → 自己找证据 → 把结论写回去。结论一写回，页面就显示出来了。

        ## 流程
        1. **取任务**：有 `ListAuditTasks` 工具就调它，拿到待处理的名字；用户在聊天里直接给了名字，就以那个名字为准。
        2. **找证据**：用 `WebSearch` 搜「名字 官网 / 网站 / app」，看候选站点；需要更硬的证据就用
           `FetchUrl` 打开候选页读正文，**不要只看标题猜**。
        3. **下结论**：站点类型 + 是否博彩 + 一句指出依据的理由（口径见下）。
        4. **写回**：有 `SaveAppVerdict` 工具就调它把结论落进库里；没有工具时把结论直接说给用户。

        用户手上这批名字多是手机上以 Chrome 网页应用/快捷方式装的，通常只知道显示名——名字只是线索，
        结论要看候选站点证据。

        ## 证据与同名问题
        - 网搜候选（标题 / URL / 摘要）是主要证据；清单里直接给的网址等同第一条候选，可信度高于网搜。
        - 一个名字可能同名不同义（公司名、商品名、博彩品牌都可能叫 Sunwin）。证据指向哪个就是哪个，
          指不明白就落 `inconclusive`，**不要**挑一个看起来像的当结论。

        ## 站点类型 siteType
        只填一个短标签，优先从这些里选：
        体育博彩 / 真人赌场 / 棋牌 / 视频 / 直播 / 社交 / 电商 / 新闻资讯 / 工具 / 软件下载 / 网盘 /
        游戏 / 色情 / 金融理财 / 诈骗站 / 未知。
        判不准就填「未知」——**不要**编一个看起来合理的类型。

        ## 博彩判定 verdict
        - `gambling`：站点提供真钱赌博（体育博彩、真人赌场、老虎机、彩票投注、棋牌兑现、信用盘等），
          或该名字是知名博彩品牌 / 其镜像站、跳转页。
        - `not_gambling`：正常业务站点（工具、购物、新闻、游戏但无真钱赌博），或证据明确指向非博彩站点。
        - `inconclusive`：证据不足——搜到的都是无关站点、同名不同义、只有论坛讨论没有站点本身。

        规避词（娱乐城、博彩、投注、体育、真人、棋牌、彩票、百家乐、真人视讯、信用盘、slot、casino、bet）
        一出现即高度可疑；但**不要**仅凭名字里有 bet/win/game 就下 gambling 结论，要看候选站点证据。

        ## 纪律
        - **不确定 ≠ 干净**：证据不足就落 `inconclusive`，并在理由里说清缺什么；不要为了给出结论而降级成非博彩。
        - 一个名字给一个结论；拿不准就写置信度低一些，不要写成 0.99。
        - 同一个名字可以判多次（用户在调口径、或换你复判）：再写一次即可，历史会保留，页面能看出结论怎么变的。
        - `reason` 必须指出依据（例如「候选站点含体育投注/真人视讯」），不要写「AI 判断」这种废话。
        """;

    /// <summary>
    /// 历史版本的种子正文（用来做**不覆盖用户改动**的迁移：盘上内容若还是某个旧版默认值，
    /// 说明用户没改过，可以安全升级成新版；改过的就一律不动）。
    /// </summary>
    internal static readonly string[] PreviousDefaults =
    {
        """
        ---
        name: app-audit
        description: 给定一个应用名（多为 Chrome 网页应用的显示名），反查它对应的网站，给出站点类型并判断是否为博彩站点。
        ---

        # 应用审查：名字 → 站点 → 类型 → 是否博彩

        用户手上有一批应用名（手机上以 Chrome 网页应用/快捷方式安装，只知道显示名）。
        你的任务是：从证据里认出这个名字**最可能对应的那个网站**，说清它是**什么类型的网站**，并判断它**是不是博彩站**。

        ## 证据从哪来
        - 网搜候选（标题 / URL / 摘要）：这是主要证据。名字可能同名不同义（公司名、商品名、博彩品牌都可能叫 Sunwin）。
        - 清单里直接给的网址：等同第一条候选，可信度高于网搜。
        - 需要更多依据时，抓候选页正文（fetch）再看，不要靠标题猜。

        ## 站点类型 site_type
        只填一个短标签，取值优先从这些里选：
        体育博彩 / 真人赌场 / 棋牌 / 视频 / 直播 / 社交 / 电商 / 新闻资讯 / 工具 / 软件下载 / 网盘 /
        游戏 / 色情 / 金融理财 / 诈骗站 / 未知。
        判不准就填「未知」——**不要**编一个看起来合理的类型。

        ## 博彩判定 verdict
        - `gambling`：站点提供真钱赌博（体育博彩、真人赌场、老虎机、彩票投注、棋牌兑现、信用盘等），
          或该名字是知名博彩品牌 / 其镜像站、跳转页。
        - `not_gambling`：正常业务站点（工具、购物、新闻、游戏但无真钱赌博），或证据明确指向非博彩站点。
        - `inconclusive`：证据不足——搜到的都是无关站点、同名不同义、只有论坛讨论没有站点本身。

        规避词（娱乐城、博彩、投注、体育、真人、棋牌、彩票、百家乐、真人视讯、信用盘、slot、casino、bet）
        一出现即高度可疑；但**不要**仅凭名字里有 bet/win/game 就下 gambling 结论，要看候选站点证据。

        ## 输出契约（**只输出一个 JSON 对象**，不要代码块、不要解释）
        ```json
        {"site":"候选列表里原样出现的 URL，或空串","site_type":"简短中文类型","verdict":"gambling|not_gambling|inconclusive","confidence":0.0-1.0,"reason":"一句话中文理由（≤60 字，指出依据）"}
        ```
        - `site` 只能填候选里**原样出现**的 URL；没有像样的对应站点就填空串（不要编域名）。
        - `confidence` 是 0 到 1 的小数；证据越弱越低。
        - `reason` 要指出依据（例如「候选站点含体育投注/真人视讯」），不要写「AI 判断」这种废话。
        """,
    };

    /// <summary>skill 正文落盘路径：<c>&lt;skillsDir&gt;/app-audit/SKILL.md</c>。</summary>
    public static string PathFor(string skillsDirectory)
        => Path.Combine(skillsDirectory, Name, "SKILL.md");

    /// <summary>
    /// 缺就写入内置正文；已存在时**只在内容仍是某个旧版默认值**的情况下升级成新版，
    /// 用户改过的正文一律不动。返回最终使用的正文。
    /// </summary>
    public static string EnsureSeeded(string skillsDirectory)
    {
        try
        {
            var path = PathFor(skillsDirectory);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultContent);
            }
            else if (PreviousDefaults.Contains(File.ReadAllText(path)))
            {
                File.WriteAllText(path, DefaultContent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去就只用内置正文，不影响判定。
        }
        return LoadOrDefault(skillsDirectory);
    }

    /// <summary>读盘上的 skill 正文（去掉 YAML frontmatter）；读不到就回退内置正文。</summary>
    public static string LoadOrDefault(string? skillsDirectory)
    {
        if (string.IsNullOrWhiteSpace(skillsDirectory)) return DefaultContent;
        try
        {
            var path = PathFor(skillsDirectory);
            if (!File.Exists(path)) return DefaultContent;
            var body = StripFrontMatter(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(body) ? DefaultContent : body;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultContent;
        }
    }

    /// <summary>去掉开头的 <c>--- … ---</c> frontmatter（那部分是给 skills 索引看的，不进提示词）。</summary>
    internal static string StripFrontMatter(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return text.Trim();
        var end = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return text.Trim();
        var after = trimmed[(end + 4)..];
        return after.Trim();
    }
}
