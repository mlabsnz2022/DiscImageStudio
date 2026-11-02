using System.Text.Json.Serialization;

namespace DiscImageStudio.Core.Calibration;

/// <summary>A projective map between source pixels and disc-centred millimetres, with Y down.</summary>
public sealed class CalibrationPhotoTransform
{
    private readonly double[] _discToSource;
    private readonly double[] _sourceToDisc;

    [JsonConstructor]
    public CalibrationPhotoTransform(double[] discToSourceMatrix9)
    {
        ArgumentNullException.ThrowIfNull(discToSourceMatrix9);
        if (discToSourceMatrix9.Length != 9 || discToSourceMatrix9.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Photo transform must contain 9 finite numbers.", nameof(discToSourceMatrix9));
        _discToSource = (double[])discToSourceMatrix9.Clone();
        double scale = _discToSource.Max(Math.Abs);
        if (scale <= 0) throw new ArgumentException("Photo transform is not invertible.");
        for (int i = 0; i < 9; i++) _discToSource[i] /= scale;
        _sourceToDisc = Invert(_discToSource);
    }

    /// <summary>Row-major homography; a defensive copy suitable for saving in a session.</summary>
    public double[] DiscToSourceMatrix9 => (double[])_discToSource.Clone();

    public CalibrationPoint SourceToDisc(CalibrationPoint sourcePixel) => Map(_sourceToDisc, sourcePixel);
    public CalibrationPoint DiscToSource(CalibrationPoint discMm) => Map(_discToSource, discMm);

    public bool IsValidForDisc(double physicalOuterRadiusMm)
    {
        if (!double.IsFinite(physicalOuterRadiusMm) || physicalOuterRadiusMm <= 0) return false;
        // The whole physical circle must remain on one finite side of the projective horizon.
        double variation = physicalOuterRadiusMm * Math.Sqrt(_discToSource[6] * _discToSource[6]
            + _discToSource[7] * _discToSource[7]);
        return Math.Abs(_discToSource[8]) > variation * 1.02 + 1e-12;
    }

    internal static CalibrationPoint Map(double[] matrix, CalibrationPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new ArgumentException("Photo coordinates must be finite numbers.");
        double divisor = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        double scale = Math.Abs(matrix[6] * point.X) + Math.Abs(matrix[7] * point.Y) + Math.Abs(matrix[8]);
        if (Math.Abs(divisor) <= Math.Max(1e-15, scale * 1e-12)) throw new ArgumentException("Location is near projective horizon, cannot rectify.");
        double x = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / divisor;
        double y = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / divisor;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Photo transform produced invalid coordinates.");
        return new(x, y);
    }

    internal static double[] Invert(double[] m)
    {
        double[] result =
        [
            m[4] * m[8] - m[5] * m[7], m[2] * m[7] - m[1] * m[8], m[1] * m[5] - m[2] * m[4],
            m[5] * m[6] - m[3] * m[8], m[0] * m[8] - m[2] * m[6], m[2] * m[3] - m[0] * m[5],
            m[3] * m[7] - m[4] * m[6], m[1] * m[6] - m[0] * m[7], m[0] * m[4] - m[1] * m[3],
        ];
        double determinant = m[0] * result[0] + m[1] * result[3] + m[2] * result[6];
        double determinantScale = Math.Abs(m[0] * result[0]) + Math.Abs(m[1] * result[3]) + Math.Abs(m[2] * result[6]);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= Math.Max(1e-20, determinantScale * 1e-12))
            throw new ArgumentException("Photo transform degenerated; cannot recover disc surface coordinates.");
        for (int i = 0; i < 9; i++) result[i] /= determinant;
        return result;
    }
}

public sealed record CalibrationPhotoFitResult(bool Succeeded, CalibrationPhotoTransform? Transform,
    double OuterRmsPixels, double HoleRmsPixels, string Message)
{
    public string? Error => Succeeded ? null : Message;
}

/// <summary>
/// Fits one homography jointly to the known physical outer edge and centre hole.
/// The two rings share a centre and known radii; printed features are never used.
/// </summary>
public static class CalibrationPhotoRectifier
{
    private sealed record Datum(double X, double Y, double Radius, double Weight);

