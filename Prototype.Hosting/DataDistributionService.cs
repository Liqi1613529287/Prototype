using Prototype.Core;

namespace Prototype.Hosting;

//广播分发：源队列 → 每个 target 一条私有队列
//AddTarget 是广播不是抢占，每个 target 都拿到完整的一份数据
//所以某一个消费者慢只会填满它自己的队列，不拖累其他路
public class DataDistributionService<T>
{
    private readonly DataChannel<T> _source;

    private readonly List<DataChannel<T>> _targets = new();

    //因目标队列满而没能投递出去的点数
    private long _dropped;

    public DataDistributionService(DataChannel<T> source)
    {
        _source = source;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void AddTarget(DataChannel<T> channel)
    {
        _targets.Add(channel);
    }

    public async Task Start(CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var point in _source.ReadAllAsync().WithCancellation(cancellationToken))
            {
                foreach (var target in _targets)
                {
                    if (!target.Push(point))
                    {
                        Interlocked.Increment(ref _dropped);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }
}
