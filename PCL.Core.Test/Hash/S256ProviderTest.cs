using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;

namespace PCL.Core.Test.Hash;

[TestClass]
public class S256ProviderTest
{
    // RFC 7636 Appendix B 标准测试向量
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [TestMethod]
    public void ComputeCodeChallenge_MatchesRfc7636Vector()
    {
        Assert.AreEqual(CodeChallenge, S256Provider.Instance.ComputeCodeChallenge(CodeVerifier));
    }

    [TestMethod]
    public void ComputeHash_ReturnsRawSha256Digest()
    {
        var digest = S256Provider.Instance.ComputeHash(CodeVerifier);

        Assert.AreEqual(32, digest.Length, "SHA-256 摘要应为 32 字节");
        Assert.AreEqual(64, S256Provider.Instance.Length, "十六进制表示长度应为 64");
        Assert.AreEqual(CodeChallenge, digest.FromBytesToB64UrlSafe().TrimEnd('='));
    }
}
