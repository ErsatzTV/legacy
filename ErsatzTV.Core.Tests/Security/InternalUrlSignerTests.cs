using ErsatzTV.Core.Security;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Security;

[TestFixture]
public class InternalUrlSignerTests
{
    [Test]
    public void Verify_Alpha_Exp_Should_Not_Throw()
    {
        bool actual = InternalUrlSigner.Verify("abc", "sig");
        actual.ShouldBeFalse();
    }

    [Test]
    public void Verify_Long_Exp_Should_Not_Throw()
    {
        bool actual = InternalUrlSigner.Verify("99999999999999", "sig");
        actual.ShouldBeFalse();
    }
}
