using System;
using System.IO;

namespace Parsek.Tests.LogValidation
{
    internal static class TestFixtureLoader
    {
        public static string GetFixturePath(string fileName)
        {
            string repoRoot = ResolveRepoRoot();
            string fixturePath = Path.Combine(repoRoot, "Source", "Parsek.Tests", "Fixtures", "KspLog", fileName);
            if (!File.Exists(fixturePath))
                throw new FileNotFoundException($"Fixture file not found: {fixturePath}");
            return fixturePath;
        }

        private static string ResolveRepoRoot()
        {
            DirectoryInfo current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                string markerPath = Path.Combine(current.FullName, "Source", "Parsek.Tests", "Parsek.Tests.csproj");
                if (File.Exists(markerPath))
                    return current.FullName;
                current = current.Parent;
            }

            throw new InvalidOperationException(
                $"Unable to resolve repository root from base directory '{AppContext.BaseDirectory}'.");
        }
    }
}
