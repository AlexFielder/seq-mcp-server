using SeqMcpServer.Services;

namespace SeqMcpServer.Tests;

public class EnvironmentCredentialStoreKeyCtorTests
{
    private static void WithEnv(Dictionary<string, string?> vars, Action body)
    {
        var saved = vars.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
            body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void KeyCtor_GetApiKeyDefault_ReturnsGivenKey_WhenEnvVarUnset()
    {
        WithEnv(new() { ["SEQ_API_KEY"] = null }, () =>
        {
            var store = new EnvironmentCredentialStore("fake-given-key");
            Assert.Equal("fake-given-key", store.GetApiKey("default"));
        });
    }

    [Fact]
    public void KeyCtor_UnknownWorkspace_FallsBackToGivenKey()
    {
        WithEnv(new() { ["SEQ_API_KEY"] = null, ["SEQ_API_KEY_NOSUCH"] = null }, () =>
        {
            var store = new EnvironmentCredentialStore("fake-given-key");
            Assert.Equal("fake-given-key", store.GetApiKey("nosuch"));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void KeyCtor_NullEmptyOrWhitespace_ThrowsArgumentException(string? key)
    {
        Assert.ThrowsAny<ArgumentException>(() => new EnvironmentCredentialStore(key!));
    }

    [Fact]
    public void KeyCtor_WorkspaceSpecificEnvVar_OverridesForNamedWorkspace()
    {
        WithEnv(new() { ["SEQ_API_KEY"] = null, ["SEQ_API_KEY_PRODUCTION"] = "fake-prod-key" }, () =>
        {
            var store = new EnvironmentCredentialStore("fake-given-key");
            Assert.Equal("fake-prod-key", store.GetApiKey("production"));
            Assert.Equal("fake-given-key", store.GetApiKey("default"));
        });
    }

    [Fact]
    public void ParameterlessCtor_StillReadsEnvVar()
    {
        WithEnv(new() { ["SEQ_API_KEY"] = "fake-env-key" }, () =>
        {
            var store = new EnvironmentCredentialStore();
            Assert.Equal("fake-env-key", store.GetApiKey("default"));
        });
    }

    [Fact]
    public void ParameterlessCtor_StillThrowsInvalidOperation_WhenEnvVarUnset()
    {
        WithEnv(new() { ["SEQ_API_KEY"] = null }, () =>
        {
            Assert.Throws<InvalidOperationException>(() => new EnvironmentCredentialStore());
        });
    }
}
