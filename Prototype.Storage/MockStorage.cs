using Prototype.Core;

namespace Prototype.Storage;

public class MockStorage<T> : IDataStorage<T>
{
    private const int Capacity = 1_000_000;

    private readonly List<DataPoint<T>> _points = new();

    public void Write(DataPoint<T> point)
    {
        if (_points.Count >= Capacity)
        {
            _points.RemoveRange(0, Capacity / 10);
        }

        _points.Add(point);
    }
}
