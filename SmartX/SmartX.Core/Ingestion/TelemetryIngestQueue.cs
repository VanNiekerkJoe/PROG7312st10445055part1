using System.Diagnostics.CodeAnalysis;

namespace SmartX.Core.Ingestion;

/// <summary>
/// The two-lane ingestion buffer that sits between the HTTP endpoint and the
/// processor:
///   * Queue&lt;T&gt; (FIFO) holds ordinary telemetry in arrival order.
///   * PriorityQueue&lt;TElement, TPriority&gt; holds critical packets (severe
///     spikes, out-of-range values, disconnects) and is always drained first,
///     so a critical alert bypasses any backlog of standard packets.
/// Within the priority lane, higher urgency wins and equal urgency stays
/// first-come-first-served (the sequence number breaks ties, because
/// PriorityQueue on its own is not stable).
/// </summary>
public sealed class TelemetryIngestQueue
{
    public const int MaxStandardDepth = 5000;

    private readonly object _lock = new();
    private readonly Queue<IngestItem> _standard = new();
    private readonly PriorityQueue<IngestItem, (int NegativeUrgency, long Sequence)> _critical = new();
    private long _sequence;

    public int StandardDepth
    {
        get { lock (_lock) { return _standard.Count; } }
    }

    public int PriorityDepth
    {
        get { lock (_lock) { return _critical.Count; } }
    }

    /// <summary>Returns false when the standard lane is full (back-pressure).</summary>
    public bool Enqueue(IngestItem item)
    {
        lock (_lock)
        {
            if (item.IsCritical)
            {
                _critical.Enqueue(item, (-item.Urgency, ++_sequence));
                return true;
            }

            if (_standard.Count >= MaxStandardDepth)
            {
                return false;
            }

            _standard.Enqueue(item);
            return true;
        }
    }

    /// <summary>Critical lane first, then the FIFO lane.</summary>
    public bool TryDequeue([NotNullWhen(true)] out IngestItem? item)
    {
        lock (_lock)
        {
            if (_critical.TryDequeue(out item, out _))
            {
                return true;
            }

            return _standard.TryDequeue(out item);
        }
    }
}
