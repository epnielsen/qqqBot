namespace MarketBlocks.Core.Math;

/// <summary>
/// O(1) complexity Simple Moving Average calculator using a circular buffer.
/// Thread-safe for single-writer scenarios (typical trading use case).
/// No external dependencies - uses only decimal arithmetic.
/// </summary>
public sealed class IncrementalSma
{
    private readonly decimal[] _buffer;
    private readonly int _capacity;
    private int _count;
    private int _head;
    private decimal _runningSum;
    
    /// <summary>
    /// Creates a new incremental SMA calculator.
    /// </summary>
    /// <param name="capacity">Number of samples in the moving average window.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if capacity is less than 1.</exception>
    public IncrementalSma(int capacity)
    {
        if (capacity <= 0) 
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        
        _capacity = capacity;
        _buffer = new decimal[capacity];
        _count = 0;
        _head = 0;
        _runningSum = 0m;
    }
    
    /// <summary>
    /// Adds a new value and returns the updated SMA.
    /// O(1) time complexity - no iteration over buffer.
    /// </summary>
    /// <param name="value">The new value to add (typically a price).</param>
    /// <returns>The updated simple moving average.</returns>
    public decimal Add(decimal value)
    {
        if (_count < _capacity)
        {
            // Buffer not full yet - just add
            _buffer[_count] = value;
            _runningSum += value;
            _count++;
        }
        else
        {
            // Buffer full - subtract oldest, add newest (circular)
            decimal oldest = _buffer[_head];
            _runningSum = _runningSum - oldest + value;
            _buffer[_head] = value;
            _head = (_head + 1) % _capacity;
        }
        
        return _runningSum / _count;
    }
    
    /// <summary>
    /// Current SMA value (or 0 if empty).
    /// </summary>
    public decimal CurrentAverage => _count > 0 ? _runningSum / _count : 0m;
    
    /// <summary>
    /// Number of samples currently in buffer.
    /// </summary>
    public int Count => _count;
    
    /// <summary>
    /// Maximum capacity of the buffer.
    /// </summary>
    public int Capacity => _capacity;
    
    /// <summary>
    /// Whether buffer has reached full capacity (SMA is "warmed up").
    /// </summary>
    public bool IsFull => _count >= _capacity;
    
    /// <summary>
    /// Percentage of buffer filled (0.0 to 1.0).
    /// </summary>
    public decimal FillRatio => (decimal)_count / _capacity;
    
    /// <summary>
    /// Reset to empty state.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
        _runningSum = 0m;
        Array.Clear(_buffer, 0, _buffer.Length);
    }
    
    /// <summary>
    /// Seed with initial values (for warm-up from historical data).
    /// </summary>
    /// <param name="values">Initial values to seed (will take last N if more than capacity).</param>
    public void Seed(IEnumerable<decimal> values)
    {
        Clear();
        foreach (var value in values)
        {
            Add(value);
        }
    }
    
    /// <summary>
    /// Seed from an array (optimized path for bulk loading).
    /// </summary>
    /// <param name="values">Array of values.</param>
    /// <param name="startIndex">Starting index in array.</param>
    /// <param name="count">Number of values to load.</param>
    public void Seed(decimal[] values, int startIndex, int count)
    {
        Clear();
        int endIndex = System.Math.Min(startIndex + count, values.Length);
        for (int i = startIndex; i < endIndex; i++)
        {
            Add(values[i]);
        }
    }
    
    /// <summary>
    /// Gets all values currently in the buffer (for debugging/inspection).
    /// Returns values in chronological order (oldest first).
    /// </summary>
    public IReadOnlyList<decimal> GetValues()
    {
        var result = new decimal[_count];
        
        if (_count < _capacity)
        {
            // Buffer not full - values are in order starting at index 0
            Array.Copy(_buffer, result, _count);
        }
        else
        {
            // Buffer full - need to unwind circular buffer
            int firstPartLength = _capacity - _head;
            Array.Copy(_buffer, _head, result, 0, firstPartLength);
            Array.Copy(_buffer, 0, result, firstPartLength, _head);
        }
        
        return result;
    }
}
