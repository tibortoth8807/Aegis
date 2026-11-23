using Aegis.Application.Access;
using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.UnitTests.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static Aegis.UnitTests.Domain.People;

namespace Aegis.UnitTests.Application;

[TestClass]
public class ProvisioningTests
{
    private readonly People _p = new();
    private readonly Mock<IProvisioningGateway> _gateway = new(MockBehavior.Strict);
    private readonly Mock<DbSet<AccessGrant>> _grants = new();
    private readonly Mock<IAegisDbContext> _db = new();

    public ProvisioningTests() => _db.SetupGet(d => d.Grants).Returns(_grants.Object);

    private AccessProvisioner Provisioner() =>
        new(_db.Object, _gateway.Object, TimeProvider.System, NullLogger<AccessProvisioner>.Instance);

    private AccessRequest ApprovedRequest()
    {
        var r = _p.Request(_p.Alice, _p.Alice, _p.CrmUser);
        r.Id = 42;
        r.Number = "REQ-000042";
        r.Submit(Ctx(_p.Alice));
        r.Approve(Ctx(_p.Manager));
        r.Approve(Ctx(_p.Owner));
        return r;
    }

    [TestMethod]
    public async Task Provisioning_CallsTheTargetSystem_ThenActivatesAndRecordsTheGrant()
    {
        _gateway.Setup(g => g.GrantAsync(_p.Alice, _p.CrmUser, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var r = ApprovedRequest();

        var ok = await Provisioner().ProvisionAsync(r, Ctx(Actor.System), CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(AccessRequestStatus.Active, r.Status);
        _gateway.Verify(g => g.GrantAsync(_p.Alice, _p.CrmUser, It.IsAny<CancellationToken>()), Times.Once);
        _grants.Verify(g => g.Add(It.Is<AccessGrant>(x => x.RequestId == 42 && x.EmployeeId == _p.Alice.Id && x.ValidUntil == r.ValidUntil)), Times.Once);
    }

    [TestMethod]
    public async Task WhenTheTargetSystemFails_TheRequestStaysApproved_AndNoGrantIsRecorded()
    {
        _gateway.Setup(g => g.GrantAsync(It.IsAny<Employee>(), It.IsAny<SystemRole>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LDAP server unavailable"));
        var r = ApprovedRequest();

        var ok = await Provisioner().ProvisionAsync(r, Ctx(Actor.System), CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(AccessRequestStatus.Approved, r.Status);
        _grants.Verify(g => g.Add(It.IsAny<AccessGrant>()), Times.Never);
    }

    [TestMethod]
    public async Task Cancellation_IsNotSwallowed()
    {
        _gateway.Setup(g => g.GrantAsync(It.IsAny<Employee>(), It.IsAny<SystemRole>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Provisioner().ProvisionAsync(ApprovedRequest(), Ctx(Actor.System), CancellationToken.None));
    }
}
