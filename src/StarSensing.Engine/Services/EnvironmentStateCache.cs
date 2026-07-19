using StarSensing.Core.Models;

namespace StarSensing.Engine.Services;

/// <summary>Thread-safe cache of the latest processed environment states keyed by batch id.</summary>
public sealed class EnvironmentStateCache
{
    private readonly object _lock = new();
    private EnvironmentState _latest = new();
    private readonly Dictionary<Guid, EnvironmentState> _byBatch = new();

    public void Put(Guid batchId, EnvironmentState state)
    {
        lock (_lock)
        {
            _latest = state;
            _byBatch[batchId] = state;
            if (_byBatch.Count > 200)
            {
                var oldest = _byBatch.Keys.First();
                _byBatch.Remove(oldest);
            }
        }
    }

    public EnvironmentState? Get(Guid batchId)
    {
        lock (_lock)
        {
            return _byBatch.TryGetValue(batchId, out var s) ? s : null;
        }
    }

    public EnvironmentState Latest
    {
        get { lock (_lock) return _latest; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _byBatch.Clear();
            _latest = new EnvironmentState();
        }
    }
}
