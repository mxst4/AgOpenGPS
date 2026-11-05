using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AgOpenGPS.Core.Models;

namespace AgOpenGPS.Core.FarmTelemetry
{
    public sealed class FarmFieldCapture
    {
        public string Directory { get; }
        public string Name { get; }
        public Wgs84 Origin { get; }
        public GeoDelta Drift { get; }

        public FarmFieldCapture(string directory, string name, Wgs84 origin, GeoDelta drift)
        {
            Directory = directory;
            Name = name;
            Origin = origin;
            Drift = drift;
        }
    }

    public sealed class PreparedFarmField
    {
        public FarmField Field { get; }
        public FarmCoverage Coverage { get; }
        private readonly string _directory;
        private readonly long _revision;
        private readonly string _originFile;

        internal PreparedFarmField(FarmField field, FarmCoverage coverage, string directory, long revision, string originFile)
        {
            Field = field;
            Coverage = coverage;
            _directory = directory;
            _revision = revision;
            _originFile = originFile;
        }

        public bool IsCurrent()
        {
            try
            {
                return AgShareFieldIdentity.Read(_directory, () =>
                    AgShareFieldIdentity.IsCurrent(_directory, Field.AgShareId, _revision) &&
                    SavedFieldReader.ReadStable(Path.Combine(_directory, "Field.txt"), 65536).Text == _originFile);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public static class SavedFieldReader
    {
        internal sealed class SavedFile
        {
            public string Text { get; }
            public DateTime ModifiedAtUtc { get; }
            public SavedFile(string text, DateTime modifiedAtUtc) { Text = text; ModifiedAtUtc = modifiedAtUtc; }
        }

        private sealed class ReadFailure : IOException
        {
            public string Reason { get; }
            public ReadFailure(string reason) : base(reason) { Reason = reason; }
        }

        public static PreparedFarmField Read(FarmFieldCapture capture, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid id = Guid.Empty;
            long revision = 0;
            string originFile = null;
            AgShareFieldIdentity.Read(capture.Directory, () =>
            {
                id = AgShareFieldIdentity.GetOrCreate(capture.Directory);
                revision = AgShareFieldIdentity.Revision(capture.Directory);
                originFile = ReadStable(Path.Combine(capture.Directory, "Field.txt"), 65536).Text;
                return true;
            });

            DateTime readAtUtc = DateTime.UtcNow;
            var plane = new LocalPlane(capture.Origin, new SharedFieldProperties { DriftCompensation = capture.Drift });
            FarmCoverage coverage;
            FarmBoundary boundary = null;
            if (!OriginMatches(originFile, capture.Origin))
                coverage = FarmCoverage.Unavailable("ORIGIN_CHANGED", readAtUtc);
            else
            {
                coverage = ReadCoverage(capture.Directory, plane, readAtUtc, cancellationToken);
                boundary = ReadBoundary(capture.Directory, plane, cancellationToken);
            }
            var field = new FarmField(id, capture.Name,
                new[] { capture.Origin.Longitude, capture.Origin.Latitude }, boundary);
            var prepared = new PreparedFarmField(field, coverage, capture.Directory, revision, originFile);
            if (!prepared.IsCurrent()) throw new OperationCanceledException("Field identity or origin changed while reading.");
            return prepared;
        }

        public static bool OriginMatches(string text, Wgs84 origin)
        {
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.Trim().Equals("StartFix", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = reader.ReadLine()?.Split(',');
                    if (parts == null || parts.Length != 2) return false;
                    return TryNumber(parts[0], out double latitude) && TryNumber(parts[1], out double longitude) &&
                        Math.Abs(latitude - origin.Latitude) < 1e-9 && Math.Abs(longitude - origin.Longitude) < 1e-9;
                }
            }
            return false;
        }

        private static FarmCoverage ReadCoverage(string directory, LocalPlane plane, DateTime readAtUtc, CancellationToken cancellationToken)
        {
            try
            {
                var saved = ReadStable(Path.Combine(directory, "Sections.txt"), 32 * 1024 * 1024);
                return ParseCoverage(saved.Text, saved.ModifiedAtUtc, readAtUtc, plane, cancellationToken);
            }
            catch (ReadFailure ex) { return FarmCoverage.Unavailable(ex.Reason, readAtUtc); }
            catch (FileNotFoundException) { return FarmCoverage.Unavailable("FILE_MISSING", readAtUtc); }
            catch (DirectoryNotFoundException) { return FarmCoverage.Unavailable("FILE_MISSING", readAtUtc); }
            catch (IOException) { return FarmCoverage.Unavailable("FILE_UNREADABLE", readAtUtc); }
            catch (UnauthorizedAccessException) { return FarmCoverage.Unavailable("FILE_UNREADABLE", readAtUtc); }
        }

        public static FarmCoverage ParseCoverage(string text, DateTime modifiedAtUtc, DateTime readAtUtc,
            LocalPlane plane, CancellationToken cancellationToken)
        {
            var strips = new List<double[][]>();
            int totalVertices = 0;
            var lines = Lines(text);
            int cursor = 0;
            while (cursor < lines.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[cursor++];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 5 || count % 2 != 1)
                    return FarmCoverage.Unavailable("INVALID_DATA", readAtUtc);
                if (count - 1 > FarmTelemetryJson.MaxCoverageVertices - totalVertices || strips.Count >= FarmTelemetryJson.MaxStrips)
                    return FarmCoverage.Unavailable("LIMIT_EXCEEDED", readAtUtc);
                if (cursor + count > lines.Length || (cursor + count == lines.Length && !EndsInNewline(text)))
                    return strips.Count == 0 ? FarmCoverage.Unavailable("INCOMPLETE_READ", readAtUtc)
                        : FarmCoverage.Ready(readAtUtc, modifiedAtUtc, true, strips.ToArray());
                var strip = new double[count - 1][];
                for (int i = 0; i < count; i++)
                {
                    if (!TryRow(lines[cursor++], out double easting, out double northing))
                        return FarmCoverage.Unavailable("INVALID_DATA", readAtUtc);
                    if (i == 0) continue;
                    var point = plane.ConvertGeoCoordToWgs84(new GeoCoord(northing, easting));
                    if (!point.IsValid) return FarmCoverage.Unavailable("INVALID_DATA", readAtUtc);
                    strip[i - 1] = new[] { point.Longitude, point.Latitude };
                }
                totalVertices += strip.Length;
                strips.Add(strip);
            }
            return FarmCoverage.Ready(readAtUtc, modifiedAtUtc, false, strips.ToArray());
        }

        private static FarmBoundary ReadBoundary(string directory, LocalPlane plane, CancellationToken cancellationToken)
        {
            try
            {
                return ParseBoundary(ReadStable(Path.Combine(directory, "Boundary.txt"), 4 * 1024 * 1024).Text, plane, cancellationToken);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static FarmBoundary ParseBoundary(string text, LocalPlane plane, CancellationToken cancellationToken)
        {
            if (!EndsInNewline(text)) return null;
            var lines = Lines(text);
            var rings = new List<double[][]>();
            int cursor = 0;
            int vertices = 0;
            if (lines.Length > 0 && lines[0].Trim() == "$Boundary") cursor++;
            while (cursor < lines.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(lines[cursor])) { cursor++; continue; }
                for (int flag = 0; flag < 2 && cursor < lines.Length && bool.TryParse(lines[cursor], out _); flag++) cursor++;
                if (cursor >= lines.Length || !int.TryParse(lines[cursor++], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 3)
                    return null;
                if (count > FarmTelemetryJson.MaxBoundaryVertices - vertices || cursor + count > lines.Length) return null;
                var ring = new List<double[]>();
                for (int i = 0; i < count; i++)
                {
                    if (!TryRow(lines[cursor++], out double easting, out double northing)) return null;
                    var point = plane.ConvertGeoCoordToWgs84(new GeoCoord(northing, easting));
                    if (!point.IsValid) return null;
                    ring.Add(new[] { point.Longitude, point.Latitude });
                }
                if (ring.Select(p => Tuple.Create(p[0], p[1])).Distinct().Count() < 3) return null;
                if (!ring[0].SequenceEqual(ring[ring.Count - 1])) ring.Add(ring[0]);
                vertices += ring.Count;
                if (vertices > FarmTelemetryJson.MaxBoundaryVertices) return null;
                rings.Add(ring.ToArray());
            }
            var coordinates = rings.ToArray();
            return FarmBoundaryValidator.IsValid(coordinates, cancellationToken) ? new FarmBoundary(coordinates) : null;
        }

        internal static SavedFile ReadStable(string path, int maxBytes)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var before = new FileInfo(path);
                if (!before.Exists) throw new FileNotFoundException(path);
                long length = before.Length;
                DateTime modified = before.LastWriteTimeUtc;
                if (length > maxBytes) throw new ReadFailure("LIMIT_EXCEEDED");
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var bytes = new byte[(int)length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                    if (offset != bytes.Length || stream.ReadByte() != -1) continue;
                    text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                }
                var after = new FileInfo(path);
                if (after.Exists && after.Length == length && after.LastWriteTimeUtc == modified)
                    return new SavedFile(text, modified);
                Thread.Sleep(25);
            }
            throw new ReadFailure("INCOMPLETE_READ");
        }

        private static string[] Lines(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return EndsInNewline(text) ? lines.Take(lines.Length - 1).ToArray() : lines;
        }

        private static bool EndsInNewline(string text) => text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);

        private static bool TryRow(string row, out double easting, out double northing)
        {
            easting = northing = 0;
            var parts = row.Split(',');
            return parts.Length == 3 && TryNumber(parts[0], out easting) && TryNumber(parts[1], out northing) && TryNumber(parts[2], out _);
        }

        private static bool TryNumber(string text, out double number)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !double.IsNaN(number) && !double.IsInfinity(number);
        }
    }
}
