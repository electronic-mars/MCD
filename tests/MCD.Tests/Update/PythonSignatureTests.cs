using System.Text.Json;
using Mcd.Core.Update;
using Shouldly;
using Xunit;

namespace Mcd.Tests.Update;

public sealed class PythonSignatureTests
{
    // One-off cross-check of tools/sign_release.py against this program's own
    // verifier: runs only when MCD_SIGNED_DIR points at a folder holding an
    // installer and the latest.json made for it.
    [Fact]
    public void WhatTheSigningScriptMakesIsAcceptedByTheProgram()
    {
        string? dir = Environment.GetEnvironmentVariable("MCD_SIGNED_DIR");

        if (dir is null)
        {
            return;
        }

        using var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "latest.json")));
        string sig = feed.RootElement.GetProperty("platforms").GetProperty("windows-x86_64").GetProperty("signature").GetString()!;
        string file = Directory.GetFiles(dir, "*-setup.exe").Single();

        Updater.Verify(file, Convert.FromHexString(sig)).ShouldBeTrue();
    }
}
