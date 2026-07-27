using System.Diagnostics;
using NAudio.Dsp;
using NAudio.Wave;

namespace AudioRouter;

public sealed class AdaptiveResamplingSampleProvider : ISampleProvider
{
    private const double MaximumNormalCorrection = 0.001;
    private const double MaximumRecoveryCorrection = 0.01;
    private const double NormalCorrectionStrength = 0.001;
    private const double RecoveryCorrectionStrength = 0.005;
    private const double UpdateIntervalSeconds = 0.1;
    private const double BufferSmoothingTimeConstantSeconds = 0.4;
    private const double RateSmoothingTimeConstantSeconds = 0.3;
    private const double MinimumTargetBufferSeconds = 0.005;

    private readonly ISampleProvider _source;
    private readonly WdlResampler _resampler;
    private readonly Func<TimeSpan> _getBufferedDuration;
    private double _targetBufferSeconds;
    private double _softLimitSeconds;
    private double _deadBandSeconds;
    private readonly int _channels;
    private readonly int _inputSampleRate;
    private readonly int _outputSampleRate;
    private double _currentRateRatio = 1.0;
    private double _smoothedBufferSeconds;
    private long _lastRateUpdateTimestamp;
    private int _isRecoveryMode;

    public AdaptiveResamplingSampleProvider(
        ISampleProvider source,
        int outputSampleRate,
        Func<TimeSpan> getBufferedDuration,
        TimeSpan targetBufferDuration,
        TimeSpan softLimit)
    {
        _source = source ??
                  throw new ArgumentNullException(nameof(source));
        _getBufferedDuration = getBufferedDuration ??
                               throw new ArgumentNullException(
                                   nameof(getBufferedDuration));
        if (outputSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputSampleRate));
        }

        _targetBufferSeconds = Math.Max(
            targetBufferDuration.TotalSeconds,
            MinimumTargetBufferSeconds);
        _softLimitSeconds = Math.Max(
            softLimit.TotalSeconds,
            _targetBufferSeconds * 2);
        _deadBandSeconds = Math.Min(
            0.01,
            _targetBufferSeconds * 0.2);
        _smoothedBufferSeconds = _targetBufferSeconds;
        _lastRateUpdateTimestamp = Stopwatch.GetTimestamp();
        _channels = source.WaveFormat.Channels;
        _inputSampleRate = source.WaveFormat.SampleRate;
        _outputSampleRate = outputSampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            _outputSampleRate,
            _channels);

        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        _resampler.SetRates(
            _inputSampleRate,
            _outputSampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public double CurrentRateCorrectionPpm =>
        (Volatile.Read(ref _currentRateRatio) - 1.0) *
        1_000_000.0;

    public bool IsRecoveryMode =>
        Volatile.Read(ref _isRecoveryMode) != 0;

    public void RaiseBufferTarget(
        TimeSpan targetBufferDuration,
        TimeSpan softLimit)
    {
        var targetSeconds = Math.Max(
            targetBufferDuration.TotalSeconds,
            MinimumTargetBufferSeconds);
        var softLimitSeconds = Math.Max(
            softLimit.TotalSeconds,
            targetSeconds * 2);
        var deadBandSeconds = Math.Min(
            0.01,
            targetSeconds * 0.2);

        SetMaximum(ref _targetBufferSeconds, targetSeconds);
        SetMaximum(ref _softLimitSeconds, softLimitSeconds);
        SetMaximum(ref _deadBandSeconds, deadBandSeconds);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        AudioThreadPriority.PromoteCurrentThread();
        UpdateResamplingRate();

        var framesRequested = count / _channels;
        var inputNeeded = _resampler.ResamplePrepare(
            framesRequested,
            _channels,
            out var inputBuffer,
            out var inputBufferOffset);
        var inputAvailable = _source.Read(
            inputBuffer,
            inputBufferOffset,
            inputNeeded * _channels) / _channels;
        var outputAvailable = _resampler.ResampleOut(
            buffer,
            offset,
            inputAvailable,
            framesRequested,
            _channels);

        return outputAvailable * _channels;
    }

    private void UpdateResamplingRate()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds =
            (now - _lastRateUpdateTimestamp) /
            (double)Stopwatch.Frequency;
        if (elapsedSeconds < UpdateIntervalSeconds)
        {
            return;
        }

        _lastRateUpdateTimestamp = now;
        var currentBufferSeconds =
            _getBufferedDuration().TotalSeconds;
        var bufferAlpha = 1.0 - Math.Exp(
            -elapsedSeconds /
            BufferSmoothingTimeConstantSeconds);
        _smoothedBufferSeconds +=
            (currentBufferSeconds -
             _smoothedBufferSeconds) *
            bufferAlpha;

        UpdateRecoveryMode();
        var desiredCorrection = IsRecoveryMode
            ? GetRecoveryCorrection()
            : GetNormalCorrection();
        var desiredRateRatio = 1.0 + desiredCorrection;
        var rateAlpha = 1.0 - Math.Exp(
            -elapsedSeconds /
            RateSmoothingTimeConstantSeconds);
        var currentRateRatio =
            Volatile.Read(ref _currentRateRatio);
        currentRateRatio +=
            (desiredRateRatio - currentRateRatio) *
            rateAlpha;
        Volatile.Write(
            ref _currentRateRatio,
            currentRateRatio);
        _resampler.SetRates(
            _inputSampleRate * currentRateRatio,
            _outputSampleRate);
    }

    private void UpdateRecoveryMode()
    {
        var targetBufferSeconds = Volatile.Read(
            ref _targetBufferSeconds);
        var softLimitSeconds = Volatile.Read(
            ref _softLimitSeconds);
        var deadBandSeconds = Volatile.Read(
            ref _deadBandSeconds);
        if (IsRecoveryMode)
        {
            if (_smoothedBufferSeconds <=
                targetBufferSeconds +
                deadBandSeconds)
            {
                Volatile.Write(
                    ref _isRecoveryMode,
                    0);
            }

            return;
        }

        if (_smoothedBufferSeconds >= softLimitSeconds)
        {
            Volatile.Write(
                ref _isRecoveryMode,
                1);
        }
    }

    private double GetNormalCorrection()
    {
        var targetBufferSeconds = Volatile.Read(
            ref _targetBufferSeconds);
        var deadBandSeconds = Volatile.Read(
            ref _deadBandSeconds);
        var bufferErrorSeconds =
            _smoothedBufferSeconds -
            targetBufferSeconds;
        if (Math.Abs(bufferErrorSeconds) <=
            deadBandSeconds)
        {
            return 0;
        }

        var adjustedError = bufferErrorSeconds -
                             Math.CopySign(
                                 deadBandSeconds,
                                 bufferErrorSeconds);
        var relativeError =
            adjustedError / targetBufferSeconds;
        return Math.Clamp(
            relativeError * NormalCorrectionStrength,
            -MaximumNormalCorrection,
            MaximumNormalCorrection);
    }

    private double GetRecoveryCorrection()
    {
        var targetBufferSeconds = Volatile.Read(
            ref _targetBufferSeconds);
        var softLimitSeconds = Volatile.Read(
            ref _softLimitSeconds);
        var excessSeconds = Math.Max(
            0,
            _smoothedBufferSeconds -
            softLimitSeconds);
        var relativeExcess =
            excessSeconds / targetBufferSeconds;
        return Math.Clamp(
            MaximumNormalCorrection +
            relativeExcess * RecoveryCorrectionStrength,
            MaximumNormalCorrection,
            MaximumRecoveryCorrection);
    }

    private static void SetMaximum(
        ref double target,
        double value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(
                ref target,
                value,
                current);
            if (observed.Equals(current))
            {
                return;
            }

            current = observed;
        }
    }
}
