using System;
using System.Collections.Concurrent;
using System.IO;

namespace AgOpenGPS.Core.FarmTelemetry
{
    public static class AgShareFieldIdentity
    {
        private sealed class Entry
        {
            public readonly object Gate = new object();
            public long Revision;
        }

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static Entry For(string directory) => Entries.GetOrAdd(Path.GetFullPath(directory), _ => new Entry());

        public static T Read<T>(string directory, Func<T> read)
        {
            lock (For(directory).Gate) return read();
        }

        public static void Write(string directory, Action write)
        {
            var entry = For(directory);
            lock (entry.Gate)
            {
                entry.Revision++;
                write();
            }
        }

        public static long Revision(string directory)
        {
            var entry = For(directory);
            lock (entry.Gate) return entry.Revision;
        }

        public static Guid GetOrCreate(string directory)
        {
            return Read(directory, () =>
            {
                string path = Path.Combine(directory, "agshare.txt");
                if (File.Exists(path)) return Guid.Parse(File.ReadAllText(path).Trim());
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
                Guid id = Guid.NewGuid();
                Replace(directory, id);
                return id;
            });
        }

        public static bool IsCurrent(string directory, Guid id, long revision)
        {
            return Read(directory, () => Revision(directory) == revision &&
                Guid.TryParse(File.ReadAllText(Path.Combine(directory, "agshare.txt")).Trim(), out Guid current) && current == id);
        }

        public static void Replace(string directory, Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("Field identity must not be empty.", nameof(id));
            Write(directory, () =>
            {
                string path = Path.Combine(directory, "agshare.txt");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, id.ToString());
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            });
        }
    }
}
