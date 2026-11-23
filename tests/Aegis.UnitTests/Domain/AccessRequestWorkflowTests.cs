using Aegis.Domain.Access;
using Aegis.Domain.Common;
using static Aegis.UnitTests.Domain.People;

namespace Aegis.UnitTests.Domain;

[TestClass]
public class AccessRequestWorkflowTests
{
    private People _p = null!;

    [TestInitialize]
    public void Setup() => _p = new People();

    [TestMethod]
    public void StandardRole_GoesThroughManagerAndOwner_ThenProvisioning()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        Assert.AreEqual(AccessRequestStatus.Draft, r.Status);

        r.Submit(Ctx(_p.Alice));
        Assert.AreEqual(AccessRequestStatus.PendingManager, r.Status);
        Assert.AreEqual(Now, r.SubmittedAt);

        r.Approve(Ctx(_p.Manager));
        Assert.AreEqual(AccessRequestStatus.PendingOwner, r.Status);

        r.Approve(Ctx(_p.Owner));
        Assert.AreEqual(AccessRequestStatus.Approved, r.Status);

        r.Provision(Ctx(Actor.System));
        Assert.AreEqual(AccessRequestStatus.Active, r.Status);
        Assert.AreEqual(Now.AddDays(365), r.ValidUntil);
        CollectionAssert.AreEqual(new[] { "create", "submit", "approve", "approve", "provision" }, r.History.Select(h => h.Action).ToArray());
    }

    [TestMethod]
    public void PrivilegedRole_NeedsSecurityOfficer_AndIsCappedAt30Days()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmAdmin);
        r.Submit(Ctx(_p.Alice));
        r.Approve(Ctx(_p.Manager));
        r.Approve(Ctx(_p.Owner));
        Assert.AreEqual(AccessRequestStatus.PendingSecurity, r.Status);

        r.Approve(Ctx(_p.Security));
        r.Provision(Ctx(Actor.System));
        Assert.AreEqual(Now.AddDays(AccessRequestState.PrivilegedMaxDays), r.ValidUntil);
    }

    [TestMethod]
    public void ManagerRequestingForTheirReport_SkipsTheManagerStep()
    {
        var r = _p.Request(_p.Manager, _p.Bob, _p.CrmUser);
        r.Submit(Ctx(_p.Manager));
        Assert.AreEqual(AccessRequestStatus.PendingOwner, r.Status);
        CollectionAssert.Contains(r.ApproverIds, _p.Manager.Id);
    }

    [TestMethod]
    public void NobodyApprovesTheirOwnRequest()
    {
        var r = _p.Request(_p.Manager, _p.Manager, _p.CrmUser);   // the manager has no manager → owner step
        r.Submit(Ctx(_p.Manager));
        var e = Assert.ThrowsException<ForbiddenException>(() => r.Approve(Ctx(_p.Manager)));
        StringAssert.Contains(e.Message, "you created");
    }

    [TestMethod]
    public void TheSamePersonCannotApproveTwoSteps()
    {
        _p.Crm.OwnerId = _p.Manager.Id;                          // the manager also owns the system
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        r.Submit(Ctx(_p.Alice));
        r.Approve(Ctx(_p.Manager));
        Assert.ThrowsException<ForbiddenException>(() => r.Approve(Ctx(_p.Manager)));
        r.Approve(Ctx(_p.Admin));                                 // an administrator can stand in for the owner
        Assert.AreEqual(AccessRequestStatus.Approved, r.Status);
    }

    [TestMethod]
    public void OnlyTheBeneficiarysManagerApprovesTheManagerStep()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        r.Submit(Ctx(_p.Alice));
        Assert.ThrowsException<ForbiddenException>(() => r.Approve(Ctx(_p.Bob)));
        Assert.IsFalse(r.State.CanDecide(r, _p.Owner.AsActor()));
        Assert.IsTrue(r.State.CanDecide(r, _p.Manager.AsActor()));
    }

    [TestMethod]
    public void RejectingNeedsAReason_AndEndsTheWorkflow()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        r.Submit(Ctx(_p.Alice));
        Assert.ThrowsException<ValidationFailedException>(() => r.Reject(Ctx(_p.Manager)));
        r.Reject(Ctx(_p.Manager, "Use the shared reporting role instead"));
        Assert.AreEqual(AccessRequestStatus.Rejected, r.Status);
        Assert.IsTrue(r.State.IsFinal);
        Assert.AreEqual(Now, r.DecidedAt);
        var e = Assert.ThrowsException<InvalidStateTransitionException>(() => r.Approve(Ctx(_p.Owner)));
        Assert.AreEqual("Cannot approve a request that is rejected.", e.Message);
    }

    [TestMethod]
    public void AllowedActions_FollowTheState()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        CollectionAssert.AreEqual(new[] { "cancel", "submit" }, r.State.AllowedActions.ToArray());
        r.Submit(Ctx(_p.Alice));
        CollectionAssert.AreEqual(new[] { "approve", "cancel", "reject" }, r.State.AllowedActions.ToArray());
        r.Approve(Ctx(_p.Manager));
        r.Approve(Ctx(_p.Owner));
        CollectionAssert.AreEqual(new[] { "provision" }, r.State.AllowedActions.ToArray());
        r.Provision(Ctx(Actor.System));
        CollectionAssert.AreEqual(new[] { "expire", "revoke" }, r.State.AllowedActions.ToArray());
    }

    [TestMethod]
    public void ActiveAccess_ExpiresOnlyAfterItsEndDate()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        r.Submit(Ctx(_p.Alice));
        r.Approve(Ctx(_p.Manager));
        r.Approve(Ctx(_p.Owner));
        r.Provision(Ctx(Actor.System));

        Assert.ThrowsException<BusinessRuleException>(() => r.Expire(Ctx(Actor.System, at: Now.AddDays(100))));
        r.Expire(Ctx(Actor.System, at: Now.AddDays(366)));
        Assert.AreEqual(AccessRequestStatus.Expired, r.Status);
    }

    [TestMethod]
    public void RevokingIsForAdminsOwnersAndManagers()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        r.Submit(Ctx(_p.Alice));
        r.Approve(Ctx(_p.Manager));
        r.Approve(Ctx(_p.Owner));
        r.Provision(Ctx(Actor.System));

        Assert.ThrowsException<ForbiddenException>(() => r.Revoke(Ctx(_p.Bob, "no longer needed")));
        r.Revoke(Ctx(_p.Manager, "Moved to another team"));
        Assert.AreEqual(AccessRequestStatus.Revoked, r.Status);
    }

    [TestMethod]
    public void OnlyRequesterBeneficiaryOrAdminCanCancel()
    {
        var r = _p.Request(_p.Manager, _p.Bob, _p.CrmUser);
        Assert.ThrowsException<ForbiddenException>(() => r.Cancel(Ctx(_p.Alice)));
        r.Cancel(Ctx(_p.Bob));
        Assert.AreEqual(AccessRequestStatus.Cancelled, r.Status);
    }

    [TestMethod]
    public void ShortJustification_IsAValidationError()
    {
        var e = Assert.ThrowsException<ValidationFailedException>(() =>
            AccessRequest.Create(_p.Alice, _p.Alice, _p.CrmUser, "pls", Ctx(_p.Alice)));
        Assert.IsTrue(e.Errors.ContainsKey(nameof(AccessRequest.Justification)));
    }

    [TestMethod]
    public void EveryStatusHasExactlyOneStateClass()
    {
        foreach (var status in Enum.GetValues<AccessRequestStatus>())
            Assert.AreEqual(status, AccessRequestState.Of(status).Status);
    }
}
