using System.Diagnostics;
using NAudio.Dsp;
using NAudio.Wave;

namespace AudioRouter;

public sealed class AdaptiveResamplingSampleProvider : ISampleProvider
{
    private const double MaximumRateCorrection = 0.001;
    private const double CorrectionStrength = 0.001;
    private const double RateSmoothingFactor = 0.25;
    private const double BufferSmoothingFactor = 0.02;
    private const double UpdateIntervalSeconds = 0.5;
    private const double MinimumTargetBufferSeconds = 0.005;

    private readonly ISampleProvider _source;
    private readonly WdlResampler _resampler;
    private readonly Func<TimeSpan> _getBufferedDuration;
    private readonly double _targetBufferSeconds;
    private readonly int _channels;
    private readonly int _inputSampleRate;
    private readonly int _outputSampleRate;
    private double _currentRateRatio = 1.0;
    private double _smoothedBufferSeconds;
    private long _lastRateUpdateTimestamp;

    public AdaptiveResamplingSampleProvider(
        ISampleProvider source,
        int outputSampleRate,
        Func<TimeSpan> getBufferedDuration,
        TimeSpan targetBufferDuration)
    {
        _source = source;
        _getBufferedDuration = getBufferedDuration;
        _targetBufferSeconds = Math.Max(
            targetBufferDuration.TotalSeconds,
            MinimumTargetBufferSeconds);
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
        _resampler.SetRates(_inputSampleRate, _outputSampleRate);
    }

    public WaveFormat WaveFormat { get; }

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
        var currentBufferSeconds = _getBufferedDuration().TotalSeconds;
        _smoothedBufferSeconds +=
            (currentBufferSeconds - _smoothedBufferSeconds) *
            BufferSmoothingFactor;

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds =
            (now - _lastRateUpdateTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds < UpdateIntervalSeconds)
        {
            return;
        }

        _lastRateUpdateTimestamp = now;
        var bufferErrorSeconds =
            _smoothedBufferSeconds - _targetBufferSeconds;
        var relativeError = bufferErrorSeconds / _targetBufferSeconds;
        var desiredCorrection = Math.Clamp(
            relativeError * CorrectionStrength,
            -MaximumRateCorrection,
            MaximumRateCorrection);
        var desiredRateRatio = 1.0 + desiredCorrection;

        _currentRateRatio +=
            (desiredRateRatio - _currentRateRatio) * RateSmoothingFactor;
        _resampler.SetRates(
            _inputSampleRate * _currentRateRatio,
            _outputSampleRate);
    }
}
