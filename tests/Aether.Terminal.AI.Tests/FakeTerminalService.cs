using Aether.Terminal.AI;

namespace Aether.Terminal.AI.Tests;

/// <summary>ITerminalService 测试替身：记录调用、可配置 RunAsync 应答——不起 PTY/RoyalTerminal。</summary>
internal sealed class FakeTerminalService : ITerminalService
{
    public sealed class FakeSession : ITerminalSession
    {
        public Guid Id { get; init; }
        public string? Cwd { get; set; }
        public string Title { get; set; } = "fake";
        public bool IsRunning { get; set; } = true;
    }

    public List<FakeSession> Sessions { get; } = new();
    public FakeSession? Active;
    /// <summary>CreateSession 的 (cwd, title) 记录。</summary>
    public List<(string? Cwd, string? Title)> Created { get; } = new();
    /// <summary>SendInput 的 (sessionId, text) 记录。</summary>
    public List<(Guid SessionId, string Text)> Inputs { get; } = new();
    /// <summary>RunAsync 的 (sessionId, command, timeoutMs) 记录。</summary>
    public List<(Guid SessionId, string Command, int TimeoutMs)> Runs { get; } = new();
    /// <summary>RunAsync 的应答（null = 默认成功）。</summary>
    public TerminalRunResult? NextRunResult { get; set; }

    public Guid? ActiveSessionId => Active?.Id;

    public event Action? SessionsChanged;
    public event Action<Guid>? ActiveSessionChanged;
    public event Action<Guid>? SessionExited;

    public ITerminalSession CreateSession(string? cwd, string? title)
    {
        var s = new FakeSession { Id = Guid.NewGuid(), Cwd = cwd, Title = title ?? "shell" };
        Sessions.Add(s);
        Active = s;
        Created.Add((cwd, title));
        SessionsChanged?.Invoke();
        ActiveSessionChanged?.Invoke(s.Id);
        return s;
    }

    public void CloseSession(Guid sessionId)
    {
        var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s is null) return;
        Sessions.Remove(s);
        if (Active?.Id == sessionId) Active = Sessions.LastOrDefault();
        SessionExited?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public void SetActive(Guid sessionId)
    {
        Active = Sessions.FirstOrDefault(x => x.Id == sessionId);
        ActiveSessionChanged?.Invoke(sessionId);
        SessionsChanged?.Invoke();
    }

    public IReadOnlyList<ITerminalSession> GetSessions() => Sessions.ToList();

    public void SendInput(Guid sessionId, string text) => Inputs.Add((sessionId, text));

    public Task<TerminalRunResult> RunAsync(Guid sessionId, string command, int timeoutMs, CancellationToken ct)
    {
        Runs.Add((sessionId, command, timeoutMs));
        return Task.FromResult(NextRunResult ?? new TerminalRunResult { ExitCode = 0, Completed = true, Output = "ok" });
    }
}
