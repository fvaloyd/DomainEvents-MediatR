using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSqlServer<StoreDbContext>("server=FRANCIS\\SQLEXPRESS;database=DomainEventsDb;user=sa;password=123;Encrypt=false;");

builder.Services.AddMediatR(opt => opt.RegisterServicesFromAssembly(typeof(Program).Assembly));

var app = builder.Build();

app.MapGet("items/{id:guid}", async (Guid id, StoreDbContext db) =>
{
    var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id);
    if (item is null)
        return Results.NotFound();
    return Results.Ok(new ItemResponse(item.Id, item.Name));
}).WithName("itemById");

app.MapPost("items/", async ([FromBody] ItemForCreation request, StoreDbContext db) =>
{
    var item = Item.Create(request.Name);
    await db.Items.AddAsync(item);
    await db.SaveChangesAsync();
    return Results.CreatedAtRoute("itemById", new { id = item.Id }, new ItemResponse(item.Id, item.Name));
});

app.Run();

record ItemForCreation(string Name);
record ItemResponse(Guid Id, string Name);

class StoreDbContext : DbContext
{
    private readonly IPublisher _publisher;

    public StoreDbContext(DbContextOptions<StoreDbContext> options, IPublisher publisher)
        : base(options)
    {
        _publisher = publisher;
    }

    public DbSet<Item> Items => Set<Item>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        var domainEvents = ChangeTracker.Entries<Entity>()
                                .Select(e => e.Entity)
                                .Where(e => e.DomainEvents.Any())
                                .SelectMany(e => e.DomainEvents)
                                .ToArray();

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            await _publisher.Publish(domainEvent, cancellationToken);
        }

        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>().Ignore(i => i.DomainEvents);
    }
}

record DomainEvent(Guid Id) : INotification;

abstract class Entity
{
    private readonly IList<DomainEvent> _domainEvents = new List<DomainEvent>();

    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearEvents()
        => _domainEvents.Clear();

    public void RaiseEvent(DomainEvent @event)
        => _domainEvents.Add(@event);
}


class Item : Entity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public static Item Create(string name)
    {
        var item = new Item()
        {
            Id = Guid.NewGuid(),
            Name = name
        };

        item.RaiseEvent(new ItemCreatedDomainEvent(Guid.NewGuid(), item.Id));

        return item;
    }
}

record ItemCreatedDomainEvent(Guid id, Guid ItemId) : DomainEvent(id);

class ItemCreatedDomainEventHandler : INotificationHandler<ItemCreatedDomainEvent>
{
    public ILogger<ItemCreatedDomainEventHandler> _logger;

    public ItemCreatedDomainEventHandler(ILogger<ItemCreatedDomainEventHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(ItemCreatedDomainEvent notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Item created::{notification.ItemId}");
        return Task.CompletedTask;
    }
}
