using System;
using System.IO;
using System.Text.Json;

namespace Corpus
{
    // Recovery-media layout contract (§1): the ADK floor, the fixed payload
    // files StagePayload must produce (publish closure + optional store copy
    // vary by build and are asserted by presence, not by name), and the
    // media-info record baked into every stick.
    public static class MediaLayout
    {
        public const int AdksMinBuild = 26100;

        // Media-root-relative, '/' separators; AssertLayout normalizes.
        public static readonly string[] ExpectedPayloadFiles =
        [
            "Corpus/config_corpus.yaml",
            "Corpus/restore.cmd",
            "Corpus/README-restore.txt",
            "Corpus/media-info.json",
        ];

        public static readonly string[] ExpectedStoreFiles =
        [
            "Corpus/store/id.key",
            "Corpus/store/corpus-chunks.db",
        ];

        public static void AssertLayout(string stageDir, bool hasStore)
        {
            ArgumentNullException.ThrowIfNull(stageDir);
            foreach (var rel in ExpectedPayloadFiles)
                Require(stageDir, rel, "payload file");
            if (hasStore)
            {
                foreach (var rel in ExpectedStoreFiles)
                    Require(stageDir, rel, "store copy");
            }
        }

        private static void Require(string stageDir, string rel, string what)
        {
            var full = Path.Combine(stageDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                throw new InvalidDataException($"media staging {stageDir} is missing {what} {rel}");
        }
    }

    public sealed record MediaInfo(int AdkBuild, string CorpusVersion, string[] PePackages, string[] Drivers, int[] StoreGens, string BuiltUtc)
    {
        public string ToJson() => JsonSerializer.Serialize(this);

        public static MediaInfo FromJson(string json) =>
            JsonSerializer.Deserialize<MediaInfo>(json) ?? throw new InvalidDataException("media-info.json is empty.");

        public void Save(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            File.WriteAllText(path, ToJson());
        }

        public static MediaInfo Load(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return FromJson(File.ReadAllText(path));
        }
    }
}
