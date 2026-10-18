using NexZen.Application.ViewModels;
using NexZen.Core.AI;
using NexZen.Core.Interfaces;
using Xunit;

namespace NexZen.Tests;

// A reply that ends after the user switched tabs must land in the tab that asked.
public class AiTabRoutingTests
{
    [Fact]
    public async Task Error_after_tab_switch_goes_to_the_asking_tab()
    {
        var client = new PendingChatClient();
        var vm = NewViewModel(client);
        vm.SwitchTo("A", "Tab A", "about:blank");
        vm.MessageInput = "hello";
        var send = vm.SendMessageCommand.ExecuteAsync(null);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.SwitchTo("B", "Tab B", "about:blank");
        client.Finish(new AiChatResult(false, "", "Rate limited"));
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(vm.Messages);
        vm.SwitchTo("A", "Tab A", "about:blank");
        Assert.Equal(new[] { "hello", "Rate limited" }, vm.Messages.Select(m => m.Text));
        Assert.True(vm.Messages[1].IsSystem);
    }

    [Fact]
    public async Task Stop_after_tab_switch_removes_the_empty_draft_from_the_asking_tab()
    {
        var client = new PendingChatClient();
        var vm = NewViewModel(client);
        vm.SwitchTo("A", "Tab A", "about:blank");
        vm.MessageInput = "hello";
        var send = vm.SendMessageCommand.ExecuteAsync(null);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.SwitchTo("B", "Tab B", "about:blank");
        vm.StopCommand.Execute(null);
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(vm.Messages);
        vm.SwitchTo("A", "Tab A", "about:blank");
        Assert.Equal(new[] { "hello" }, vm.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task Reply_on_the_same_tab_still_fills_the_draft()
    {
        var client = new PendingChatClient();
        var vm = NewViewModel(client);
        vm.SwitchTo("A", "Tab A", "about:blank");
        vm.MessageInput = "hello";
        var send = vm.SendMessageCommand.ExecuteAsync(null);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.Finish(new AiChatResult(true, "Hi there", null));
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "hello", "Hi there" }, vm.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task Error_for_a_closed_tab_is_dropped()
    {
        var client = new PendingChatClient();
        var vm = NewViewModel(client);
        vm.SwitchTo("A", "Tab A", "about:blank");
        vm.MessageInput = "hello";
        var send = vm.SendMessageCommand.ExecuteAsync(null);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.SwitchTo("B", "Tab B", "about:blank");
        vm.DropTab("A");
        client.Finish(new AiChatResult(false, "", "Rate limited"));
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(vm.Messages);
    }

    internal static AiFunctionViewModel NewViewModel(IAiChatClient client)
        => new(client, new FakeSecrets(), new FakeSettings());

    internal sealed class PendingChatClient : IAiChatClient
    {
        private readonly TaskCompletionSource<AiChatResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Finish(AiChatResult result) => _result.TrySetResult(result);

        public Task<AiConnectionTestResult> TestConnectionAsync(
            AiChatEndpoint endpoint, string apiKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new AiConnectionTestResult(true, "ok", null));

        public async Task<AiChatResult> CompleteAsync(
            AiChatEndpoint endpoint, string apiKey, IReadOnlyList<AiChatMessage> messages, int? maxTokens,
            CancellationToken cancellationToken = default, IProgress<string>? progress = null,
            IProgress<string>? status = null)
        {
            Started.TrySetResult();
            return await _result.Task.WaitAsync(cancellationToken);
        }
    }

    internal sealed class FakeSecrets : IAiSecretStore
    {
        public bool HasKey => true;
        public bool HasKeyFor(string profileId) => true;
        public void SaveKey(string apiKey) { }
        public void SaveKeyFor(string profileId, string apiKey) { }
        public string? TryLoadKey() => "test-key";
        public string? TryLoadKeyFor(string profileId) => "test-key";
        public void DeleteKey() { }
        public void DeleteKeyFor(string profileId) { }
    }

    internal sealed class FakeSettings : ISettingsService
    {
        public int TabColumns { get; set; }
        public string TabBackgroundColor { get; set; } = "";
        public string? TabBackgroundImagePath { get; set; }
        public string SearchEngine { get; set; } = "Google";
        public string NexShareDeviceLabel { get; set; } = "";
        public List<string> TrustedDevices { get; set; } = new();
        public string AiProvider { get; set; } = AiProviderDefaults.SpaceXaiName;
        public string AiBaseUrl { get; set; } = AiProviderDefaults.SpaceXaiBaseUrl;
        public string AiModel { get; set; } = AiProviderDefaults.SpaceXaiModel;
        public string AiOutputLanguage { get; set; } = "English";
        public int AiChatFontSize { get; set; } = 14;
        public List<AiProviderProfile> AiCustomProviders { get; set; } = new();
        public string AiActiveProfileId { get; set; } = AiProviderDefaults.SpaceXaiId;
        public string AiSpaceXaiModel { get; set; } = AiProviderDefaults.SpaceXaiModel;
        public string AiLocalModelId { get; set; } = "";
        public bool AiThinking { get; set; }
        public void Save() { }
    }
}
