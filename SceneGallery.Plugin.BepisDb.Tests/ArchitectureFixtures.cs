namespace SceneGallery.Plugin.BepisDb.Tests.ArchitectureFixtures;

internal sealed class Dependency { internal static int Read() => Environment.TickCount; }
internal sealed class Construction { internal object Run() => new Dependency(); }
internal sealed class StaticCall { internal int Run() => Dependency.Read(); }
internal sealed class AsyncCall { internal async Task<int> Run() { await Task.Yield(); return Dependency.Read(); } }
internal sealed class Pure { internal int Run(int value) => value + 1; }
