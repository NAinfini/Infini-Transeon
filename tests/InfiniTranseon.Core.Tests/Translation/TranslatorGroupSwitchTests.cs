using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Translation;

namespace InfiniTranseon.Core.Tests.Translation;

public sealed class TranslatorGroupSwitchTests
{
    [Fact]
    public async Task ForegroundScopeNeverGuessesAndRetranslatesOnlyTheMatchedTarget()
    {
        TargetInstanceId foreground = new(Guid.NewGuid());
        TargetInstanceId background = new(Guid.NewGuid());
        var cancelled = new List<TargetInstanceId>();
        var retranslated = new List<TargetInstanceId>();
        var service = new TranslatorGroupSwitchService(
            (target, _) => { cancelled.Add(target); return ValueTask.CompletedTask; },
            (target, _, _) => { retranslated.Add(target); return ValueTask.CompletedTask; });
        RunningTranslationTarget[] targets =
        [
            new(foreground, Guid.NewGuid(), true, ["line"]),
            new(background, Guid.NewGuid(), false, ["other"]),
        ];

        TranslatorGroupSwitchResult result = await service.SwitchAsync(
            Guid.NewGuid(), TranslatorGroupSwitchScope.ForegroundMatched, targets, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        Assert.Equal([foreground], result.AffectedTargets);
        Assert.Equal([foreground], cancelled);
        Assert.Equal([foreground], retranslated);

        result = await service.SwitchAsync(
            Guid.NewGuid(), TranslatorGroupSwitchScope.ForegroundMatched,
            [targets[1]], null, TestContext.Current.CancellationToken);
        Assert.False(result.Applied);
        Assert.Equal("translationGroup.noMatchingTarget", result.StatusCode);
    }

    [Fact]
    public async Task ExplicitTargetSetIgnoresTargetsNotNamedByTheUser()
    {
        TargetInstanceId selected = new(Guid.NewGuid());
        TargetInstanceId omitted = new(Guid.NewGuid());
        var touched = new List<TargetInstanceId>();
        var service = new TranslatorGroupSwitchService(
            (_, _) => ValueTask.CompletedTask,
            (target, _, _) => { touched.Add(target); return ValueTask.CompletedTask; });

        TranslatorGroupSwitchResult result = await service.SwitchAsync(
            Guid.NewGuid(),
            TranslatorGroupSwitchScope.TargetSet,
            [
                new(selected, Guid.NewGuid(), false, ["visible"]),
                new(omitted, Guid.NewGuid(), true, ["visible"]),
            ],
            new HashSet<TargetInstanceId> { selected },
            TestContext.Current.CancellationToken);

        Assert.True(result.Applied);
        Assert.Equal([selected], touched);
    }
}
