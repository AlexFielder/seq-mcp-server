using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SeqMcpServer.Services;

/// <summary>
/// Reads an API key saved by PowerShell as a DPAPI-protected SecureString:
/// <c>ConvertTo-SecureString $key -AsPlainText -Force | Export-Clixml key.xml</c>.
/// Only the same Windows user on the same machine can decrypt it, so the key never sits in
/// plain text in an MCP client's configuration file.
/// </summary>
public static class ProtectedKeyFile
{
    private static readonly XNamespace PsNs = "http://schemas.microsoft.com/powershell/2004/04";

    public static string Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI-protected key files can only be read on Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Seq API key file not found.", fullPath);
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(fullPath);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"'{fullPath}' is not a CLIXML file.", ex);
        }

        var hex = (doc.Descendants(PsNs + "SS").FirstOrDefault() ?? doc.Descendants("SS").FirstOrDefault())?.Value.Trim();
        if (string.IsNullOrEmpty(hex))
        {
            throw new InvalidDataException($"'{fullPath}' holds no SecureString (<SS>) element.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"The SecureString in '{fullPath}' is not valid hex.", ex);
        }

        byte[] plainBytes;
        try
        {
            plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                $"'{fullPath}' could not be decrypted. It must be written by Export-Clixml as this Windows user on this machine.", ex);
        }

        try
        {
            return Encoding.Unicode.GetString(plainBytes);
        }
        finally
        {
            Array.Clear(plainBytes);
        }
    }
}
