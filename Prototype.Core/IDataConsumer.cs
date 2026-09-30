namespace Prototype.Core;

public interface IDataConsumer<T>
{
    Task Start(CancellationToken cancellationToken = default);
}
