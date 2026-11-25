using Microsoft.Extensions.Caching.Memory;

// Each signed-in account has its own cursor. A new process or an evicted entry
// gets a fresh cursor, so reconnecting devices always reconcile with SQLite.
sealed class InboxChanges : IDisposable
{
    sealed class Feed
    {
        public Guid Cursor = Guid.NewGuid();
        public TaskCompletionSource<Guid> Next = NewSignal();
    }

    static TaskCompletionSource<Guid> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly MemoryCache feeds = new(new MemoryCacheOptions());
    readonly object gate = new();

    public void Notify(params string[] users)
    {
        lock (gate)
        {
            foreach (var user in users)
            {
                if (!feeds.TryGetValue<Feed>(user, out var feed) || feed is null) continue;
                feed.Cursor = Guid.NewGuid();
                feed.Next.TrySetResult(feed.Cursor);
                feed.Next = NewSignal();
            }
        }
    }

    public void NotifyChat(Store db, string chat)
    {
        var pair = db.One("SELECT a,b FROM conversations WHERE id=$c", ("c", chat));
        if (pair is not null) Notify((string)pair["a"]!, (string)pair["b"]!);
    }

    async Task<IResult> Wait(string user, Guid? since, CancellationToken cancel)
    {
        Task<Guid> next;
        Guid cursor;
        lock (gate)
        {
            if (!feeds.TryGetValue<Feed>(user, out var feed) || feed is null)
            {
                feed = new Feed();
                feeds.Set(user, feed, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(3) });
            }
            cursor = feed.Cursor;
            if (since != cursor) return Results.Ok(new { cursor });
            next = feed.Next.Task;
        }

        try { cursor = await next.WaitAsync(TimeSpan.FromSeconds(20), cancel); }
        catch (TimeoutException) { /* The client can now renew its authenticated request. */ }
        return Results.Ok(new { cursor });
    }

    public void Map(WebApplication app) =>
        app.MapGet("/api/inbox/changes", (HttpContext ctx, Guid? since) =>
            Wait((string)ctx.Items["user"]!, since, ctx.RequestAborted));

    public void Dispose() => feeds.Dispose();

    public static void Init(Store db)
    {
        db.Exec("""
            CREATE TABLE IF NOT EXISTS chat_versions(
                chat TEXT PRIMARY KEY REFERENCES conversations(id) ON DELETE CASCADE,
                version INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS edited_messages(
                message INTEGER PRIMARY KEY REFERENCES messages(seq) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS conversations_by_b ON conversations(b);
            """);

        // Versions share the transaction with the change, including mutations by older clients.
        foreach (var (table, operation, chat) in new[] {
            ("messages", "INSERT", "NEW.chat_id"), ("messages", "UPDATE OF body", "NEW.chat_id"),
            ("messages", "DELETE", "OLD.chat_id"),
            ("reactions", "INSERT", "(SELECT chat_id FROM messages WHERE seq=NEW.message)"),
            ("reactions", "DELETE", "(SELECT chat_id FROM messages WHERE seq=OLD.message)") })
        {
            var trigger = "inbox_" + table + "_" + operation.Split(' ')[0];
            db.Exec($"""
                CREATE TRIGGER IF NOT EXISTS {trigger} AFTER {operation} ON {table}
                BEGIN
                    INSERT INTO chat_versions(chat,version)
                    SELECT id,1 FROM conversations WHERE id={chat}
                    ON CONFLICT(chat) DO UPDATE SET version=version+1;
                END;
                """);
        }
        db.Exec("""
            CREATE TRIGGER IF NOT EXISTS mark_message_edit AFTER UPDATE OF body ON messages
            BEGIN INSERT OR IGNORE INTO edited_messages(message) VALUES(NEW.seq); END;
            """);
    }
}
