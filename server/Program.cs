using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ChatDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<Presence>();
var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
    db.Database.ExecuteSqlRaw(@"
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'chat') EXEC('CREATE SCHEMA chat');
IF OBJECT_ID(N'chat.Messages', N'U') IS NULL
BEGIN
    CREATE TABLE [chat].[Messages] (
        [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Room] nvarchar(40) NOT NULL,
        [Username] nvarchar(24) NOT NULL,
        [Text] nvarchar(2000) NOT NULL,
        [SentAt] datetime2 NOT NULL
    );
    CREATE INDEX [IX_Messages_Room_SentAt] ON [chat].[Messages] ([Room], [SentAt]);
END");
}

app.MapGet("/api/rooms", async (ChatDb db) =>
{
    var rooms = await db.Messages.Select(m => m.Room).Distinct().OrderBy(r => r).ToListAsync();
    if (rooms.Count == 0) rooms.Add("general");
    return rooms;
});

app.MapGet("/api/rooms/{room}/messages", async (ChatDb db, string room, int take) =>
    (await db.Messages.Where(m => m.Room == room)
        .OrderByDescending(m => m.SentAt).Take(Math.Clamp(take <= 0 ? 50 : take, 1, 200))
        .Select(m => new { m.Username, m.Text, m.SentAt }).ToListAsync())
    .OrderBy(m => m.SentAt));

app.MapHub<ChatHub>("/hub/chat");
app.Run();

public class Presence
{
    public ConcurrentDictionary<string, (string User, string Room)> Online { get; } = new();
}

public class ChatHub : Hub
{
    private readonly ChatDb _db;
    private readonly Presence _presence;
    public ChatHub(ChatDb db, Presence presence) { _db = db; _presence = presence; }

    public async Task Join(string username, string room)
    {
        username = (username ?? "").Trim(); room = string.IsNullOrWhiteSpace(room) ? "general" : room.Trim();
        if (username.Length == 0 || username.Length > 24) { await Clients.Caller.SendAsync("Rejected", "bad username"); return; }
        _presence.Online[Context.ConnectionId] = (username, room);
        await Groups.AddToGroupAsync(Context.ConnectionId, room);
        var history = await _db.Messages.Where(m => m.Room == room)
            .OrderByDescending(m => m.SentAt).Take(50)
            .Select(m => new { m.Username, m.Text, m.SentAt }).ToListAsync();
        await Clients.Caller.SendAsync("History", history.OrderBy(m => m.SentAt));
        await Clients.Group(room).SendAsync("Users",
            _presence.Online.Values.Where(v => v.Room == room).Select(v => v.User).Distinct().OrderBy(u => u));
        await Clients.OthersInGroup(room).SendAsync("System", $"{username} joined");
    }

    public async Task SendMessage(string text)
    {
        if (!_presence.Online.TryGetValue(Context.ConnectionId, out var me)) return;
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 2000) return;
        var msg = new ChatMessage { Room = me.Room, Username = me.User, Text = text, SentAt = DateTime.UtcNow };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        await Clients.Group(me.Room).SendAsync("Message", new { msg.Username, msg.Text, msg.SentAt });
    }

    public async Task Typing()
    {
        if (_presence.Online.TryGetValue(Context.ConnectionId, out var me))
            await Clients.OthersInGroup(me.Room).SendAsync("Typing", me.User);
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        if (_presence.Online.TryRemove(Context.ConnectionId, out var me))
        {
            await Clients.Group(me.Room).SendAsync("Users",
                _presence.Online.Values.Where(v => v.Room == me.Room).Select(v => v.User).Distinct().OrderBy(u => u));
            await Clients.Group(me.Room).SendAsync("System", $"{me.User} left");
        }
        await base.OnDisconnectedAsync(ex);
    }
}

public class ChatMessage
{
    public long Id { get; set; }
    public string Room { get; set; } = "general";
    public string Username { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; }
}

public class ChatDb : DbContext
{
    public ChatDb(DbContextOptions<ChatDb> o) : base(o) { }
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasDefaultSchema("chat");
        mb.Entity<ChatMessage>().Property(m => m.Room).HasMaxLength(40);
        mb.Entity<ChatMessage>().Property(m => m.Username).HasMaxLength(24);
        mb.Entity<ChatMessage>().HasIndex(m => new { m.Room, m.SentAt });
    }
}
