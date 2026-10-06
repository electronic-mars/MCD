namespace Mcd.Core.Update;

/// <summary>
/// The public half of the release key. The private half never leaves the
/// repository's secrets. Replacing this is what changes who may publish an
/// update; tools/make_update_key.py writes it.
/// </summary>
internal static class ReleaseKey
{
    public static byte[] Public { get; } = Convert.FromHexString(
        "a32953f7c6d5c2a9ea222b38a52b68c453ac2185f12c6251c7fcc0dc74572cf00b15452dc24c8a31c0b1d39eff1a9100fec5ac908a88fdbb9dfdeb35c9f217b4");
}
