using System;
using System.Threading.Tasks;
using NUnit.Framework;

public class ErrorHandlerTests
{
    [Test]
    public void SafeExecute_ReturnsValue_WhenNoException()
    {
        var result = ErrorHandler.SafeExecute(() => 42, "ctx", _ => { });
        Assert.AreEqual(42, result);
    }

    [Test]
    public void SafeExecute_ReturnsDefault_AndInvokesLog_WhenException()
    {
        string logged = null;
        var result = ErrorHandler.SafeExecute<int>(() => throw new InvalidOperationException("boom"), "myContext", msg => logged = msg);
        Assert.AreEqual(0, result);
        Assert.IsNotNull(logged);
        StringAssert.Contains("myContext", logged);
        StringAssert.Contains("boom", logged);
    }

    [Test]
    public void SafeExecuteAsync_ReturnsValue_WhenNoException()
    {
        var result = ErrorHandler.SafeExecuteAsync(() => Task.FromResult(7), "ctx", _ => { }).GetAwaiter().GetResult();
        Assert.AreEqual(7, result);
    }

    [Test]
    public void SafeExecuteAsync_ReturnsDefault_AndInvokesLog_WhenException()
    {
        string logged = null;
        var result = ErrorHandler.SafeExecuteAsync<int>(
            () => Task.FromException<int>(new InvalidOperationException("async-boom")),
            "asyncCtx",
            msg => logged = msg).GetAwaiter().GetResult();
        Assert.AreEqual(0, result);
        Assert.IsNotNull(logged);
        StringAssert.Contains("asyncCtx", logged);
        StringAssert.Contains("async-boom", logged);
    }
}
