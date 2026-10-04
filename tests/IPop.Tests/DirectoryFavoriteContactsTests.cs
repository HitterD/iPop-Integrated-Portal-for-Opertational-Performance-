using System.Net;
using System.Reflection;
using System.Text.Json;
using IPop.Web.Services;
using Microsoft.JSInterop;
using Xunit;

namespace IPop.Tests;

public class DirectoryFavoriteContactsTests
{
    private sealed class Storage : IJSRuntime
    {
        public Dictionary<string, string> Values { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? result = null;
            if (identifier == "localStorage.getItem") result = Values.GetValueOrDefault((string)args![0]!);
            if (identifier == "localStorage.setItem") Values[(string)args![0]!] = (string)args[1]!;
            return ValueTask.FromResult(result is null ? default! : (TValue)result);
        }
    }
    private sealed class Server : HttpMessageHandler
    {
        public string[] Favorites = [];
        public TaskCompletionSource? Mutation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Favorites)) };
            if (Mutation is not null) await Mutation.Task;
            return new(HttpStatusCode.NoContent);
        }
    }
    private static void Account(AuthState auth, string? nik) => typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
        nik is null ? null : new DirectoryEmployeeDto(nik, nik, null, null, null, null, null, null, "", null, null, "off"));
    [Fact]
    public async Task Switching_account_clears_memory_and_uses_distinct_storage_keys()
    {
        var storage = new Storage(); var server = new Server { Favorites = ["first-contact"] };
        var auth = new AuthState(new HttpClient(server) { BaseAddress = new Uri("https://example.test") }, storage);
        var contacts = new FavoriteContacts(storage, auth);
        Account(auth, "A"); await contacts.EnsureLoadedAsync(); Assert.True(contacts.IsFavorite("first-contact"));
        Account(auth, "B"); Assert.Empty(contacts.All); server.Favorites = ["second-contact"];
        await contacts.EnsureLoadedAsync(); Assert.False(contacts.IsFavorite("first-contact")); Assert.True(contacts.IsFavorite("second-contact"));
        Assert.Contains("ipop.directory.favorites.v1.A", storage.Values.Keys); Assert.Contains("ipop.directory.favorites.v1.B", storage.Values.Keys);
        Account(auth, null); Assert.Empty(contacts.All);
    }
    [Fact]
    public async Task Changed_event_waits_for_server_mutation_before_filtered_list_refresh()
    {
        var storage = new Storage(); var server = new Server { Mutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var auth = new AuthState(new HttpClient(server) { BaseAddress = new Uri("https://example.test") }, storage); Account(auth, "A");
        var contacts = new FavoriteContacts(storage, auth); await contacts.EnsureLoadedAsync();
        var changed = 0; contacts.Changed += () => changed++;
        var toggle = contacts.ToggleAsync("contact"); Assert.Equal(0, changed);
        server.Mutation.SetResult(); Assert.True(await toggle); Assert.Equal(1, changed);
    }
}
