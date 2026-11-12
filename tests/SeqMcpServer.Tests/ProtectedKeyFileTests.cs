using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SeqMcpServer.Services;

namespace SeqMcpServer.Tests;

public sealed class ProtectedKeyFileTests : IDisposable
{
    private const string FakeKey = "FAKE-key_0123456789abcdef!@#";
    private readonly List<string> _files = new();

    private string NewPath()
    {
        var p = Path.Combine(Path.GetTempPath(), "pkf-test-" + Guid.NewGuid().ToString("N") + ".xml");
        _files.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* ignore */ }
        }
    }

    private static byte[] ProtectBytes(string plaintext) =>
        ProtectedData.Protect(Encoding.Unicode.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);

    private static string Protect(string plaintext) =>
        Convert.ToHexString(ProtectBytes(plaintext)).ToLowerInvariant();

    private static string Clixml(string ssContent) =>
        "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
        "<SS>" + ssContent + "</SS></Objs>";

    private string WriteFile(string content)
    {
        var p = NewPath();
        File.WriteAllText(p, content);
        return p;
    }

    private static byte[] Tampered()
    {
        var blob = ProtectBytes(FakeKey);
        blob[^1] ^= 0xFF;
        blob[blob.Length / 2] ^= 0xFF;
        return blob;
    }

    [Fact]
    public void Read_ReturnsExactPlaintext_ForHandBuiltClixml()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = WriteFile(Clixml(Protect(FakeKey)));

        Assert.Equal(FakeKey, ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_PreservesLeadingAndTrailingWhitespaceOfKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string key = "  spaced key \t";
        var path = WriteFile(Clixml(Protect(key)));

        Assert.Equal(key, ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_HandlesNonAsciiPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string key = "kéy-ü€-fake";
        var path = WriteFile(Clixml(Protect(key)));

        Assert.Equal(key, ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_ToleratesWhitespaceAroundHex()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = WriteFile(Clixml("\n  " + Protect(FakeKey) + "\n  "));

        Assert.Equal(FakeKey, ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_ExpandsEnvironmentVariablesInPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = WriteFile(Clixml(Protect(FakeKey)));
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        const string varName = "PKF_TEST_DIR";
        Environment.SetEnvironmentVariable(varName, dir);
        try
        {
            Assert.Equal(FakeKey, ProtectedKeyFile.Read("%" + varName + "%\\" + name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        var path = NewPath();
        Assert.Throws<FileNotFoundException>(() => ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_NotXml_ThrowsInvalidData()
    {
        var path = WriteFile("this is not xml at all");
        Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_NoSsElement_ThrowsInvalidData()
    {
        var path = WriteFile("<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S>hello</S></Objs>");
        Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_EmptySs_ThrowsInvalidData()
    {
        var path = WriteFile(Clixml(""));
        Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_WhitespaceOnlySs_ThrowsInvalidData()
    {
        var path = WriteFile(Clixml("   \n "));
        Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
    }

    [Theory]
    [InlineData("zzzz-not-hex")]
    [InlineData("0123456789abcdeg")]
    [InlineData("abc")]
    public void Read_InvalidHex_ThrowsInvalidData(string content)
    {
        var path = WriteFile(Clixml(content));
        Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_ValidHexButNotDpapi_ThrowsInvalidDataWrappingCryptoError()
    {
        if (!OperatingSystem.IsWindows()) return;
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(128)).ToLowerInvariant();
        var path = WriteFile(Clixml(random));

        var ex = Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void Read_TamperedDpapiBlob_ThrowsInvalidData()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = WriteFile(Clixml(Convert.ToHexString(Tampered())));

        Assert.Throws<InvalidDataException>(() => ProtectedKeyFile.Read(path));
    }

    [Fact]
    public void Read_ErrorMessages_NeverContainPlaintextKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cases = new[]
        {
            WriteFile(Clixml(Convert.ToHexString(Tampered()))),
            WriteFile(Clixml(Protect(FakeKey).Substring(0, 7) + "zz")),
            WriteFile(FakeKey),
        };

        foreach (var path in cases)
        {
            var ex = Assert.ThrowsAny<Exception>(() => ProtectedKeyFile.Read(path));
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                Assert.DoesNotContain(FakeKey, e.Message);
                Assert.DoesNotContain(FakeKey.Substring(0, 8), e.Message);
            }
        }
    }

    [Fact]
    public void Read_FileProducedByRealPowerShellExportClixml_ReturnsPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pwsh = FindPwsh();
        if (pwsh is null) return; // skip cleanly: pwsh not installed

        var path = NewPath();
        var script = "ConvertTo-SecureString '" + FakeKey.Replace("'", "''") +
                     "' -AsPlainText -Force | Export-Clixml -LiteralPath '" + path.Replace("'", "''") + "'";
        var psi = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.ReadToEnd();
        Assert.True(proc.WaitForExit(60_000), "pwsh timed out");
        Assert.True(proc.ExitCode == 0, "pwsh failed: " + stderrTask.Result);
        Assert.True(File.Exists(path));

        Assert.Equal(FakeKey, ProtectedKeyFile.Read(path));
    }

    private static string? FindPwsh()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* bad PATH entry */ }
        }
        return null;
    }
}
