using NetArchTest.Rules;
using SceneGallery.PluginSdk;

namespace SceneGallery.Plugin.BepisDb.Tests;

public sealed class ArchitectureTests
{
    private static readonly System.Reflection.Assembly PluginAssembly = typeof(BepisDbPlugin).Assembly;

    [Fact]
    public void EveryProviderType_HasAnExplicitArchitecturalHome()
    {
        Type[] adapter = [typeof(BepisDbPlugin)];
        Type[] workflow = [typeof(BepisDbRuntime), typeof(BepisDbFetchCoordinator), typeof(BepisDbSessionOwner)];
        Type[] transport = [typeof(IBepisDbFetcher), typeof(CookieHttpFetcher)];
        Type[] storage = [typeof(ArtworkDiskCache), typeof(PluginSettings)];
        Type[] parsingAndMapping =
        [
            typeof(BepisDbFilenameParser), typeof(BepisDbAuthorFolderNameParser),
            typeof(BepisDbCategory), typeof(BepisDbCategoryHelper), typeof(BepisDbResponseParser),
            typeof(BepisDbCardParseResult), typeof(BepisDbCardMapper),
            typeof(BepisDbApiResponse), typeof(BepisDbApiData), typeof(BepisDbCardData),
            typeof(BepisDbUploader), typeof(BepisDbTag), typeof(BepisDbCardMetadata),
        ];
        var expected = adapter.Concat(workflow).Concat(transport).Concat(storage).Concat(parsingAndMapping).ToArray();
        var providerNamespace = typeof(BepisDbPlugin).Namespace!;
        var actual = PluginAssembly.GetTypes().Where(type => !type.IsNested &&
            (type.Namespace == providerNamespace ||
             type.Namespace?.StartsWith(providerNamespace + ".", StringComparison.Ordinal) == true))
            .OrderBy(type => type.FullName).ToArray();

        Assert.Equal(expected.Length, expected.Distinct().Count());
        Assert.Equal(expected.OrderBy(type => type.FullName), actual);
    }

    [Theory]
    [InlineData("^BepisDbPlugin$", "System.Net.Http")]
    [InlineData("^BepisDbPlugin$", "System.Text.Json")]
    [InlineData("^BepisDbPlugin$", "System.IO")]
    [InlineData("^BepisDbFetchCoordinator$", "System.Net.Http")]
    [InlineData("^BepisDbFetchCoordinator$", "SceneGallery.PluginSdk.IPluginHost")]
    [InlineData("^BepisDbFetchCoordinator$", "SceneGallery.Plugin.BepisDb.CookieHttpFetcher")]
    [InlineData("^BepisDbFetchCoordinator$", "SceneGallery.Plugin.BepisDb.BepisDbRuntime")]
    [InlineData("^(CookieHttpFetcher|BepisDbSessionOwner|ArtworkDiskCache|PluginSettings)$", "SceneGallery.Plugin.BepisDb.BepisDbFetchCoordinator")]
    [InlineData("^(CookieHttpFetcher|BepisDbSessionOwner|ArtworkDiskCache|PluginSettings)$", "SceneGallery.Plugin.BepisDb.BepisDbRuntime")]
    [InlineData("^(CookieHttpFetcher|BepisDbSessionOwner|ArtworkDiskCache|PluginSettings)$", "SceneGallery.Plugin.BepisDb.BepisDbPlugin")]
    [InlineData("^(ArtworkDiskCache|PluginSettings)$", "System.Net.Http")]
    [InlineData("^BepisDbResponseParser$", "System.Net.Http")]
    [InlineData("^BepisDbResponseParser$", "System.IO")]
    [InlineData("^BepisDbResponseParser$", "SceneGallery.Plugin.BepisDb.ArtworkDiskCache")]
    [InlineData("^(BepisDbResponseParser|BepisDbFilenameParser|BepisDbAuthorFolderNameParser|BepisDbCategoryHelper)$", "SceneGallery.Plugin.BepisDb.BepisDbRuntime")]
    public void Layers_RespectDependencyDirection(string selectedTypePattern, string forbiddenDependency)
    {
        var selected = Types.InAssembly(PluginAssembly).That().HaveNameMatching(selectedTypePattern);
        Assert.Equal(selectedTypePattern.Count(character => character == '|') + 1, selected.GetTypes().Count());
        var result = selected.ShouldNot().HaveDependencyOn(forbiddenDependency).GetResult();
        Assert.True(result.IsSuccessful, $"{selectedTypePattern} must not depend on {forbiddenDependency}: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void ImplementationAndSourcePackages_DoNotExpandThePublicSdkSurface()
    {
        Assert.Equal([typeof(BepisDbPlugin)], PluginAssembly.GetExportedTypes());
        var expectedCapabilities = new[]
        {
            typeof(IFolderAuthorProvider), typeof(ICardImportProvider), typeof(IImportDestinationProvider),
            typeof(ICookieSetupValidator), typeof(IPluginSettingsProvider), typeof(IDisposable),
        };
        Assert.Equal(expectedCapabilities.SelectMany(type => type.GetInterfaces().Append(type)).Distinct().OrderBy(type => type.FullName),
            typeof(BepisDbPlugin).GetInterfaces().OrderBy(type => type.FullName));
        Assert.NotNull(typeof(BepisDbPlugin).GetConstructor(Type.EmptyTypes));
        Assert.Equal(new[]
        {
            "ApplyCookies", "Dispose", "FetchArtworkInfoAsync", "GetArtworkUrl", "GetAuthorInfoAsync", "GetProfileUrl",
            "GetSettingValue", "HasUsableCookiesAsync", "Initialize", "SetSettingValue", "TryParseArtworkFolderName",
            "TryParseFilename", "TryParseFolderName", "TryParseUrl", "get_CompletionTitleHint", "get_CookieDomain",
            "get_DestinationFolderName", "get_Name", "get_NeedsCookieSetup", "get_ProviderId", "get_Settings",
            "get_SetupUrl", "get_UsesRatingFolders", "get_Version",
        }, typeof(BepisDbPlugin).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly).Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(PluginAssembly.GetTypes().Where(type => type.Namespace == "SceneGallery.PluginCommon"),
            type => Assert.False(type.IsVisible, type.FullName));
        var references = PluginAssembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("SceneGallery.PluginCommon", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.Name is "NetArchTest.Rules" or "xunit.core");
        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("KoikatsuSceneGallery", StringComparison.Ordinal)
            || reference.Name.StartsWith("SceneGallery.Plugin.", StringComparison.Ordinal));
        Assert.Equal(new Version(1, 0, 0, 0), Assert.Single(references, reference => reference.Name == "SceneGallery.PluginSdk").Version);
    }

    [Fact]
    public void HttpTransport_RemainsInjectableForOfflineTests()
        => Assert.Contains(typeof(CookieHttpFetcher).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(HttpMessageHandler)));

    [Theory]
    [InlineData(nameof(ArchitectureFixtures.Construction), false)]
    [InlineData(nameof(ArchitectureFixtures.StaticCall), false)]
    [InlineData(nameof(ArchitectureFixtures.AsyncCall), false)]
    [InlineData(nameof(ArchitectureFixtures.Pure), true)]
    public void DependencyGuard_InspectsConstructionStaticCallsAndAsyncBodies(string name, bool valid)
    {
        var selected = Types.InAssembly(typeof(ArchitectureTests).Assembly).That().HaveName(name);
        Assert.Single(selected.GetTypes());
        Assert.Equal(valid, selected.ShouldNot().HaveDependencyOn(typeof(ArchitectureFixtures.Dependency).FullName!).GetResult().IsSuccessful);
    }
}
