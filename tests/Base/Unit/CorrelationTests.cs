namespace Croicu.Desk.Tools.Base.Tests.Unit;

[TestClass]
public sealed class CorrelationTests
{
    [TestMethod]
    public void Current_CalledTwiceInSameContext_ReturnsSameValue()
    {
        Assert.AreEqual(Correlation.Current, Correlation.Current);
    }

    [TestMethod]
    public async Task Current_FlowsAcrossAwait()
    {
        var before = Correlation.Current;

        await Task.Yield();

        Assert.AreEqual(before, Correlation.Current);
    }

    [TestMethod]
    public void Current_IsAnEightCharacterHexString()
    {
        var id = Correlation.Current;

        Assert.HasCount(8, id);
        Assert.IsTrue(id.All(Uri.IsHexDigit));
    }
}
