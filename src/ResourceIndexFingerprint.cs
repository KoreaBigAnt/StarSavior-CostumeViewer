using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StarSaviorCostumeViewer
{
    internal static class ResourceIndexFingerprint
    {
        internal static string Build(string catalog, string source, IEnumerable<string> bundles)
        {
            var data = new StringBuilder("installed-index-v1\n").Append(Path.GetFullPath(source)).Append('\n');
            Append(data, catalog);
            foreach (string path in bundles.OrderBy(path => path, StringComparer.Ordinal)) Append(data, path);
            using (var digest = SHA256.Create())
                return BitConverter.ToString(digest.ComputeHash(Encoding.UTF8.GetBytes(data.ToString()))).Replace("-", "");
        }
        private static void Append(StringBuilder data, string path)
        {
            var file = new FileInfo(path);
            data.Append(file.Name).Append('|').Append(file.Length).Append('|').Append(File.GetLastWriteTime(path).Ticks).Append('\n');
        }
    }
}
