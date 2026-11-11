using System;
using System.Reflection;

namespace SubtitleForge.Services
{
    /// <summary>
    /// Build stamp injected by the csproj (GITHUB_SHA on CI, "local" elsewhere).
    /// </summary>
    internal static class BuildInfo
    {
        private static string? sha;

        public static string Sha
        {
            get
            {
                if (sha == null)
                {
                    sha = Resolve();
                }

                return sha;
            }
        }

        public static string ShortSha
        {
            get { return Sha.Length > 7 ? Sha.Substring(0, 7) : Sha; }
        }

        private static string Resolve()
        {
            try
            {
                foreach (AssemblyMetadataAttribute attribute in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (attribute.Key == "BuildSha" && !string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        return attribute.Value!;
                    }
                }
            }
            catch (Exception)
            {
                return "local";
            }

            return "local";
        }
    }
}
