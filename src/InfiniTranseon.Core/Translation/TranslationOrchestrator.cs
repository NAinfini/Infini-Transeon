using System.Runtime.CompilerServices;
using System.Threading.Channels;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Translation;

public sealed class TranslationOrchestrator
{
    private readonly TranslationChannelRunner _runner;

    public TranslationOrchestrator(TranslationChannelRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async IAsyncEnumerable<TranslationOutput> RunAsync(
        TextGeneration source,
        IReadOnlyList<TranslationChannelDefinition> channels,
        TranslationRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channels), "One to four channels are supported.");
        if (channels.Select(item => item.Id).Distinct().Count() != channels.Count ||
            channels.Select(item => item.DisplaySlot.SlotId).Distinct().Count() != channels.Count)
        {
            throw new ArgumentException("Channel and immutable slot IDs must be unique.", nameof(channels));
        }

        var output = Channel.CreateBounded<TranslationOutput>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] pumps = channels.Select(channel => PumpAsync(
            source, channel, options, output.Writer, linked.Token)).ToArray();
        Task completion = CompleteAsync(pumps, output.Writer);
        try
        {
            await foreach (TranslationOutput item in output.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                yield return item;
            await completion.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
        }
    }

    private async Task PumpAsync(
        TextGeneration source,
        TranslationChannelDefinition channel,
        TranslationRunOptions options,
        ChannelWriter<TranslationOutput> writer,
        CancellationToken cancellationToken)
    {
        await foreach (TranslationOutput item in _runner.RunAsync(
                           source, channel, options, cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CompleteAsync(Task[] pumps, ChannelWriter<TranslationOutput> writer)
    {
        try
        {
            await Task.WhenAll(pumps).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }
}
