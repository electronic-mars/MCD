// Signs the installer and writes the description the program reads.
//
//     dotnet run tools/sign-release/SignRelease.cs -- <installer> <version> <download-url> [notes-file]
//
// Runs on the build server, after the installer is built and before the
// release is created. The private key arrives through the environment from a
// repository secret and is never written to disk.
//
// Signed with what .NET itself carries and nothing fetched for the occasion.
// The key once sat in the environment of a step that installed a signing
// package from the package index, unpinned, and imported it: whoever controlled
// that package's next release would have held the key every installed copy
// trusts.
//
// Writes latest.json next to the installer. That file goes onto the release
// under exactly that name: the program asks for it by a fixed address that
// always points at the newest release.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: SignRelease <installer> <version> <download-url> [notes-file]");
    return 2;
}

string installer = Path.GetFullPath(args[0]);
string version = args[1].TrimStart('v');
string url = args[2];
string notes = args.Length > 3 ? File.ReadAllText(args[3]).Trim() : string.Empty;

// The public half the program carries, from the repository root it is run in.
// MCD_RELEASE_KEY_SOURCE points elsewhere for a test with a key of its own.
string keySource = Environment.GetEnvironmentVariable("MCD_RELEASE_KEY_SOURCE")
    ?? Path.Combine("src", "MCD.Core", "Update", "ReleaseKey.cs");

string pem = (Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY") ?? string.Empty).Trim();

if (pem.Length == 0)
{
    Console.Error.WriteLine("UPDATE_SIGNING_KEY is not set - nothing would be signed, and a release nobody can install is worse than no release");
    return 1;
}

using ECDsa key = ECDsa.Create();
key.ImportFromPem(pem);

byte[] payload = File.ReadAllBytes(installer);

// The two halves laid end to end (IEEE P1363), which is what the program
// checks, not the DER wrapping.
byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

// Checked against the key the program actually carries. A private key that has
// drifted from the public half compiled into the build produces a release
// nobody can install, and nobody finds out until the first person presses the
// update button.
Match found = Regex.Match(File.ReadAllText(keySource), "Convert\\.FromHexString\\(\\s*\"([0-9a-fA-F]{128})\"");

if (!found.Success)
{
    Console.Error.WriteLine($"no release key in {keySource}");
    return 1;
}

ECPoint q = key.ExportParameters(includePrivateParameters: false).Q;

if (!Convert.FromHexString(found.Groups[1].Value).AsSpan().SequenceEqual([.. q.X!, .. q.Y!]))
{
    Console.Error.WriteLine($"the private key in UPDATE_SIGNING_KEY is not the pair of the key in {Path.GetFileName(keySource)} - releasing this would ship an update nobody can install.");
    return 1;
}

string output = Path.Combine(Path.GetDirectoryName(installer)!, "latest.json");

using (var stream = File.Create(output))
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteString("version", version);
    json.WriteString("notes", notes);
    json.WriteStartObject("platforms");
    json.WriteStartObject("windows-x86_64");
    json.WriteString("url", url);
    json.WriteString("signature", Convert.ToHexStringLower(signature));
    json.WriteEndObject();
    json.WriteEndObject();
    json.WriteEndObject();
}

Console.WriteLine($"signed {Path.GetFileName(installer)} ({payload.Length} bytes) -> latest.json");
return 0;
