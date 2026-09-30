namespace Prototype.Devices.Device.Abstractions;

public interface IDataSource<T>
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
