using BridgeMcp.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BridgeMcp.Proxy.Tests;

public class SessionStoreTests
{
    [Fact]
    public void GetOrCreateInvokesFactoryOnce()
    {
        var store = new SessionStore();
        var calls = 0;
        var entry1 = store.GetOrCreate("s1", id => { calls++; return new SessionStore.SessionEntry(id, null!, null!); });
        var entry2 = store.GetOrCreate("s1", id => { calls++; return new SessionStore.SessionEntry(id, null!, null!); });

        Assert.Same(entry1, entry2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetReturnsNullForUnknown()
    {
        var store = new SessionStore();
        Assert.Null(store.Get("nope"));
    }

    [Fact]
    public void RemoveDropsEntry()
    {
        var store = new SessionStore();
        store.GetOrCreate("s1", id => new SessionStore.SessionEntry(id, null!, null!));
        Assert.True(store.Remove("s1", out _));
        Assert.Null(store.Get("s1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void CountReflectsEntries()
    {
        var store = new SessionStore();
        store.GetOrCreate("a", id => new SessionStore.SessionEntry(id, null!, null!));
        store.GetOrCreate("b", id => new SessionStore.SessionEntry(id, null!, null!));
        Assert.Equal(2, store.Count);
    }
}
