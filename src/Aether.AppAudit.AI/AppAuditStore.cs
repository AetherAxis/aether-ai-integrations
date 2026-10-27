using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aether.AppAudit.AI;

/// <summary>
/// 应用审查结果的落盘（`&lt;dataDir&gt;/app-audit.json`）——手机与桌面**同一份路径**，换端搬数据目录即接着用。
/// <para>写盘是原子替换（临时文件 + Move），丑重启不会留下半个 JSON；读不出来只记一条警告并当空表，
/// 不让审查页把整个 App 拖垮（这页是工具，不是启动前提）。</para>
/// </summary>
public sealed class AppAuditStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private AppAuditDocument _doc;

    public AppAuditStore(string filePath)
    {
        FilePath = filePath;
        _doc = Load(filePath);
    }

    public string FilePath { get; }

    /// <summary>快照（只读——外部改请走 <see cref="Mutate"/> 或用 store 上的方法）。</summary>
    public AppAuditDocument Document
    {
        get { lock (_gate) return _doc; }
    }

    public IReadOnlyList<AppAuditEntry> Entries
    {
        get { lock (_gate) return _doc.Entries.ToList(); }
    }

    /// <summary>内容变化（UI 据此刷新；在调用线程上触发，调用方负责切 UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 在锁内改文档并落盘。`mutate` 抛异常时不写盘、内存也不留半改状态（先改副本再替换）。
    /// </summary>
    public void Mutate(Action<AppAuditDocument> mutate)
    {
        lock (_gate)
        {
            mutate(_doc);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// 合并导入：按 <see cref="AppAuditEntry.IdFor"/> 去重，**已有记录保留全部调查结果**，
    /// 只用新导入的名字/网址补齐（再导入一次不能把已判过的结论洗掉）。
    /// </summary>
    public int MergeNames(IEnumerable<(string Name, string? ShortcutUrl)> names, DateTimeOffset importedAt)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var (rawName, url) in names)
            {
                var name = rawName.Trim();
                if (name.Length == 0) continue;
                var id = AppAuditEntry.IdFor(name);
                var existing = _doc.Entries.FirstOrDefault(e => e.Id == id);
                if (existing is null)
                {
                    _doc.Entries.Add(new AppAuditEntry
                    {
                        Id = id,
                        AppName = name,
                        ShortcutUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
                    });
                    added++;
                    continue;
                }
                // 已有记录：只补空缺，不覆盖结论。
                if (string.IsNullOrWhiteSpace(existing.ShortcutUrl) && !string.IsNullOrWhiteSpace(url))
                    existing.ShortcutUrl = url.Trim();
            }
            _doc.LastImportAt = importedAt;
        }
        Save();
        Changed?.Invoke();
        return added;
    }

    /// <summary>清空（保留文件本身——下次导入直接重来）。</summary>
    public void Clear()
    {
        lock (_gate) _doc.Entries.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        AppAuditDocument snapshot;
        lock (_gate) snapshot = _doc;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不阻断内存态（用户还能看到本轮结果）；读取方按 LastError 提示。
        }
    }

    private static AppAuditDocument Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new AppAuditDocument();
            return JsonSerializer.Deserialize<AppAuditDocument>(File.ReadAllText(filePath), Json)
                   ?? new AppAuditDocument();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppAuditDocument();
        }
    }
}
