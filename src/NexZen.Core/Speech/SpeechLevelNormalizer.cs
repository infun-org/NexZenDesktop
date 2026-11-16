namespace NexZen.Core.Speech;

/// <summary>
/// Evens out the loudness of generated speech: the model renders each sentence at its own level
/// (several dB apart), which sounds like the volume jumping. Each sentence is brought to the same
/// speech level: the first ~0.6 s is held back to measure it, then the gain follows the sentence's
/// running speech level (silence is ignored), changing slowly so syllables keep their shape.
/// A soft limiter catches peaks the gain pushes past full scale.
/// </summary>
public sealed class SpeechLevelNormalizer
{
    private const double SilenceRms = 0.01;
    private readonly int _window;
    private readonly int _holdSamples;
    private readonly double _target;
    private readonly double _minGain, _maxGain;
    private readonly double _maxStep; // gain change allowed per window (ratio)

    private readonly List<float> _held = new();
    private bool _holding = true;
    private double _gain = 1;
    private double _speechSquares;
    private long _speechSamples;
    private double _windowSquares;
    private int _windowFill;

    public SpeechLevelNormalizer(int sampleRate, double targetDb = -21, double holdSeconds = 0.6,
        double maxBoostDb = 14, double maxCutDb = 14)
    {
        _window = Math.Max(1, sampleRate / 20); // 50 ms
        _holdSamples = (int)(sampleRate * holdSeconds);
        _target = Math.Pow(10, targetDb / 20);
        _maxGain = Math.Pow(10, maxBoostDb / 20);
        _minGain = Math.Pow(10, -maxCutDb / 20);
        _maxStep = Math.Pow(10, 0.4 / 20); // 0.4 dB per 50 ms
    }

    /// <summary>Starts a new sentence: its level is measured afresh.</summary>
    public void BeginSentence()
    {
        _held.Clear();
        _holding = true;
        _speechSquares = 0;
        _speechSamples = 0;
        _windowSquares = 0;
        _windowFill = 0;
    }

    /// <summary>Adds generated samples; levelled samples go to <paramref name="emit"/> (possibly later).</summary>
    public void Push(float[] samples, int count, Action<float[], int> emit)
    {
        Measure(samples, count);
        if (_holding)
        {
            for (var i = 0; i < count; i++)
                _held.Add(samples[i]);
            if (_held.Count < _holdSamples)
                return;
            ReleaseHeld(emit);
            return;
        }
        var output = new float[count];
        Array.Copy(samples, output, count);
        Apply(output, count);
        emit(output, count);
    }

    /// <summary>Ends the sentence, emitting anything still held back (a short sentence).</summary>
    public void EndSentence(Action<float[], int> emit)
    {
        if (_holding)
            ReleaseHeld(emit);
    }

    private void ReleaseHeld(Action<float[], int> emit)
    {
        _holding = false;
        // The first measurement sets the gain directly; afterwards it only moves gradually.
        _gain = DesiredGain() ?? _gain;
        if (_held.Count == 0)
            return;
        var output = _held.ToArray();
        _held.Clear();
        Apply(output, output.Length, smooth: false);
        emit(output, output.Length);
    }

    private double? DesiredGain()
    {
        if (_speechSamples < _window)
            return null;
        var rms = Math.Sqrt(_speechSquares / _speechSamples);
        return Math.Clamp(_target / rms, _minGain, _maxGain);
    }

    private void Measure(float[] samples, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var v = samples[i];
            _windowSquares += v * v;
            if (++_windowFill < _window)
                continue;
            var rms = Math.Sqrt(_windowSquares / _window);
            if (rms >= SilenceRms)
            {
                _speechSquares += _windowSquares;
                _speechSamples += _window;
            }
            _windowSquares = 0;
            _windowFill = 0;
        }
    }

    private void Apply(float[] buffer, int count, bool smooth = true)
    {
        var start = _gain;
        var end = start;
        if (smooth && DesiredGain() is { } desired)
        {
            var steps = Math.Max(1.0, (double)count / _window);
            var maxRatio = Math.Pow(_maxStep, steps);
            end = Math.Clamp(desired, start / maxRatio, start * maxRatio);
        }
        for (var i = 0; i < count; i++)
        {
            var g = count > 1 ? start + (end - start) * i / (count - 1) : end;
            buffer[i] = SoftLimit((float)(buffer[i] * g));
        }
        _gain = end;
    }

    /// <summary>Unchanged below 0.8; above it, bends smoothly towards 1.0 instead of clipping.</summary>
    public static float SoftLimit(float x)
    {
        var a = Math.Abs(x);
        if (a <= 0.8f)
            return x;
        var bent = 0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f);
        return x < 0 ? -bent : bent;
    }
}
