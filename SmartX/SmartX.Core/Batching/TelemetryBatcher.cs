using SmartX.Core.Telemetry;

namespace SmartX.Core.Batching;

/// <summary>
/// Buffers raw readings for a single device into fixed-size "windows", stores
/// each completed window as one row of a jagged array (T[][]) so historical
/// batches accumulate sequentially without the wasted space a rectangular
/// T[,] would carry once devices report at different rates, then flushes the
/// whole jagged buffer into a flat, optimised List&lt;TelemetryPacket&lt;T&gt;&gt;
/// for downstream consumption (SignalR broadcast, storage, the constellation
/// view).
/// </summary>
/// <typeparam name="T">The raw reading type for this device (float, int, bool, ...).</typeparam>
public sealed class TelemetryBatcher<T> where T : struct
{
    private readonly string _deviceId;
    private readonly SensorCategory _category;
    private readonly int _windowSize;

    /// <summary>
    /// Jagged array of completed windows. Each row is one window's raw
    /// values; rows are appended sequentially as windows complete, so this
    /// is genuinely jagged (row lengths are all _windowSize here, but the
    /// structure itself grows one ragged row at a time rather than being
    /// pre-sized as a rectangular array).
    /// </summary>
    private T[][] _historicalBatches = [];

    private readonly List<T> _currentWindow = [];

    public TelemetryBatcher(string deviceId, SensorCategory category, int windowSize = 10)
    {
        if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
        _deviceId = deviceId;
        _category = category;
        _windowSize = windowSize;
    }

    public int CompletedWindowCount => _historicalBatches.Length;

    /// <summary>Adds one raw reading to the current window, closing the window if it's now full.</summary>
    public void Add(T value)
    {
        _currentWindow.Add(value);
        if (_currentWindow.Count >= _windowSize)
        {
            CompleteWindow();
        }
    }

    private void CompleteWindow()
    {
        var grown = new T[_historicalBatches.Length + 1][];
        Array.Copy(_historicalBatches, grown, _historicalBatches.Length);
        grown[^1] = _currentWindow.ToArray();
        _historicalBatches = grown;
        _currentWindow.Clear();
    }

    /// <summary>
    /// Transfers every completed window out of the jagged raw-array buffer
    /// into a flat List&lt;TelemetryPacket&lt;T&gt;&gt;, then clears the buffer.
    /// This is the "raw arrays -> optimised Collections" hand-off point.
    /// </summary>
    public List<TelemetryPacket<T>> FlushToPacketList()
    {
        var result = new List<TelemetryPacket<T>>();

        foreach (var window in _historicalBatches)
        {
            foreach (var value in window)
            {
                result.Add(TelemetryPacket<T>.Create(_deviceId, value, _category));
            }
        }

        _historicalBatches = [];
        return result;
    }
}
