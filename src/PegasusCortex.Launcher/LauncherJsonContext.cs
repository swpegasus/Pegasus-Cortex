using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Launcher;

[JsonSerializable(typeof(CortexStableLauncher.VersionSelector))]
internal sealed partial class LauncherJsonContext : JsonSerializerContext;
