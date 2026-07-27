using NAudio.Dsp;
using NAudio.Wave;

namespace AudioRouter;

public sealed class AdaptiveResamplingSampleProvider : ISampleProvider
{
    private const double MaximumRateCorrection = 0.005;
    private const double CorrectionStrength = 0.003;
    private const double SmoothingFactor = 0.02;

    private readonly ISampleProvider _source;
    private readonly WdlResampler _resampler;
    private readonly Func<TimeSpan> _getBufferedDuration;
    private readonly double _targetBufferSeconds;
    private readonly int _channels;
    private readonly int _sampleRate;
    private double _currentRateRatio = 1.0;

    public AdaptiveResamplingSampleProvider(
        ISampleProvider source,
        Func<TimeSpan> getBufferedDuration,
        TimeSpan targetBufferDuration)
    {
        _source = source;
        _getBufferedDuration = getBufferedDuration;
        _targetBufferSeconds = Math.Max(
            targetBufferDuration.TotalSeconds,
            0.02);
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            _sampleRate,
            _channels);

        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        _resampler.SetRates(_sampleRate, _sampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
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
        var bufferErrorSeconds =
            _getBufferedDuration().TotalSeconds - _targetBufferSeconds;
        var relativeError = bufferErrorSeconds / _targetBufferSeconds;
        var desiredCorrection = Math.Clamp(
            relativeError * CorrectionStrength,
            -MaximumRateCorrection,
            MaximumRateCorrection);
        var desiredRateRatio = 1.0 + desiredCorrection;

        _currentRateRatio +=
            (desiredRateRatio - _currentRateRatio) * SmoothingFactor;
        _resampler.SetRates(
            _sampleRate * _currentRateRatio,
            _sampleRate);
    }
}
