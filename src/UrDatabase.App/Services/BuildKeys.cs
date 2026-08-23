using System.Linq;
using System.Reflection;

namespace UrDatabase.Services
{
    /// <summary>
    /// API keys baked in at build time via MSBuild properties, so official builds work with no
    /// user setup. Both default to empty: a plain <c>dotnet build</c> with no keys still compiles,
    /// runs and passes tests, which is what keeps contributors from needing a key.
    /// </summary>
    public static class BuildKeys
    {
        public const string TmdbMetadataName = "TmdbApiKey";
        public const string OmdbMetadataName = "OmdbApiKey";
        public const string UrActorMetadataName = "UrActorApiKey";

        public static string Tmdb => Read(typeof(BuildKeys).Assembly, TmdbMetadataName);

        public static string Omdb => Read(typeof(BuildKeys).Assembly, OmdbMetadataName);

        /// <summary>The UrActor key, for Academy Award nominations.</summary>
        public static string UrActor => Read(typeof(BuildKeys).Assembly, UrActorMetadataName);

        /// <summary>Reads a compiled-in value. Returns empty when the build supplied none.</summary>
        internal static string Read(Assembly assembly, string name)
        {
            var value = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == name)
                ?.Value;

            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }
    }
}
