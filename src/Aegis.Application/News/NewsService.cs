using Aegis.Application.Common;
using Aegis.Domain.Common;
using Aegis.Domain.News;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.News;

public sealed record NewsDto(int Id, string Title, string Body, DateTimeOffset PublishedAt, string Author, bool Pinned);

public sealed record NewsInput(string Title, string Body, bool Pinned);

public sealed class NewsService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    public async Task<IReadOnlyList<NewsDto>> ListAsync(int take, CancellationToken ct) =>
        await db.News.AsNoTracking().OrderByDescending(n => n.Pinned).ThenByDescending(n => n.PublishedAt).Take(Math.Clamp(take, 1, 50))
            .Join(db.Employees, n => n.AuthorId, e => e.Id, (n, e) => new NewsDto(n.Id, n.Title, n.Body, n.PublishedAt, e.FirstName + " " + e.LastName, n.Pinned))
            .ToListAsync(ct);

    public async Task<NewsDto> PublishAsync(NewsInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var n = new NewsItem
        {
            Title = Guard.Required(input.Title, nameof(input.Title), 150),
            Body = Guard.Required(input.Body, nameof(input.Body), 8000),
            Pinned = input.Pinned,
            PublishedAt = time.GetUtcNow(),
            AuthorId = user.Actor.EmployeeId,
        };
        db.News.Add(n);
        await db.SaveChangesAsync(ct);
        return new NewsDto(n.Id, n.Title, n.Body, n.PublishedAt, user.Actor.Name, n.Pinned);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var n = (await db.News.FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("News", id);
        db.News.Remove(n);
        await db.SaveChangesAsync(ct);
    }
}
