using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpsFlow.Domain;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Seeding;

public sealed class DevelopmentSeeder(
    OpsFlowDbContext db,
    UserManager<ApplicationUser> users,
    IConfiguration configuration,
    ILogger<DevelopmentSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var password = configuration["Seed:DemoPassword"];
        if (string.IsNullOrWhiteSpace(password) || password == "REPLACE_ME")
        {
            throw new InvalidOperationException("Set Seed:DemoPassword (or Seed__DemoPassword) to a local-only password before seeding. Do not use a production secret.");
        }

        if (await db.Organizations.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Seed skipped because organizations already exist.");
            return;
        }

        var now = DateTime.UtcNow;
        var northwind = Organization("Northwind Facilities", "northwind", now);
        var harborline = Organization("Harborline Medical", "harborline", now);
        db.Organizations.AddRange(northwind, harborline);
        foreach (var organization in new[] { northwind, harborline })
        {
            foreach (var priority in Enum.GetValues<WorkOrderPriority>())
            {
                db.SlaPolicies.Add(new SlaPolicy
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organization.Id,
                    Priority = priority,
                    DurationSeconds = SlaClock.DefaultDurationSeconds[priority],
                    UpdatedAtUtc = now
                });
            }
        }

        var directory = new (string Email, string Name, Guid Northwind, Guid Harborline, OrganizationRole? NorthRole, OrganizationRole? HarborRole)[]
        {
            ("admin.northwind@opsflow.example", "Nina Alden", Guid.NewGuid(), Guid.Empty, OrganizationRole.Admin, null),
            ("dispatcher.northwind@opsflow.example", "Diego Marsh", Guid.NewGuid(), Guid.Empty, OrganizationRole.Dispatcher, null),
            ("tech.a.northwind@opsflow.example", "Amina Shah", Guid.NewGuid(), Guid.Empty, OrganizationRole.Technician, null),
            ("tech.b.northwind@opsflow.example", "Ben Okonkwo", Guid.NewGuid(), Guid.Empty, OrganizationRole.Technician, null),
            ("requester.northwind@opsflow.example", "Riley Chen", Guid.NewGuid(), Guid.Empty, OrganizationRole.Requester, null),
            ("admin.harborline@opsflow.example", "Helena Brooks", Guid.Empty, Guid.NewGuid(), null, OrganizationRole.Admin),
            ("dispatcher.harborline@opsflow.example", "Omar Farouk", Guid.Empty, Guid.NewGuid(), null, OrganizationRole.Dispatcher),
            ("tech.harborline@opsflow.example", "Priya Nair", Guid.Empty, Guid.NewGuid(), null, OrganizationRole.Technician),
            ("requester.harborline@opsflow.example", "Sam Patel", Guid.Empty, Guid.NewGuid(), null, OrganizationRole.Requester),
            ("alex.cross@opsflow.example", "Alex Cross", Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Admin, OrganizationRole.Technician)
        };

        foreach (var person in directory)
        {
            if (person.NorthRole is OrganizationRole northRole)
            {
                await CreateUserAsync(person.Email, person.Name, person.Northwind == Guid.Empty ? person.Harborline : person.Northwind, password, now);
                db.Memberships.Add(Member(northwind.Id, person.Northwind == Guid.Empty ? person.Harborline : person.Northwind, northRole, now));
            }

            if (person.HarborRole is OrganizationRole harborRole)
            {
                var userId = person.NorthRole is not null ? person.Northwind : person.Harborline;
                if (person.NorthRole is null)
                {
                    await CreateUserAsync(person.Email, person.Name, userId, password, now);
                }

                db.Memberships.Add(Member(harborline.Id, userId, harborRole, now));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var requester = directory[4];
        var techA = directory[2];
        var techB = directory[3];
        AddWorkOrder(northwind, requester.Northwind, null, "Lobby HVAC is blowing warm air", "The east lobby unit is not cooling.", "HVAC", WorkOrderPriority.Critical, WorkOrderStatus.Open, now.AddHours(-5));
        AddWorkOrder(northwind, requester.Northwind, techA.Northwind, "Badge reader offline at dock 2", "Contractors cannot badge in.", "Access", WorkOrderPriority.High, WorkOrderStatus.Assigned, now.AddHours(-8));
        AddWorkOrder(northwind, requester.Northwind, techB.Northwind, "Replace flickering stair lights", "Third floor stairwell lights cycle every few seconds.", "Electrical", WorkOrderPriority.Medium, WorkOrderStatus.InProgress, now.AddDays(-1));
        var resolved = AddWorkOrder(northwind, requester.Northwind, techA.Northwind, "Restock first-aid cabinet", "Cabinet outside the cafeteria is empty.", "Safety", WorkOrderPriority.Low, WorkOrderStatus.Resolved, now.AddDays(-2));
        resolved.ResolvedAtUtc = now.AddDays(-1);
        AddWorkOrder(harborline, directory[8].Harborline, null, "Waiting room outlet is dead", "The outlet by chair 4 has no power.", "Electrical", WorkOrderPriority.High, WorkOrderStatus.Open, now.AddHours(-2));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Development seed created two organizations and sample memberships. Demo passwords were read from configuration and are not logged.");
    }

    private async Task CreateUserAsync(string email, string name, Guid id, string password, DateTime now)
    {
        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = name,
            CreatedAtUtc = now
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(' ', result.Errors.Select(error => error.Description)));
        }
    }

    private static Organization Organization(string name, string slug, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Slug = slug,
        NextWorkOrderNumber = 1000,
        CreatedAtUtc = now
    };

    private static OrganizationMembership Member(Guid organizationId, Guid userId, OrganizationRole role, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        UserId = userId,
        Role = role,
        CreatedAtUtc = now
    };

    private WorkOrder AddWorkOrder(
        Organization organization,
        Guid requesterId,
        Guid? assigneeId,
        string title,
        string description,
        string category,
        WorkOrderPriority priority,
        WorkOrderStatus status,
        DateTime createdAt)
    {
        organization.NextWorkOrderNumber++;
        var duration = SlaClock.DefaultDurationSeconds[priority];
        var workOrder = new WorkOrder
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Number = organization.NextWorkOrderNumber,
            Title = title,
            Description = description,
            Category = category,
            Priority = priority,
            Status = status,
            RequesterUserId = requesterId,
            AssignedTechnicianUserId = assigneeId,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
            SlaDurationSeconds = duration,
            DueAtUtc = SlaClock.ComputeDueAt(createdAt, duration, 0)
        };
        db.WorkOrders.Add(workOrder);
        db.Activities.Add(new WorkOrderActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            WorkOrderId = workOrder.Id,
            ActivityType = "Created",
            Message = "Seeded work order.",
            CreatedAtUtc = createdAt
        });
        return workOrder;
    }
}
