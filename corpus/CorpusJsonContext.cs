using System.Text.Json.Serialization;

namespace Corpus
{
    // NativeAOT JSON: reflection-free Serialize/Deserialize for every
    // persisted DTO. Call sites use CorpusJsonContext.Default.<Type> so the
    // trimmer/AOT compiler sees the concrete type (no IL2026/IL3050).
    [JsonSerializable(typeof(CompactStateRecord))]
    [JsonSerializable(typeof(DifferencingParentRecord))]
    [JsonSerializable(typeof(DiskLayout))]
    [JsonSerializable(typeof(SnapshotManifest))]
    [JsonSerializable(typeof(Store.LandingFingerprint))]
    [JsonSerializable(typeof(WimMount.Marker))]
    [JsonSerializable(typeof(RotationState))]
    [JsonSerializable(typeof(MediaInfo))]
    internal partial class CorpusJsonContext : JsonSerializerContext
    {
    }
}
