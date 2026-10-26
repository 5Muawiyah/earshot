using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The answer to a pairing request runs in the request event, whose task nobody awaits. A failure of the question or of the
// accept is kept, so the pairing reads as a handler that failed, with its type and HRESULT, not as a pairing someone cancelled.
// Before, the exception was never observed and the pairing ended as RejectedByHandler, "Pairing was cancelled.".
[TestClass]
public sealed class PairingHandlerTests
{
    private sealed class Prompt(Func<Task<bool>> answer) : IPairingPrompt
    {
        public Task<bool> ConfirmAsync(FixPairKind kind, string? pin) => answer();
    }

    [TestMethod]
    public async Task AYesAcceptsAndANoDoesNot()
    {
        int accepted = 0;
        Assert.IsNull(await WinRtPairingApi.AnswerRequestAsync(FixPairKind.ConfirmOnly, null, new Prompt(() => Task.FromResult(true)), () => accepted++));
        Assert.IsNull(await WinRtPairingApi.AnswerRequestAsync(FixPairKind.ConfirmOnly, null, new Prompt(() => Task.FromResult(false)), () => accepted++));
        Assert.AreEqual(1, accepted);
    }

    [TestMethod]
    public async Task AQuestionOrAnAcceptThatThrowsIsReturned()
    {
        var asked = new InvalidOperationException("window gone");
        Assert.AreSame(asked, await WinRtPairingApi.AnswerRequestAsync(FixPairKind.ConfirmPinMatch, "123456", new Prompt(() => throw asked), () => { }));

        var accept = new IOException("accept", unchecked((int)0x8000000E));
        Assert.AreSame(accept, await WinRtPairingApi.AnswerRequestAsync(FixPairKind.ConfirmOnly, null, new Prompt(() => Task.FromResult(true)), () => throw accept));
    }

    // A question closed because the attempt ended is not a failure of the handler: Windows' own status says how it ended.
    [TestMethod]
    public async Task AQuestionClosedByTheEndOfTheAttemptAcceptsNothingAndIsNoFailure()
    {
        int accepted = 0;
        Assert.IsNull(await WinRtPairingApi.AnswerRequestAsync(FixPairKind.ConfirmPinMatch, "1", new Prompt(() => Task.FromCanceled<bool>(new CancellationToken(true))), () => accepted++));
        Assert.AreEqual(0, accepted);
    }

    [TestMethod]
    public void TheFirstFailureIsKeptAndTurnsTheResultIntoAHandlerFailure()
    {
        var failure = new PairingHandlerFailure();
        var first = new IOException("accept", unchecked((int)0x8000000E));
        failure.Record(null);
        failure.Record(first);
        failure.Record(new InvalidOperationException("later"));

        PairingCallResult rejected = new("RejectedByHandler", 17, false, null);
        PairingCallResult result = failure.Apply(rejected);

        Assert.AreEqual(PairingHandlerFailure.Name, result.Name);
        Assert.AreEqual(unchecked((int)0x8000000E), result.Code);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Threw, "Not a pairing status, so it is not read as a cancel.");
        StringAssert.Contains(result.Detail, "IOException");
        StringAssert.Contains(result.Detail, "RejectedByHandler");
        Assert.AreSame(rejected, new PairingHandlerFailure().Apply(rejected), "With no failure the result is Windows' own.");
    }
}
