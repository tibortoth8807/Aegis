using Aegis.Application.Assistant;
using Aegis.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Aegis.UnitTests.Application;

[TestClass]
public class IntentRecognizerTests
{
    [DataTestMethod]
    [DataRow("What are my open requests?", Intent.MyRequests)]
    [DataRow("Mik a nyitott kéréseim?", Intent.MyRequests)]
    [DataRow("Is anything waiting for my approval?", Intent.Approvals)]
    [DataRow("Mit kell jóváhagynom?", Intent.Approvals)]
    [DataRow("How do I request access to the CRM?", Intent.HowToRequest)]
    [DataRow("Hogyan igényelhetek jogosultságot?", Intent.HowToRequest)]
    [DataRow("Which trainings are due?", Intent.MyTrainings)]
    [DataRow("Milyen tréningjeim vannak?", Intent.MyTrainings)]
    [DataRow("What access do I have?", Intent.MyAccess)]
    [DataRow("The VPN does not work", Intent.NewTicket)]
    [DataRow("status of my tickets", Intent.MyTickets)]
    [DataRow("hi", Intent.Help)]
    [DataRow("Tell me about the history of Rome", Intent.Unknown)]
    public void RecognisesTheIntent(string message, Intent expected) => Assert.AreEqual(expected, IntentRecognizer.Recognize(message).Intent);

    [DataTestMethod]
    [DataRow("Who is the manager of Anna Kiss?", "anna kiss")]
    [DataRow("Ki Kiss Anna vezetője?", "kiss anna")]
    [DataRow("manager of Bob", "bob")]
    public void ExtractsThePersonForManagerQuestions(string message, string name)
    {
        var r = IntentRecognizer.Recognize(message);
        Assert.AreEqual(Intent.WhoIsManager, r.Intent);
        Assert.AreEqual(name, r.Subject);
    }

    [TestMethod]
    public void ExtractsTheSystemForOwnerQuestions()
    {
        var r = IntentRecognizer.Recognize("Who owns the CRM?");
        Assert.AreEqual(Intent.SystemOwner, r.Intent);
        Assert.AreEqual("crm", r.Subject);
    }

    [TestMethod]
    public void FoldRemovesAccents() => Assert.AreEqual("jovahagyas arvizturo", IntentRecognizer.Fold("Jóváhagyás Árvíztűrő"));
}

[TestClass]
public class AssistantServiceTests
{
    private readonly Mock<IAssistantKnowledge> _knowledge = new();
    private readonly Mock<IChatModel> _model = new();

    public AssistantServiceTests()
    {
        _knowledge.Setup(k => k.ProfileAsync(It.IsAny<CancellationToken>())).ReturnsAsync("Alice Analyst, E10002, Sales");
        _knowledge.Setup(k => k.MyOpenRequestsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((1, (IReadOnlyList<string>)new List<string> { "REQ-000007 CRM / User - pending manager" }));
        _knowledge.Setup(k => k.AwaitingMeAsync(It.IsAny<CancellationToken>())).ReturnsAsync((0, (IReadOnlyList<string>)new List<string>()));
        _knowledge.Setup(k => k.MyTrainingsDueAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _knowledge.Setup(k => k.MyAccessAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "CRM / User until 2027-11-23" });
        _knowledge.Setup(k => k.MyOpenTicketsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        _model.SetupGet(m => m.Name).Returns("ollama:llama3.2");
    }

    private AssistantService Service(bool withModel) =>
        new(_knowledge.Object, withModel ? new[] { _model.Object } : Array.Empty<IChatModel>(), NullLogger<AssistantService>.Instance);

    [TestMethod]
    public async Task KnownQuestions_AreAnsweredFromData_WithoutTheModel()
    {
        var reply = await Service(withModel: true).AskAsync(new AskInput("what are my open requests"), CancellationToken.None);
        StringAssert.Contains(reply.Text, "REQ-000007");
        Assert.AreEqual("built-in", reply.Source);
        Assert.AreEqual("/requests", reply.Links[0].Route);
        _model.Verify(m => m.CompleteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task OtherQuestions_GoToTheModel_WithTheUsersFactsAsContext()
    {
        string? prompt = null;
        _model.Setup(m => m.CompleteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<ChatTurn>, CancellationToken>((p, _, _) => prompt = p)
            .ReturnsAsync("  Your CRM access is valid until 23 November 2027.  ");

        var reply = await Service(withModel: true).AskAsync(new AskInput("Until when can I use the customer system?"), CancellationToken.None);

        Assert.AreEqual("Your CRM access is valid until 23 November 2027.", reply.Text);
        Assert.AreEqual("ollama:llama3.2", reply.Source);
        StringAssert.Contains(prompt, "CRM / User until 2027-11-23");
        StringAssert.Contains(prompt, "Never invent");
    }

    [TestMethod]
    public async Task WhenTheModelFails_TheBuiltInAssistantAnswers()
    {
        _model.Setup(m => m.CompleteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var reply = await Service(withModel: true).AskAsync(new AskInput("Tell me a joke about auditors"), CancellationToken.None);
        StringAssert.StartsWith(reply.Text, "The AI model is not reachable");
        Assert.AreEqual("built-in", reply.Source);
        Assert.IsTrue(reply.Suggestions.Count > 0);
    }

    [TestMethod]
    public async Task WithoutAModel_UnknownQuestionsGetSuggestions()
    {
        var reply = await Service(withModel: false).AskAsync(new AskInput("Tell me a joke"), CancellationToken.None);
        StringAssert.StartsWith(reply.Text, "I'm not sure");
        Assert.AreEqual(4, reply.Suggestions.Count);
    }

    [TestMethod]
    public async Task AmbiguousNames_AreListedForTheUserToPick()
    {
        _knowledge.Setup(k => k.FindPeopleAsync("anna", It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new PersonHit(1, "Anna Kiss", "Analyst", "Sales", "Mara Manager", "anna.kiss@aegis.example"),
            new PersonHit(2, "Anna Nagy", "Engineer", "Platform", null, "anna.nagy@aegis.example"),
        });
        var reply = await Service(withModel: false).AskAsync(new AskInput("who is the manager of Anna?"), CancellationToken.None);
        StringAssert.Contains(reply.Text, "Several people match");
        Assert.AreEqual(2, reply.Links.Count);
    }
}
