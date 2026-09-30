using System.Diagnostics;
using Prototype.Core;
using Prototype.Devices.Device.Abstractions;

namespace Prototype.Devices.DataSources;
public class MockSource : IDataSource<float>
{
    private const int BlockMilliseconds = 5;
    private readonly DataChannel<float> _dataChannel;
    private readonly int _sampleRate;
    private readonly double _amplitude;
    private readonly double _periodSeconds;
    private readonly double _phaseRadians;
    private readonly long? _baseTimestampNs;
    public MockSource(DataChannel<float> dataChannel, int sampleRate = 25_600, double amplitude = 10.0,
        double periodSeconds = 3.0,double phaseDegrees = 0.0,long? baseTimestampNs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodSeconds);
        _dataChannel = dataChannel;
        _sampleRate = sampleRate;
        _amplitude = amplitude;
        _periodSeconds = periodSeconds;
        _phaseRadians = phaseDegrees * Math.PI / 180.0;
        _baseTimestampNs = baseTimestampNs;
    }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        long baseTimestamp = _baseTimestampNs
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

        long index = 0;
        var clock = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            long target = (long)(clock.Elapsed.TotalSeconds * _sampleRate);

            while (index < target)
            {
                double t = index / (double)_sampleRate;

                float value = (float)(_amplitude * Math.Sin(2 * Math.PI * t / _periodSeconds + _phaseRadians));

                long timestamp = baseTimestamp + (long)(t * 1_000_000_000.0);

                _dataChannel.Push(new DataPoint<float>(timestamp, value));

                index++;
            }

            try
            {
                await Task.Delay(BlockMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
