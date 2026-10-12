using System;
using System.IO;
using TicTack;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Corpus
{
    public class CorpusCompressionConfig
    {
        public int Level { get; set; }

        public CorpusCompressionConfig()
        {
            Level = 3;
        }
    }

    public class CorpusLoggingConfig
    {
        public string? Level { get; set; }
        public string? Path { get; set; }
        public bool Console { get; set; }

        public CorpusLoggingConfig()
        {
            Level = "info";
            Console = false;
        }
    }

    public class CorpusConfig
    {
        // Chunk packs + manifests live here. Required.
        public string StorePath { get; set; }
        // Chunk-index .db root. Empty = platform default (see StateRoot).
        public string StateDbPath { get; set; }
        public int ChunkMib { get; set; }
        public CorpusCompressionConfig Compression { get; set; }
        public CorpusLoggingConfig Logging { get; set; }

        public CorpusConfig()
        {
            StorePath = string.Empty;
            StateDbPath = string.Empty;
            ChunkMib = 4;
            Compression = new CorpusCompressionConfig();
            Logging = new CorpusLoggingConfig();
        }

        public static CorpusConfig? Load(string path)
        {
            if (!File.Exists(path)) return null;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .Build();

            using (var reader = new StreamReader(path))
                return deserializer.Deserialize<CorpusConfig>(reader);
        }

        public static bool Validate(CorpusConfig cfg, ILogger log)
        {
            var valid = true;
            if (string.IsNullOrEmpty(cfg.StorePath))
            {
                log.Error("store_path is required");
                valid = false;
            }
            if (cfg.ChunkMib < 1 || cfg.ChunkMib > 64)
            {
                log.Error("chunk_mib must be 1..64 (got: " + cfg.ChunkMib + ")");
                valid = false;
            }
            var level = cfg.Compression != null ? cfg.Compression.Level : 3;
            if (level < 1 || level > 22)
            {
                log.Error("compression.level must be 1..22 (got: " + level + ")");
                valid = false;
            }
            return valid;
        }

        // Chunk-index root: explicit config wins, else the platform store.
        // Same shape as TicTack's StateDbRoot, Corpus-owned default dir.
        public static string StateRoot(CorpusConfig cfg)
        {
            if (!string.IsNullOrEmpty(cfg.StateDbPath)) return cfg.StateDbPath;
            return OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corpus")
                : "/var/lib/corpus";
        }
    }
}
