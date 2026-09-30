using Prototype.Core;

namespace Prototype.Storage;

//从私有队列取点写库。单点失败只记账不掀链
public class StorageConsumer : IDataConsumer<float>
{
    private readonly DataChannel<float> _channel;

    private readonly IDataStorage<float> _storage;

    private long _written;

    public StorageConsumer(DataChannel<float> channel, IDataStorage<float> storage)
    {
        _channel = channel;
        _storage = storage;
    }

    //成功落盘的点数
    public long Written => Interlocked.Read(ref _written);

    public async Task Start(CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (DataPoint<float> point in _channel.ReadAllAsync().WithCancellation(cancellationToken))
            {
                try
                {
                    _storage.Write(point);

                    Interlocked.Increment(ref _written);
                }
                catch (Exception ex)
                {
                    // 单个点写失败不能掀掉整条消费链——原来这里一抛，后面所有数据全没了
                    Console.WriteLine($"[storage] 写入失败: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }
}
