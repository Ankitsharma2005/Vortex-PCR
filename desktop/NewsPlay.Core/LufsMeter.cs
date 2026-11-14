namespace NewsPlay.Core;

// Stereo 48 kHz BS.1770 K-weighted, 400 ms momentary measurement.
// This is not an integrated programme compliance meter.
public sealed class LufsMeter
{
    private sealed class Filter(double b0, double b1, double b2, double a1, double a2)
    {
        private double z1, z2;
        public double Run(double x) { var y = b0 * x + z1; z1 = b1 * x - a1 * y + z2; z2 = b2 * x - a2 * y; return y; }
    }
    private readonly Filter[] shelf = [Shelf(), Shelf()];
    private readonly Filter[] highpass = [Highpass(), Highpass()];
    private readonly double[] energy = new double[19200];
    private double sum;
    private int index, count;
    private static Filter Shelf() => new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, .73248077421585);
    private static Filter Highpass() => new(1, -2, 1, -1.99004745483398, .99007225036621);
    public double Measure(short[] stereo)
    {
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            double e = 0;
            for (var ch = 0; ch < 2; ch++) { var y = highpass[ch].Run(shelf[ch].Run(stereo[i + ch] / 32768d)); e += y * y; }
            sum += e - energy[index]; energy[index] = e; index = (index + 1) % energy.Length; count = Math.Min(count + 1, energy.Length);
        }
        return count < energy.Length || sum <= 0 ? double.NegativeInfinity : -.691 + 10 * Math.Log10(sum / count);
    }
}
