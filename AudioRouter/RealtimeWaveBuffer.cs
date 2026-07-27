using NAudio.Wave;

namespace AudioRouter;

internal readonly record struct RealtimeWaveBufferSnapshot(
    TimeSpan BufferedDuration,
    TimeSpan PeakBufferedDuration,
    TimeSpan DroppedDuration,
    long RecoveryCount);

internal sealed class RealtimeWaveBuffer : IWaveProvider
{
    private readonly object _syncRoot = new();
    private readonly byte[] _buffer;
    private readonly int _blockAlign;
    private int _hardLimitBytes;
    private readonly int _recoveryBytes;
    private int _readPosition;
    private int _writePosition;
    private int _bufferedBytes;
    private int _peakBufferedBytes;
    private long _droppedBytes;
    private long _recoveryCount;

    public RealtimeWaveBuffer(
        WaveFormat waveFormat,
        AudioBufferSettings settings)
    {
        WaveFormat = waveFormat ??
                     throw new ArgumentNullException(nameof(waveFormat));
        _blockAlign = Math.Max(1, waveFormat.BlockAlign);
        _recoveryBytes = DurationToBytes(
            settings.RecoveryWatermark);
        _hardLimitBytes = DurationToBytes(
            settings.HardLimit);
        var capacityBytes = DurationToBytes(settings.Capacity);

        if (!(_recoveryBytes < _hardLimitBytes &&
              _hardLimitBytes < capacityBytes))
        {
            throw new ArgumentException(
                "实时缓冲水位配置无效。",
                nameof(settings));
        }

        _buffer = new byte[capacityBytes];
    }

    public WaveFormat WaveFormat { get; }

    public int BufferedBytes
    {
        get
        {
            lock (_syncRoot)
            {
                return _bufferedBytes;
            }
        }
    }

    public TimeSpan BufferedDuration =>
        BytesToDuration(BufferedBytes);

    public void AddSamples(
        byte[] buffer,
        int offset,
        int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        count = AlignDown(count);
        if (count == 0)
        {
            return;
        }

        lock (_syncRoot)
        {
            var projectedBytes = checked(
                _bufferedBytes + count);

            if (count > _hardLimitBytes)
            {
                var bytesToKeep = Math.Min(
                    _recoveryBytes,
                    _buffer.Length);
                var bytesSkipped = count - bytesToKeep;
                offset += bytesSkipped;
                count = bytesToKeep;
                RegisterDrop(_bufferedBytes + bytesSkipped);
                ResetBuffer();
            }

            projectedBytes = _bufferedBytes + count;
            if (projectedBytes > _hardLimitBytes)
            {
                var desiredExistingBytes = Math.Max(
                    0,
                    _recoveryBytes - count);
                var bytesToDrop = Math.Max(
                    0,
                    _bufferedBytes - desiredExistingBytes);
                RegisterDrop(DropOldest(bytesToDrop));
            }

            var remainingCapacity = _buffer.Length - _bufferedBytes;
            if (count > remainingCapacity)
            {
                var bytesToDrop = AlignUp(
                    count - remainingCapacity);
                RegisterDrop(DropOldest(bytesToDrop));
            }

            WriteCore(buffer, offset, count);
            _peakBufferedBytes = Math.Max(
                _peakBufferedBytes,
                _bufferedBytes);
        }
    }

    public int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        count = AlignDown(count);
        if (count == 0)
        {
            return 0;
        }

        lock (_syncRoot)
        {
            var bytesToRead = Math.Min(count, _bufferedBytes);
            var firstPart = Math.Min(
                bytesToRead,
                _buffer.Length - _readPosition);
            Buffer.BlockCopy(
                _buffer,
                _readPosition,
                buffer,
                offset,
                firstPart);

            var secondPart = bytesToRead - firstPart;
            if (secondPart > 0)
            {
                Buffer.BlockCopy(
                    _buffer,
                    0,
                    buffer,
                    offset + firstPart,
                    secondPart);
            }

            _readPosition =
                (_readPosition + bytesToRead) % _buffer.Length;
            _bufferedBytes -= bytesToRead;
            return bytesToRead;
        }
    }

    public void ClearBuffer()
    {
        lock (_syncRoot)
        {
            ResetBuffer();
        }
    }

    public void RaiseHardLimit(TimeSpan hardLimit)
    {
        var hardLimitBytes = Math.Min(
            DurationToBytes(hardLimit),
            _buffer.Length - _blockAlign);
        lock (_syncRoot)
        {
            _hardLimitBytes = Math.Max(
                _hardLimitBytes,
                hardLimitBytes);
        }
    }

    public RealtimeWaveBufferSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return new RealtimeWaveBufferSnapshot(
                BytesToDuration(_bufferedBytes),
                BytesToDuration(_peakBufferedBytes),
                BytesToDuration(_droppedBytes),
                _recoveryCount);
        }
    }

    private void WriteCore(
        byte[] source,
        int offset,
        int count)
    {
        var firstPart = Math.Min(
            count,
            _buffer.Length - _writePosition);
        Buffer.BlockCopy(
            source,
            offset,
            _buffer,
            _writePosition,
            firstPart);

        var secondPart = count - firstPart;
        if (secondPart > 0)
        {
            Buffer.BlockCopy(
                source,
                offset + firstPart,
                _buffer,
                0,
                secondPart);
        }

        _writePosition =
            (_writePosition + count) % _buffer.Length;
        _bufferedBytes += count;
    }

    private int DropOldest(int count)
    {
        var bytesToDrop = Math.Min(
            AlignDown(count),
            _bufferedBytes);
        if (bytesToDrop == 0)
        {
            return 0;
        }

        _readPosition =
            (_readPosition + bytesToDrop) % _buffer.Length;
        _bufferedBytes -= bytesToDrop;
        return bytesToDrop;
    }

    private void RegisterDrop(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _droppedBytes += count;
        _recoveryCount++;
    }

    private void ResetBuffer()
    {
        _readPosition = 0;
        _writePosition = 0;
        _bufferedBytes = 0;
    }

    private int DurationToBytes(TimeSpan duration)
    {
        var bytes = checked((long)Math.Ceiling(
            WaveFormat.AverageBytesPerSecond *
            duration.TotalSeconds));
        var alignedBytes = checked(
            (bytes + _blockAlign - 1) /
            _blockAlign *
            _blockAlign);
        return checked((int)alignedBytes);
    }

    private TimeSpan BytesToDuration(long bytes)
    {
        return TimeSpan.FromSeconds(
            bytes /
            (double)WaveFormat.AverageBytesPerSecond);
    }

    private int AlignDown(int count)
    {
        return count / _blockAlign * _blockAlign;
    }

    private int AlignUp(int count)
    {
        return checked(
            (count + _blockAlign - 1) /
            _blockAlign *
            _blockAlign);
    }

    private static void ValidateBufferArguments(
        byte[] buffer,
        int offset,
        int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException(
                "偏移量和长度超出缓冲区范围。");
        }
    }
}
