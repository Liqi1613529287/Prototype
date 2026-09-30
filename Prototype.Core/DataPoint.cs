namespace Prototype.Core;

public class DataPoint<T>
{
    public long Timestamp { get; }
    public T Value { get; }
    public DataPoint(long  timestamp, T value)
    {
        Timestamp = timestamp;
        Value = value;
    }
}