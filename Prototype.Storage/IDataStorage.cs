using Prototype.Core;

namespace Prototype.Storage;

public interface IDataStorage<T>
{
    void Write(DataPoint<T> point);
}