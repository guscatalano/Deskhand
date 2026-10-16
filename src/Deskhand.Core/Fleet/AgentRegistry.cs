using System.Collections.Concurrent;

namespace Deskhand.Core.Fleet;

/// <summary>The fleet server's live directory of connected agents.</summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, ServerAgentLink> _agents = new();

    public void Add(ServerAgentLink link) => _agents[link.AgentId] = link;
    public void Remove(string id) => _agents.TryRemove(id, out _);
    public ServerAgentLink? Get(string id) => _agents.TryGetValue(id, out var l) ? l : null;
    public IReadOnlyList<ServerAgentLink> All => _agents.Values.ToList();

    /// <summary>The agent running in a given Windows TS session, if one is connected (newest wins).</summary>
    public ServerAgentLink? GetBySession(int session) =>
        _agents.Values.Where(l => l.SessionId == session).OrderByDescending(l => l.AgentId).FirstOrDefault();
}
