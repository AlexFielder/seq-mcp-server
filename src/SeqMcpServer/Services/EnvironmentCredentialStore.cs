namespace SeqMcpServer.Services;

public sealed class EnvironmentCredentialStore : ICredentialStore
{
    private readonly string? _defaultApiKey;

    public EnvironmentCredentialStore(string defaultApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultApiKey);
        _defaultApiKey = defaultApiKey;
    }

    public EnvironmentCredentialStore()
    {
        _defaultApiKey = Environment.GetEnvironmentVariable("SEQ_API_KEY");
        
        if (string.IsNullOrEmpty(_defaultApiKey))
        {
            throw new InvalidOperationException(
                "SEQ_API_KEY environment variable is not set. " +
                "Set SEQ_API_KEY, or point the profile's ApiKeyFile (or SEQ_API_KEY_FILE) at a DPAPI key file.");
        }
    }

    public string GetApiKey(string workspace)
    {
        // For MCP servers, we typically use a single API key
        // If workspace-specific keys are needed, they can be set as SEQ_API_KEY_<WORKSPACE>
        if (!string.IsNullOrEmpty(workspace) && workspace != "default")
        {
            var workspaceKey = Environment.GetEnvironmentVariable($"SEQ_API_KEY_{workspace.ToUpperInvariant()}");
            if (!string.IsNullOrEmpty(workspaceKey))
                return workspaceKey;
        }
        
        return _defaultApiKey ?? throw new InvalidOperationException("API key is null");
    }

    public void Reload()
    {
        // Environment variables don't need reloading
    }
}