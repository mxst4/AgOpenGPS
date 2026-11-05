using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AgOpenGPS.Core.FarmTelemetry
{
    public static class FarmBoundaryValidator
    {
        private sealed class Segment
        {
            public double[] A;
            public double[] B;
            public int Ring;
            public int Index;
            public int RingSize;
            public double MinX => Math.Min(A[0], B[0]);
            public double MaxX => Math.Max(A[0], B[0]);
            public double MinY => Math.Min(A[1], B[1]);
            public double MaxY => Math.Max(A[1], B[1]);
        }

        public static bool IsValid(double[][][] rings, CancellationToken cancellationToken)
        {
            if (rings.Length == 0) return false;
            var segments = new List<Segment>();
            for (int ringIndex = 0; ringIndex < rings.Length; ringIndex++)
            {
                var ring = rings[ringIndex];
                if (ring.Length < 4 || !ring[0].SequenceEqual(ring[ring.Length - 1])) return false;
                double area = 0;
                for (int i = 0; i < ring.Length - 1; i++)
                {
                    if (ring[i].SequenceEqual(ring[i + 1])) return false;
                    area += Cross(ring[0], ring[i], ring[i + 1]);
                    segments.Add(new Segment { A = ring[i], B = ring[i + 1], Ring = ringIndex, Index = i, RingSize = ring.Length - 1 });
                }
                if (Math.Abs(area) < 1e-16) return false;
            }
            var active = new List<Segment>();
            foreach (var segment in segments.OrderBy(s => s.MinX))
            {
                cancellationToken.ThrowIfCancellationRequested();
                active.RemoveAll(s => s.MaxX < segment.MinX);
                foreach (var previous in active)
                {
                    if (previous.MaxY < segment.MinY || segment.MaxY < previous.MinY) continue;
                    bool adjacent = previous.Ring == segment.Ring &&
                        (Math.Abs(previous.Index - segment.Index) == 1 || Math.Abs(previous.Index - segment.Index) == segment.RingSize - 1);
                    if (!adjacent && Intersects(previous, segment)) return false;
                }
                active.Add(segment);
            }
            for (int hole = 1; hole < rings.Length; hole++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Contains(rings[0], rings[hole][0])) return false;
                for (int other = 1; other < hole; other++)
                    if (Contains(rings[other], rings[hole][0]) || Contains(rings[hole], rings[other][0])) return false;
            }
            return true;
        }

        private static bool Intersects(Segment a, Segment b)
        {
            double ac = Cross(a.A, a.B, b.A);
            double ad = Cross(a.A, a.B, b.B);
            double ca = Cross(b.A, b.B, a.A);
            double cb = Cross(b.A, b.B, a.B);
            return ((ac <= 0 && ad >= 0) || (ac >= 0 && ad <= 0)) &&
                ((ca <= 0 && cb >= 0) || (ca >= 0 && cb <= 0));
        }

        private static double Cross(double[] a, double[] b, double[] c)
            => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);

        private static bool Contains(double[][] ring, double[] point)
        {
            bool inside = false;
            for (int i = 0; i < ring.Length - 1; i++)
            {
                var a = ring[i];
                var b = ring[i + 1];
                if ((a[1] > point[1]) != (b[1] > point[1]) &&
                    point[0] < (b[0] - a[0]) * (point[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
            }
            return inside;
        }
    }
}
