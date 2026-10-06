using System.Security.Cryptography;
using Mcd.Core.Update;
using Shouldly;
using Xunit;

namespace Mcd.Tests.Update;

public sealed class UpdaterTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "Version 0.9.9", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.0.9", "0.1.0", false)]
    public void VersionsAreComparedAsNumbers(string offered, string current, bool newer) =>
        Updater.IsNewer(offered, current).ShouldBe(newer);

    [Fact]
    public void AFileSignedByTheKeyPassesAndAChangedOneDoesNot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters p = key.ExportParameters(false);
        byte[] pub = [.. p.Q.X!, .. p.Q.Y!];

        string file = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
            byte[] sig = key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            Updater.Verify(file, sig, pub).ShouldBeTrue();

            File.WriteAllBytes(file, [1, 2, 3, 4, 6]);
            Updater.Verify(file, sig, pub).ShouldBeFalse();
            Updater.Verify(file, sig, new byte[64]).ShouldBeFalse();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