    public static CalibrationPhotoFitResult Fit(IReadOnlyList<CalibrationPoint> outerPixels,
        IReadOnlyList<CalibrationPoint> holePixels, double physicalOuterRadiusMm = 60,
        double physicalHoleRadiusMm = 7.5, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (outerPixels is null || holePixels is null) return Failed("Please input outer physical rim and inner physical rim contours.");
        outerPixels = outerPixels.ToArray();
        holePixels = holePixels.ToArray();
        if (!double.IsFinite(physicalOuterRadiusMm) || !double.IsFinite(physicalHoleRadiusMm)
            || physicalOuterRadiusMm is < 10 or > 100 || physicalHoleRadiusMm <= 0
            || physicalHoleRadiusMm >= physicalOuterRadiusMm * 0.5)
            return Failed("Physical dimensions for disc and inner hole are invalid. Standard disc is 120 mm diameter, 15 mm hole.");
        if (outerPixels.Count is < 5 or > 256 || holePixels.Count is < 5 or > 256)
            return Failed("Outer and inner rims each require at least 5 distributed points, up to 256 points per contour.");
        if (outerPixels.Concat(holePixels).Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            return Failed("Invalid coordinates in contour.");
        double centerX = (outerPixels.Max(p => p.X) + outerPixels.Min(p => p.X)) / 2;
        double centerY = (outerPixels.Max(p => p.Y) + outerPixels.Min(p => p.Y)) / 2;
        double sourceScale = Math.Max(outerPixels.Max(p => p.X) - outerPixels.Min(p => p.X),
            outerPixels.Max(p => p.Y) - outerPixels.Min(p => p.Y)) / 2;
        if (!double.IsFinite(sourceScale) || sourceScale < 5) return Failed("Disc in photo is too small for stable rectification.");
        CalibrationPoint[] outer = outerPixels.Select(p => new CalibrationPoint((p.X - centerX) / sourceScale,
            (p.Y - centerY) / sourceScale)).ToArray();
        CalibrationPoint[] hole = holePixels.Select(p => new CalibrationPoint((p.X - centerX) / sourceScale,
            (p.Y - centerY) / sourceScale)).ToArray();
        if (!IsSpread(outer) || !IsSpread(hole))
            return Failed("Contour points duplicate or nearly collinear; distribute points around outer and inner rims.");
        double[]? initial = InitialAffineEllipse(outer);
        if (initial is null) return Failed("Outer rim points cannot form a stable ellipse; verify points on outer physical rim.");
        double holeExtent = hole.Select(p => Math.Sqrt(Math.Pow(Math.Exp(initial[0]) * p.X + initial[1] * p.Y + initial[2], 2)
            + Math.Pow(Math.Exp(initial[3]) * p.Y + initial[4], 2))).Average();
        if (holeExtent is < 0.015 or > 0.9)
            return Failed("Inner rim does not match outer rim; ensure selected points are on physical inner hole, not printed graphics.");

        double ratio = physicalHoleRadiusMm / physicalOuterRadiusMm;
        Datum[] data = outer.Select(p => new Datum(p.X, p.Y, 1, 1.0 / Math.Sqrt(outer.Length)))
            .Concat(hole.Select(p => new Datum(p.X, p.Y, ratio, 1.0 / Math.Sqrt(hole.Length)))).ToArray();
        double[]? best = null;
        double bestCost = double.PositiveInfinity;
        var seeds = new List<double[]>();
        double[]? jointInitial = InitialJointProjection(initial, hole, ratio);
        if (jointInitial is not null) seeds.Add(jointInitial);
        // Affine outer-ellipse initialization alone is not a rectification result:
        // all seven free homography parameters are optimized against BOTH circles.
        foreach ((double gx, double gy) in new[] { (0.0, 0.0), (0.25, 0.0), (-0.25, 0.0), (0.0, 0.25), (0.0, -0.25) })
        {
            double[] seed = (double[])initial.Clone();
            seed[5] = gx;
            seed[6] = gy;
            seeds.Add(seed);
        }
        foreach (double[] seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (double[] solution, double cost) = Optimize(seed, data, cancellationToken);
            if (cost < bestCost) { best = solution; bestCost = cost; }
            if (bestCost < 1e-20) break;
        }
        if (best is null || !double.IsFinite(bestCost)) return Failed("Both contours cannot produce a valid joint projection; check point placement.");
        try
        {
            double a = Math.Exp(best[0]), b = best[1], c = best[2], d = Math.Exp(best[3]), e = best[4];
            double g = best[5], h = best[6], radius = physicalOuterRadiusMm;
            double[] sourceToDisc =
            [
                radius * a / sourceScale, radius * b / sourceScale, radius * (c - (a * centerX + b * centerY) / sourceScale),
                0, radius * d / sourceScale, radius * (e - d * centerY / sourceScale),
                g / sourceScale, h / sourceScale, 1 - (g * centerX + h * centerY) / sourceScale,
            ];
            var transform = new CalibrationPhotoTransform(CalibrationPhotoTransform.Invert(sourceToDisc));
            if (!transform.IsValidForDisc(radius)) return Failed("Camera angle or contour correspondence caused degenerate projection; retake photo or reposition points.");
            if (!CoversCircle(outerPixels, transform) || !CoversCircle(holePixels, transform))
                return Failed("Contour points are clustered; add points across different directions of both circumferences.");
            (double outerRms, double outerMaximum) = Residual(outerPixels, transform, radius, cancellationToken);
            (double holeRms, double holeMaximum) = Residual(holePixels, transform, physicalHoleRadiusMm, cancellationToken);
            double tolerance = Math.Max(2, sourceScale * 0.006);
            if (outerRms > tolerance || holeRms > tolerance || Math.Max(outerMaximum, holeMaximum) > tolerance * 3)
                return new(false, null, outerRms, holeRms,
                    $"Cannot jointly fit both rims: outer error {outerRms:0.##} px, inner error {holeRms:0.##} px. Check points and disc size.");
            return new(true, transform, outerRms, holeRms,
                $"Jointly rectified outer and inner rims. Contour error: outer {outerRms:0.##} px, inner {holeRms:0.##} px.");
        }
        catch (ArgumentException error) { return Failed(error.Message); }
    }

    private static CalibrationPhotoFitResult Failed(string message) => new(false, null, double.NaN, double.NaN, message);

    private static bool IsSpread(IReadOnlyList<CalibrationPoint> points)
    {
        for (int i = 0; i < points.Count; i++)
        for (int j = 0; j < i; j++)
            if (Math.Pow(points[i].X - points[j].X, 2) + Math.Pow(points[i].Y - points[j].Y, 2) < 1e-10) return false;
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        double xx = points.Sum(p => (p.X - mx) * (p.X - mx));
        double yy = points.Sum(p => (p.Y - my) * (p.Y - my));
        double xy = points.Sum(p => (p.X - mx) * (p.Y - my));
        return xx + yy > 1e-6 && xx * yy - xy * xy > (xx + yy) * (xx + yy) * 1e-5;
    }

    private static double[]? InitialAffineEllipse(IReadOnlyList<CalibrationPoint> points)
    {
        var normal = new double[5, 5];
        var right = new double[5];
        double meanX = points.Average(p => p.X), meanY = points.Average(p => p.Y);
        foreach (CalibrationPoint p in points)
        {
            double x = p.X - meanX, y = p.Y - meanY;
            double[] row = [x * x, 2 * x * y, y * y, x, y];
            for (int i = 0; i < 5; i++)
            {
                right[i] += row[i];
                for (int j = 0; j < 5; j++) normal[i, j] += row[i] * row[j];
            }
        }
        double[]? q = Solve(normal, right);
        if (q is null) return null;
        double determinant = q[0] * q[2] - q[1] * q[1];
        if (q[0] <= 0 || q[2] <= 0 || determinant <= 1e-10) return null;
        double cx = -(q[2] * q[3] - q[1] * q[4]) / (2 * determinant);
        double cy = -(q[0] * q[4] - q[1] * q[3]) / (2 * determinant);
        double k = 1 + q[0] * cx * cx + 2 * q[1] * cx * cy + q[2] * cy * cy;
        if (k <= 0) return null;
        double a = Math.Sqrt(q[0] / k), b = q[1] / (k * a);
        double dSquared = q[2] / k - b * b;
        if (dSquared <= 1e-10) return null;
        double d = Math.Sqrt(dSquared);
        return [Math.Log(a), b, -a * (cx + meanX) - b * (cy + meanY), Math.Log(d), -d * (cy + meanY), 0, 0];
    }

    private static double[]? InitialJointProjection(double[] outerAffine, IReadOnlyList<CalibrationPoint> hole, double ratio)
    {
        double a = Math.Exp(outerAffine[0]), b = outerAffine[1], c = outerAffine[2];
        double d = Math.Exp(outerAffine[3]), e = outerAffine[4];
        CalibrationPoint[] normalizedHole = hole.Select(p => new CalibrationPoint(a * p.X + b * p.Y + c, d * p.Y + e)).ToArray();
        double[]? holeEllipse = InitialAffineEllipse(normalizedHole);
        if (holeEllipse is null) return null;
        double centerY = -holeEllipse[4] / Math.Exp(holeEllipse[3]);
        double centerX = -(holeEllipse[2] + holeEllipse[1] * centerY) / Math.Exp(holeEllipse[0]);
        double length = Math.Sqrt(centerX * centerX + centerY * centerY);
        if (length < 1e-10) return (double[])outerAffine.Clone();
        if (length >= 0.95) return null;

        // After the outer ellipse becomes a unit circle, a disk-preserving
        // projective translation remains. For a concentric circle of radius r,
        // its projected ellipse centre has length c=v(1-r²)/(1-r²v²).
        // Recover v from BOTH the measured hole ellipse and the known radius ratio.
        double r2 = ratio * ratio, oneMinusR2 = 1 - r2;
        double v = 2 * length / (oneMinusR2 + Math.Sqrt(oneMinusR2 * oneMinusR2 + 4 * length * length * r2));
        if (v >= 0.98) return null;
        double nx = centerX / length, ny = centerY / length;
        double vx = v * nx, vy = v * ny, gamma = 1 / Math.Sqrt(1 - v * v);
        double ax = 1 + (gamma - 1) * nx * nx, ay = 1 + (gamma - 1) * ny * ny;
        double cross = (gamma - 1) * nx * ny;
        double[] matrix =
        [
            ax * a, ax * b + cross * d, ax * c + cross * e - gamma * vx,
            cross * a, cross * b + ay * d, cross * c + ay * e - gamma * vy,
            -gamma * vx * a, -gamma * (vx * b + vy * d), gamma * (1 - vx * c - vy * e),
        ];
        // Fix only the free in-plane rotation: rotate the first numerator column
        // onto +X, giving the optimizer its nonredundant seven-parameter form.
        double firstLength = Math.Sqrt(matrix[0] * matrix[0] + matrix[3] * matrix[3]);
        double cosine = matrix[0] / firstLength, sine = matrix[3] / firstLength, scale = matrix[8];
        double newA = firstLength / scale;
        double newB = (cosine * matrix[1] + sine * matrix[4]) / scale;
        double newC = (cosine * matrix[2] + sine * matrix[5]) / scale;
        double newD = (-sine * matrix[1] + cosine * matrix[4]) / scale;
        double newE = (-sine * matrix[2] + cosine * matrix[5]) / scale;
        if (newA <= 0 || newD <= 0) return null;
        return [Math.Log(newA), newB, newC, Math.Log(newD), newE, matrix[6] / scale, matrix[7] / scale];
    }

    private static (double[] Parameters, double Cost) Optimize(double[] parameters, Datum[] data, CancellationToken token)
    {
        double[] residual = Evaluate(parameters, data);
        double cost = Cost(residual), damping = 1e-3;
        for (int iteration = 0; iteration < 120 && double.IsFinite(cost); iteration++)
        {
            token.ThrowIfCancellationRequested();
            var derivatives = new double[7][];
            for (int axis = 0; axis < 7; axis++)
            {
                double step = 1e-5 * Math.Max(1, Math.Abs(parameters[axis]));
                double[] shifted = (double[])parameters.Clone();
                shifted[axis] += step;
                double[] sample = Evaluate(shifted, data);
                derivatives[axis] = sample.Select((value, i) => (value - residual[i]) / step).ToArray();
            }
            var normal = new double[7, 7];
            var right = new double[7];
            for (int i = 0; i < 7; i++)
            {
                for (int n = 0; n < data.Length; n++) right[i] -= derivatives[i][n] * residual[n];
                for (int j = 0; j < 7; j++)
                    for (int n = 0; n < data.Length; n++) normal[i, j] += derivatives[i][n] * derivatives[j][n];
                normal[i, i] += damping * Math.Max(normal[i, i], 1e-8);
            }
            double[]? delta = Solve(normal, right);
            if (delta is null) break;
            double[] candidate = parameters.Select((value, i) => value + delta[i]).ToArray();
            double[] candidateResidual = Evaluate(candidate, data);
            double candidateCost = Cost(candidateResidual);
            if (candidateCost < cost)
            {
                double improvement = cost - candidateCost;
                parameters = candidate;
                residual = candidateResidual;
                cost = candidateCost;
                damping = Math.Max(1e-10, damping / 3);
                if (cost < 1e-22 || improvement < Math.Max(1e-22, cost * 1e-10)) break;
            }
            else
            {
                damping *= 10;
                if (damping > 1e12) break;
            }
        }
        return (parameters, cost);
    }

    private static double[] Evaluate(double[] p, Datum[] data)
    {
        if (p.Any(v => !double.IsFinite(v)) || Math.Abs(p[0]) > 7 || Math.Abs(p[3]) > 7
            || Math.Abs(p[5]) > 10 || Math.Abs(p[6]) > 10)
            return Enumerable.Repeat(double.PositiveInfinity, data.Length).ToArray();
        double a = Math.Exp(p[0]), d = Math.Exp(p[3]);
        double determinant = a * d - a * p[4] * p[6] + p[1] * p[4] * p[5] - p[2] * d * p[5];
        double horizonX = -d * p[5], horizonY = p[1] * p[5] - a * p[6];
        if (determinant <= a * d * 1e-4 || a * d <= Math.Sqrt(horizonX * horizonX + horizonY * horizonY) * 1.02)
            return Enumerable.Repeat(double.PositiveInfinity, data.Length).ToArray();
        var result = new double[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            Datum point = data[i];
            double u = a * point.X + p[1] * point.Y + p[2], v = d * point.Y + p[4];
            double w = p[5] * point.X + p[6] * point.Y + 1;
            double r2 = point.Radius * point.Radius;
            double gx = 2 * (a * u - r2 * p[5] * w);
            double gy = 2 * (p[1] * u + d * v - r2 * p[6] * w);
            double gradient = Math.Sqrt(gx * gx + gy * gy);
            result[i] = w <= 0.03 || gradient < 1e-12 ? double.PositiveInfinity
                : (u * u + v * v - r2 * w * w) / gradient * point.Weight;
        }
        return result;
    }

    private static double Cost(double[] residual) => residual.Sum(value => value * value);

    private static double[]? Solve(double[,] matrix, double[] right)
    {
        int n = right.Length;
        var a = (double[,])matrix.Clone();
        var b = (double[])right.Clone();
        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column])) pivot = row;
            if (!double.IsFinite(a[pivot, column]) || Math.Abs(a[pivot, column]) < 1e-15) return null;
            if (pivot != column)
            {
                for (int j = column; j < n; j++) (a[column, j], a[pivot, j]) = (a[pivot, j], a[column, j]);
                (b[column], b[pivot]) = (b[pivot], b[column]);
            }
            for (int row = column + 1; row < n; row++)
            {
                double multiplier = a[row, column] / a[column, column];
                for (int j = column + 1; j < n; j++) a[row, j] -= multiplier * a[column, j];
                b[row] -= multiplier * b[column];
            }
        }
        var result = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double value = b[i];
            for (int j = i + 1; j < n; j++) value -= a[i, j] * result[j];
            result[i] = value / a[i, i];
            if (!double.IsFinite(result[i])) return null;
        }
        return result;
    }

    private static bool CoversCircle(IReadOnlyList<CalibrationPoint> points, CalibrationPhotoTransform transform)
    {
        double[] angles = points.Select(transform.SourceToDisc).Select(p => Math.Atan2(p.Y, p.X)).Order().ToArray();
        double maximumGap = angles[0] + 2 * Math.PI - angles[^1];
        for (int i = 1; i < angles.Length; i++) maximumGap = Math.Max(maximumGap, angles[i] - angles[i - 1]);
        return maximumGap < Math.PI * 0.95;
    }

    private static (double Rms, double Maximum) Residual(IReadOnlyList<CalibrationPoint> points,
        CalibrationPhotoTransform transform, double radius, CancellationToken token)
    {
        double squared = 0, maximum = 0;
        foreach (CalibrationPoint source in points)
        {
            token.ThrowIfCancellationRequested();
            CalibrationPoint disc = transform.SourceToDisc(source);
            double angle = Math.Atan2(disc.Y, disc.X);
            // Refine the nearest position on the projected circle in source pixels.
            for (int i = 0; i < 15; i++)
            {
                const double step = 1e-5;
                CalibrationPoint p = transform.DiscToSource(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
                CalibrationPoint q = transform.DiscToSource(new(radius * Math.Cos(angle + step), radius * Math.Sin(angle + step)));
                double dx = (q.X - p.X) / step, dy = (q.Y - p.Y) / step;
                double length = dx * dx + dy * dy;
                if (length < 1e-12) break;
                double delta = Math.Clamp(((p.X - source.X) * dx + (p.Y - source.Y) * dy) / length, -0.3, 0.3);
                angle -= delta;
                if (Math.Abs(delta) < 1e-10) break;
            }
            CalibrationPoint closest = transform.DiscToSource(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
            double distance = Math.Sqrt(Math.Pow(closest.X - source.X, 2) + Math.Pow(closest.Y - source.Y, 2));
            squared += distance * distance;
            maximum = Math.Max(maximum, distance);
        }
        return (Math.Sqrt(squared / points.Count), maximum);
    }
}
