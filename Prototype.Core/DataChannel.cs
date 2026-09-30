using System.Threading.Channels;

namespace Prototype.Core;

public class DataChannel<T>
{
    public string DeviceId { get; }
    public string ChannelId { get; }
    public string Name { get; }
    public string Unit { get; }
    //通道采样率
    public int SampleRateHertz { get; }
    
    //这条通道的逻辑身份键（设备号 + 通道号）
    public ChannelKey Key => new(DeviceId, ChannelId);
    private readonly Channel<DataPoint<T>> _channel;
    
    //队列满导致写不进去的点数。这是"数据有洞"的唯一证据，不能默默丢掉。
    private long _dropped;
    public DataChannel(string deviceId, string channelId, string name, string unit, int sampleRateHertz = 25_600)
    {
        DeviceId = deviceId;
        ChannelId = channelId;
        Name = name;
        Unit = unit;
        SampleRateHertz = sampleRateHertz;
        _channel = Channel.CreateBounded<DataPoint<T>>(100000);
    }
    //队列里还没被消费掉的点数。乘以采样周期就是"消费端落后生产者多久"。
    public int Pending => _channel.Reader.CanCount ? _channel.Reader.Count : -1;
    //累计被丢弃的点数
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool Push(DataPoint<T> point)
    {
        if (_channel.Writer.TryWrite(point))
        {
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public IAsyncEnumerable<DataPoint<T>> ReadAllAsync()
    {
        return _channel.Reader.ReadAllAsync();
    }
}