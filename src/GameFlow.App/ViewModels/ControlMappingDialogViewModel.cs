using GameFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameFlow.App.ViewModels;

public sealed class ControlMappingDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<GameFlow.Core.Models.MappingRule> backgroundRules;

    public ControlMappingDialogViewModel(
        string selectionKey,
        ProfileDocument profile,
        ILoggerFactory loggerFactory)
    {
        SelectionKey = ControlRuleMatcher.EnsurePhysicalSelectionKey(selectionKey);
        SelectionTitle = ControlRuleMatcher.GetTitle(SelectionKey);
        IsVirtualTarget = SelectionKey.StartsWith("virtual:", StringComparison.OrdinalIgnoreCase);
        SelectionScopeLabel = IsVirtualTarget ? "VIRTUAL OUTPUT TARGET" : "PHYSICAL INPUT";
        SelectionHint = IsVirtualTarget
            ? $"Choose the physical source that should produce {SelectionTitle}. The target is already selected."
            : ControlRuleMatcher.GetHint(SelectionKey);

        backgroundRules = [.. profile.Rules.Where(rule => !ControlRuleMatcher.Matches(SelectionKey, rule))];

        var focusedProfile = profile with
        {
            Rules = [.. profile.Rules.Where(rule => ControlRuleMatcher.Matches(SelectionKey, rule))]
        };

        Editor = new MappingEditorViewModel(loggerFactory.CreateLogger<MappingEditorViewModel>());
        Editor.LoadFromProfile(focusedProfile);
        Editor.SetPreferredSelection(SelectionKey);

        if (focusedProfile.Rules.Count == 0)
        {
            Editor.PreFillFromElement(SelectionKey);
        }

        Editor.RulesChanged += OnRulesChanged;
    }

    public event EventHandler<IReadOnlyList<GameFlow.Core.Models.MappingRule>>? MergedRulesChanged;

    public string SelectionKey { get; }

    public string SelectionTitle { get; }

    public string SelectionHint { get; }

    public bool IsVirtualTarget { get; }

    public string SelectionScopeLabel { get; }

    public MappingEditorViewModel Editor { get; }

    private void OnRulesChanged(object? sender, IReadOnlyList<GameFlow.Core.Models.MappingRule> focusedRules)
    {
        var merged = backgroundRules.Concat(focusedRules).ToArray();
        MergedRulesChanged?.Invoke(this, merged);
    }

    public void Dispose()
    {
        Editor.RulesChanged -= OnRulesChanged;
        Editor.Dispose();
    }
}
