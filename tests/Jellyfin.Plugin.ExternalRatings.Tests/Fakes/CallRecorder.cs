namespace Jellyfin.Plugin.ExternalRatings.Tests.Fakes;

/// <summary>
/// A shared, ordered log of named calls used to assert cross-component ordering invariants
/// (e.g. write-ahead: backup must be recorded before the item write).
/// </summary>
internal sealed class CallRecorder
{
    private readonly List<string> _calls = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    public int IndexOf(string call) => Calls.ToList().FindIndex(c => c == call);
}
