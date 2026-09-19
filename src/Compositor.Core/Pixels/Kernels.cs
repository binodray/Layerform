namespace Compositor.Pixels;

/// <summary>C# ports of HealPixels.c, ContentFill.c, NoisePixels.c and LensPixels.c (premultiplied RGBA, rows top-down).</summary>
public static class Kernels
{
    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    // MARK: Noise

    private static float NoiseUnit(uint key) => (Hash(key) >> 8) * (1.0f / 16777216.0f);

    /// <summary>noise_add: Photoshop's Add Noise; the same seed gives the same grain.</summary>
    public static void AddNoise(Span<byte> rgba, int width, int height, int stride, float amount, bool gaussian, bool monochromatic, uint seed)
    {
        float spread = amount / 100.0f * 127.5f;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 4;
                uint alpha = rgba[p + 3];
                if (alpha == 0) continue;
                uint baseKey = Hash(seed ^ Hash(unchecked((uint)(y * width + x))));
                for (int c = 0; c < 3; c++)
                {
                    uint key = monochromatic ? baseKey : unchecked(baseKey + (uint)c * 0x9e3779b9U);
                    float n;
                    if (gaussian)
                    {
                        float u1 = NoiseUnit(key), u2 = NoiseUnit(key ^ 0x68e31da4U);
                        n = MathF.Sqrt(-2.0f * MathF.Log(1.0f - u1)) * MathF.Cos(6.2831853f * u2) * spread * (2.0f / 3.0f);
                    }
                    else n = (NoiseUnit(key) * 2.0f - 1.0f) * spread;
                    float value = rgba[p + c] * 255.0f / alpha + n;
                    value = value < 0 ? 0 : value > 255 ? 255 : value;
                    rgba[p + c] = (byte)MathF.Round(value * alpha / 255.0f, MidpointRounding.AwayFromZero);
                }
            }
        }
    }

    // MARK: Lens

    /// <summary>lens_distort: radial distortion; k &gt; 0 straightens barrel, k &lt; 0 pincushion.</summary>
    public static void LensDistort(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int stride, double k)
    {
        double cx = width * 0.5, cy = height * 0.5;
        double halfDiagonal2 = cx * cx + cy * cy;
        Span<double> sums = stackalloc double[4];
        for (int y = 0; y < height; y++)
        {
            double dy = y + 0.5 - cy;
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - cx;
                double scale = 1.0 - k * (dx * dx + dy * dy) / halfDiagonal2;
                double sx = cx + dx * scale - 0.5, sy = cy + dy * scale - 0.5;
                double fx0 = Math.Floor(sx), fy0 = Math.Floor(sy);
                double fx = sx - fx0, fy = sy - fy0;
                long x0 = (long)fx0, y0 = (long)fy0;
                sums.Clear();
                for (int j = 0; j < 2; j++)
                {
                    long row = y0 + j;
                    if (row < 0 || row >= height) continue;
                    double wy = j == 1 ? fy : 1 - fy;
                    if (wy == 0) continue;
                    for (int i = 0; i < 2; i++)
                    {
                        long column = x0 + i;
                        if (column < 0 || column >= width) continue;
                        double weight = wy * (i == 1 ? fx : 1 - fx);
                        if (weight == 0) continue;
                        int p = (int)(row * stride + column * 4);
                        for (int c = 0; c < 4; c++) sums[c] += weight * source[p + c];
                    }
                }
                int o = y * stride + x * 4;
                for (int c = 0; c < 4; c++) destination[o + c] = (byte)Math.Round(sums[c], MidpointRounding.AwayFromZero);
            }
        }
    }

    // MARK: Content-Aware Fill

    /// <summary>content_fill: fills masked pixels from unselected opaque ones with randomized patch search.
    /// Returns 1 on success, 0 when there is nothing to copy from.</summary>
    public static int ContentFill(Span<byte> pixels, int stride, ReadOnlySpan<byte> mask, int maskStride, int w, int h)
    {
        int n = w * h;
        var known = new bool[n]; var target = new bool[n]; var valid = new bool[n]; var queued = new bool[n];
        var donors = new int[n]; var queue = new int[n]; var chosen = new int[n];
        int radius = w >= 5 && h >= 5 ? 2 : 0;
        int missing = 0, donorCount = 0, head = 0, tail = 0, scan = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                target[p] = mask[y * maskStride + x] != 0;
                known[p] = !target[p] && pixels[y * stride + x * 4 + 3] == 255;
                chosen[p] = -1;
                if (target[p]) missing++;
            }
        if (missing == 0) return 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (!known[p]) continue;
                bool ok = true;
                for (int dy = -radius; dy <= radius && ok; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h || !known[sy * w + sx]) { ok = false; break; }
                    }
                if (ok) { valid[p] = true; donors[donorCount++] = p; }
            }
        if (donorCount == 0) return 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                if (target[p] && ((x > 0 && known[p - 1]) || (x + 1 < w && known[p + 1]) || (y > 0 && known[p - w]) || (y + 1 < h && known[p + w])))
                {
                    queue[tail++] = p;
                    queued[p] = true;
                }
            }
        uint seed = 0x6d2b79f5;
        uint NextRandom() { seed = unchecked(seed * 1664525u + 1013904223u); return seed; }
        double Match(Span<byte> px, int p, int q)
        {
            int ppx = p % w, ppy = p / w, qx = q % w, qy = q / w, count = 0;
            double sum = 0;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = ppx + dx, y = ppy + dy, sx = qx + dx, sy = qy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h || sx < 0 || sy < 0 || sx >= w || sy >= h || !known[y * w + x]) continue;
                    int a = y * stride + x * 4, b = sy * stride + sx * 4;
                    for (int c = 0; c < 4; c++) { int d = px[a + c] - px[b + c]; sum += d * d; }
                    count++;
                }
            return count > 0 ? sum / count : double.MaxValue;
        }
        Span<int> neighbors = stackalloc int[4];
        for (;;)
        {
            while (head < tail)
            {
                int p = queue[head++], x = p % w, y = p / w, best = -1;
                double score = double.MaxValue;
                neighbors[0] = x > 0 ? p - 1 : -1; neighbors[1] = x + 1 < w ? p + 1 : -1;
                neighbors[2] = y > 0 ? p - w : -1; neighbors[3] = y + 1 < h ? p + w : -1;
                for (int k = 0; k < 28; k++)
                {
                    int q = -1;
                    if (k < 4)
                    {
                        int t = neighbors[k];
                        if (t >= 0) q = (chosen[t] >= 0 ? chosen[t] : t) + (p - t);
                    }
                    else q = donors[NextRandom() % (uint)donorCount];
                    if (q < 0 || q >= n || !valid[q]) continue;
                    double s = Match(pixels, p, q);
                    if (best < 0 || s < score) { score = s; best = q; }
                }
                if (best < 0) best = donors[0];
                for (int r = 64; r >= 1; r /= 2)
                {
                    int qx = best % w + (int)(NextRandom() % (uint)(2 * r + 1)) - r;
                    int qy = best / w + (int)(NextRandom() % (uint)(2 * r + 1)) - r;
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h || !valid[qy * w + qx]) continue;
                    int q = qy * w + qx;
                    double s = Match(pixels, p, q);
                    if (s < score) { score = s; best = q; }
                }
                pixels.Slice((best / w) * stride + (best % w) * 4, 4).CopyTo(pixels.Slice(y * stride + x * 4, 4));
                known[p] = true;
                chosen[p] = best;
                for (int k = 0; k < 4; k++)
                {
                    int q = neighbors[k];
                    if (q >= 0 && target[q] && !known[q] && !queued[q]) { queued[q] = true; queue[tail++] = q; }
                }
            }
            while (scan < n && (!target[scan] || known[scan])) scan++;
            if (scan >= n) break;
            queue[tail++] = scan;
            queued[scan] = true;
        }
        return 1;
    }

    // MARK: Spot Healing

    private const byte Outside = 0, Ring = 1, Hole = 2;
    private static double HealUnit(uint key) => (Hash(key) >> 8) / 16777216.0;

    private static double HealScore(ReadOnlySpan<byte> rgba, int stride, byte[] role, long wx0, long wy0, long ww, long wh, long dx, long dy, long W, long H)
    {
        if (Math.Abs(dx) < ww && Math.Abs(dy) < wh) return double.PositiveInfinity;
        if (wx0 + dx < 0 || wy0 + dy < 0 || wx0 + ww + dx > W || wy0 + wh + dy > H) return double.PositiveInfinity;
        double sum = 0;
        long n = 0;
        for (long y = 0; y < wh; y++)
            for (long x = 0; x < ww; x++)
            {
                if (role[y * ww + x] != Ring) continue;
                long t = (wy0 + y) * stride + (wx0 + x) * 4;
                long s = (wy0 + y + dy) * stride + (wx0 + x + dx) * 4;
                for (int c = 0; c < 4; c++) { double d = (double)rgba[(int)(t + c)] - rgba[(int)(s + c)]; sum += d * d; }
                n++;
            }
        return n > 0 ? sum / n : double.PositiveInfinity;
    }

    private static void HealSolve(float[] value, byte[] role, long w, long h, int depth)
    {
        int iterations = 300;
        if (w > 32 && h > 32 && depth < 16)
        {
            long cw = (w + 1) / 2, ch = (h + 1) / 2;
            var coarse = new float[cw * ch * 4];
            var coarseRole = new byte[cw * ch];
            Span<float> knownSum = stackalloc float[4], holeSum = stackalloc float[4];
            for (long y = 0; y < ch; y++)
                for (long x = 0; x < cw; x++)
                {
                    int known = 0, hole = 0;
                    knownSum.Clear(); holeSum.Clear();
                    for (long j = 0; j < 2; j++)
                        for (long i = 0; i < 2; i++)
                        {
                            long fx = x * 2 + i, fy = y * 2 + j;
                            if (fx >= w || fy >= h) continue;
                            long p = fy * w + fx;
                            if (role[p] == Ring) { known++; for (int c = 0; c < 4; c++) knownSum[c] += value[p * 4 + c]; }
                            else if (role[p] == Hole) { hole++; for (int c = 0; c < 4; c++) holeSum[c] += value[p * 4 + c]; }
                        }
                    long q = y * cw + x;
                    if (known > 0) { coarseRole[q] = Ring; for (int c = 0; c < 4; c++) coarse[q * 4 + c] = knownSum[c] / known; }
                    else if (hole > 0) { coarseRole[q] = Hole; for (int c = 0; c < 4; c++) coarse[q * 4 + c] = holeSum[c] / hole; }
                }
            HealSolve(coarse, coarseRole, cw, ch, depth + 1);
            for (long y = 0; y < h; y++)
                for (long x = 0; x < w; x++)
                {
                    long p = y * w + x, q = (y / 2) * cw + x / 2;
                    if (role[p] == Hole && coarseRole[q] == Hole) Array.Copy(coarse, q * 4, value, p * 4, 4);
                }
            iterations = 40;
        }
        const float omega = 1.8f;
        Span<float> sum = stackalloc float[4];
        for (int it = 0; it < iterations; it++)
            for (long y = 0; y < h; y++)
                for (long x = 0; x < w; x++)
                {
                    long p = y * w + x;
                    if (role[p] != Hole) continue;
                    sum.Clear();
                    int n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        long nx = k == 0 ? x - 1 : k == 1 ? x + 1 : x, ny = k == 2 ? y - 1 : k == 3 ? y + 1 : y;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        long q = ny * w + nx;
                        if (role[q] == Outside) continue;
                        for (int c = 0; c < 4; c++) sum[c] += value[q * 4 + c];
                        n++;
                    }
                    if (n == 0) continue;
                    for (int c = 0; c < 4; c++) value[p * 4 + c] += omega * (sum[c] / n - value[p * 4 + c]);
                }
    }

    /// <summary>spot_heal: rebuilds the covered area from nearby texture (mode 0 Content-Aware, 1 Create Texture,
    /// 2 Proximity Match), blended so it meets the surrounding tone. Coverage is width × height bytes.</summary>
    public static void SpotHeal(Span<byte> rgba, ReadOnlySpan<byte> coverage, int width, int height, int stride, float opacity, int mode, uint seed)
    {
        long W = width, H = height;
        var (bx0, by0, bx1, by1) = Imaging.PixelOps.CoverageBounds(coverage, width, height, width);
        if (bx1 <= bx0) return;
        long bw = bx1 - bx0, bh = by1 - by0, size = Math.Max(bw, bh);
        long ring = Math.Clamp(size / 8, 2, 16);
        long wx0 = Math.Max(0, bx0 - ring), wy0 = Math.Max(0, by0 - ring);
        long wx1 = Math.Min(W, bx1 + ring), wy1 = Math.Min(H, by1 + ring);
        long ww = wx1 - wx0, wh = wy1 - wy0, wn = ww * wh;
        var role = new byte[wn];
        var near = new byte[wn];
        var prefix = new long[Math.Max(ww, wh) + 1];
        var value = new float[wn * 4];
        for (long y = 0; y < wh; y++)
            for (long x = 0; x < ww; x++)
                role[y * ww + x] = coverage[(int)((wy0 + y) * width + wx0 + x)] != 0 ? Hole : Outside;
        for (long y = 0; y < wh; y++)
        {
            prefix[0] = 0;
            for (long x = 0; x < ww; x++) prefix[x + 1] = prefix[x] + (role[y * ww + x] == Hole ? 1 : 0);
            for (long x = 0; x < ww; x++)
            {
                long lo = Math.Max(0, x - ring), hi = Math.Min(ww, x + ring + 1);
                near[y * ww + x] = (byte)(prefix[hi] - prefix[lo] > 0 ? 1 : 0);
            }
        }
        for (long x = 0; x < ww; x++)
        {
            prefix[0] = 0;
            for (long y = 0; y < wh; y++) prefix[y + 1] = prefix[y] + near[y * ww + x];
            for (long y = 0; y < wh; y++)
            {
                long lo = Math.Max(0, y - ring), hi = Math.Min(wh, y + ring + 1);
                if (role[y * ww + x] == Outside && prefix[hi] - prefix[lo] > 0) role[y * ww + x] = Ring;
            }
        }
        long ringCount = 0;
        for (long p = 0; p < wn; p++) if (role[p] == Ring) ringCount++;
        if (ringCount == 0) return;

        long ox = 0, oy = 0;
        bool haveSource = false;
        if (mode != 1)
        {
            double[] factors = { 1.05, 1.35, 1.75, 2.25, 2.8 };
            int count = mode == 2 ? 2 : 5;
            double best = double.PositiveInfinity;
            for (int f = 0; f < count; f++)
                for (int a = 0; a < 24; a++)
                {
                    double angle = a * Math.PI / 12.0;
                    long dx = (long)Math.Round(Math.Cos(angle) * factors[f] * ww, MidpointRounding.AwayFromZero);
                    long dy = (long)Math.Round(Math.Sin(angle) * factors[f] * wh, MidpointRounding.AwayFromZero);
                    double score = HealScore(rgba, stride, role, wx0, wy0, ww, wh, dx, dy, W, H);
                    if (!double.IsFinite(score)) continue;
                    score *= mode == 2 ? 1.0 + 0.6 * f : 1.0 + 0.1 * f;
                    if (score < best) { best = score; ox = dx; oy = dy; }
                }
            if (double.IsFinite(best))
            {
                long cx = ox, cy = oy;
                double refined = HealScore(rgba, stride, role, wx0, wy0, ww, wh, cx, cy, W, H);
                for (long j = -3; j <= 3; j++)
                    for (long i = -3; i <= 3; i++)
                    {
                        double score = HealScore(rgba, stride, role, wx0, wy0, ww, wh, cx + i, cy + j, W, H);
                        if (score < refined) { refined = score; ox = cx + i; oy = cy + j; }
                    }
                haveSource = true;
            }
        }

        var mean = new double[4];
        var detail = new double[3];
        for (long y = 0; y < wh; y++)
            for (long x = 0; x < ww; x++)
            {
                long p = y * ww + x;
                if (role[p] != Ring) { value[p * 4] = value[p * 4 + 1] = value[p * 4 + 2] = value[p * 4 + 3] = 0; continue; }
                long ix = wx0 + x, iy = wy0 + y;
                int t = (int)(iy * stride + ix * 4);
                int s = haveSource ? (int)((iy + oy) * stride + (ix + ox) * 4) : -1;
                for (int c = 0; c < 4; c++)
                {
                    value[p * 4 + c] = rgba[t + c] - (s >= 0 ? rgba[s + c] : 0);
                    mean[c] += value[p * 4 + c];
                }
                if (!haveSource)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        double around = 0; int n = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            long nx = k == 0 ? ix - 1 : k == 1 ? ix + 1 : ix, ny = k == 2 ? iy - 1 : k == 3 ? iy + 1 : iy;
                            if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                            around += rgba[(int)(ny * stride + nx * 4 + c)];
                            n++;
                        }
                        if (n > 0) { double d = rgba[t + c] - around / n; detail[c] += d * d; }
                    }
                }
            }
        for (int c = 0; c < 4; c++) mean[c] /= ringCount;
        for (long p = 0; p < wn; p++)
            if (role[p] == Hole) for (int c = 0; c < 4; c++) value[p * 4 + c] = (float)mean[c];
        HealSolve(value, role, ww, wh, 0);
        for (int c = 0; c < 3; c++) detail[c] = Math.Sqrt(detail[c] / ringCount) * 0.9;

        var output = new double[4];
        for (long y = 0; y < wh; y++)
            for (long x = 0; x < ww; x++)
            {
                long p = y * ww + x;
                if (role[p] != Hole) continue;
                long ix = wx0 + x, iy = wy0 + y;
                int t = (int)(iy * stride + ix * 4);
                int s = haveSource ? (int)((iy + oy) * stride + (ix + ox) * 4) : -1;
                double amount = coverage[(int)(iy * width + ix)] / 255.0 * opacity;
                double grain = 0;
                if (!haveSource)
                {
                    uint key = Hash(seed ^ Hash(unchecked((uint)(iy * W + ix))));
                    double u1 = HealUnit(key), u2 = HealUnit(key ^ 0x68e31da4U);
                    grain = Math.Sqrt(-2.0 * Math.Log(1.0 - u1)) * Math.Cos(2.0 * Math.PI * u2);
                }
                for (int c = 0; c < 4; c++)
                {
                    double healed = (s >= 0 ? rgba[s + c] : 0) + value[p * 4 + c] + (c < 3 ? grain * detail[c] : 0);
                    output[c] = rgba[t + c] + (healed - rgba[t + c]) * amount;
                }
                double alpha = Math.Clamp(output[3], 0, 255);
                rgba[t + 3] = (byte)Math.Round(alpha, MidpointRounding.AwayFromZero);
                for (int c = 0; c < 3; c++) rgba[t + c] = (byte)Math.Round(Math.Clamp(output[c], 0, rgba[t + 3]), MidpointRounding.AwayFromZero);
            }
    }

    // MARK: Guided matte (GuidedMatte.swift)

    public static float[] BoxMean(float[] source, int width, int height, int radius)
    {
        float span = radius * 2 + 1;
        var pass = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float sum = 0;
            for (int x = -radius; x <= radius; x++) sum += source[row + Math.Clamp(x, 0, width - 1)];
            for (int x = 0; x < width; x++)
            {
                pass[row + x] = sum / span;
                sum -= source[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        }
        var result = new float[width * height];
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            for (int y = -radius; y <= radius; y++) sum += pass[Math.Clamp(y, 0, height - 1) * width + x];
            for (int y = 0; y < height; y++)
            {
                result[y * width + x] = sum / span;
                sum -= pass[Math.Clamp(y - radius, 0, height - 1) * width + x];
                sum += pass[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
            }
        }
        return result;
    }

    public static float[] GuidedFilter(float[] mask, float[] guide, int width, int height, int radius, float epsilon)
    {
        int count = width * height;
        var meanGuide = BoxMean(guide, width, height, radius);
        var meanMask = BoxMean(mask, width, height, radius);
        var squares = new float[count];
        var products = new float[count];
        for (int i = 0; i < count; i++) { squares[i] = guide[i] * guide[i]; products[i] = guide[i] * mask[i]; }
        var meanSquares = BoxMean(squares, width, height, radius);
        var meanProducts = BoxMean(products, width, height, radius);
        var slope = new float[count];
        var offset = new float[count];
        for (int i = 0; i < count; i++)
        {
            float variance = meanSquares[i] - meanGuide[i] * meanGuide[i];
            float covariance = meanProducts[i] - meanGuide[i] * meanMask[i];
            slope[i] = covariance / (variance + epsilon);
            offset[i] = meanMask[i] - slope[i] * meanGuide[i];
        }
        var meanSlope = BoxMean(slope, width, height, radius);
        var meanOffset = BoxMean(offset, width, height, radius);
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = Math.Clamp(meanSlope[i] * guide[i] + meanOffset[i], 0, 1);
        return result;
    }
}
