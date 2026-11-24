using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Text;
using Jarvis.Contracts.Vault;
using Jarvis.Core.Vault;

namespace Jarvis.Core.Companion;

/// <summary>
/// Selected text, anywhere: double-tap Ctrl with something selected (or the browser's right-click
/// "Jarvis" menu) and Jarvis offers to explain it, summarise it, save it to the vault, or answer a
/// question about it. Explaining and summarising send the text alone (no screenshot); private mode
/// keeps them on this PC like any turn. Saving is decided by the vault's brain on this PC (where the
/// note goes, what it learns about you), shown first, and written only when the user says so.
/// </summary>
public sealed partial class CompanionSession
{
    /// <summary>How much of a selection goes into a question (the rest of a long article isn't needed to explain it).</summary>
    private const int AskLimit = 6000;

    private static readonly TimeSpan SelectionBudget = TimeSpan.FromMilliseconds(900);

    private async Task ComposeOrSelectionAsync(Func<CancellationToken, Task<TextSelection?>> read)
    {
        TextSelection? selection = null;
        try
        {
            using var budget = new CancellationTokenSource(SelectionBudget);
            selection = await read(budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Couldn't read it in time: the text box, as before.
        }

        if (selection is { Text.Length: >= 2 })
        {
            Selected(selection, SelectionAction.Menu);
        }
        else
        {
            OpenComposer();
        }
    }

    /// <summary>Does what the user picked for a selection (from the menu here, or the browser's).</summary>
    public void Selected(TextSelection selection, SelectionAction action)
    {
        ArgumentNullException.ThrowIfNull(selection);
        switch (action)
        {
            case SelectionAction.Explain:
                Ask(SelectionRequest("Explain this simply and briefly", selection), withScreen: false);
                break;
            case SelectionAction.Summarise:
                Ask(SelectionRequest("Summarise this in a few short lines", selection), withScreen: false);
                break;
            case SelectionAction.Save:
                _ = SaveSelectionAsync(selection);
                break;
            default:
                ShowSelectionMenu(selection);
                break;
        }
    }

    /// <summary>The words of a request about a selection: the ask, where it's from, the text fenced off as quoted material.</summary>
    internal static string SelectionRequest(string ask, TextSelection selection)
    {
        var text = selection.Text.Trim();
        if (text.Length > AskLimit)
        {
            text = text[..AskLimit] + " …";
        }

        return $"{ask} (selected in {selection.Source}):\n\"\"\"\n{text}\n\"\"\"";
    }

    private void ShowSelectionMenu(TextSelection selection)
    {
        lock (_gate)
        {
            InterruptLocked();
        }

        var preview = string.Join(' ', selection.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        preview = preview.Length > 160 ? preview[..157] + "…" : preview;
        _services.Notch.Show(new NotchState.Response(
            $"“{preview}”",
            [
                new NotchAction("Explain", () => Selected(selection, SelectionAction.Explain)),
                new NotchAction("Summarise", () => Selected(selection, SelectionAction.Summarise)),
                new NotchAction("Save to Jarvis", () => Selected(selection, SelectionAction.Save)),
                new NotchAction("Ask about it", () => AskAbout(selection)),
            ],
            Card: new ResponseCard($"✦  Selected in {Short(selection.Source)}", $"{selection.Text.Length:N0} characters · Explain and Summarise send only this text, never your screen")));
    }

    private void AskAbout(TextSelection selection)
        => _services.Notch.Show(new NotchState.Composing(
            question => Ask(SelectionRequest(question.Trim().Length > 0 ? question.Trim() : "Explain this", selection), withScreen: false),
            Cancel,
            _services.History.Recent));

    private async Task SaveSelectionAsync(TextSelection selection)
    {
        if (_services.Local?.Vault is not { IsConnected: true } vault)
        {
            _services.Notch.Show(new NotchState.Response("Saving to your vault needs Jarvis connected to it. Connect it in Settings → Vault."));
            return;
        }

        _services.Notch.Show(new NotchState.Thinking("Working out where this belongs"));
        IngestPlan? plan;
        try
        {
            plan = await vault.PlanIngestAsync(selection.Text, selection.Source, selection.Url, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            _services.Notch.Show(new NotchState.Response(exception.Message));
            return;
        }

        if (plan is null)
        {
            _services.Notch.Show(new NotchState.Response("Your vault couldn't work out where to keep that. Nothing was saved."));
            return;
        }

        var about = plan.Facts.Count == 0 ? null
            : $"And about you → {string.Join(", ", plan.Facts.Select(fact => fact.Path).Distinct())}: {string.Join(" · ", plan.Facts.Select(fact => fact.Fact))}";
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Notch.Show(new NotchState.Response(
            plan.NotePath,
            [new NotchAction("Save", () => decision.TrySetResult(true)), new NotchAction("Cancel", () => decision.TrySetResult(false))],
            KeepOnClickAway: true,
            Card: new ResponseCard("✦  Save to your vault", about ?? "The text is kept word for word, with where it came from, so answers can quote it.")));
        if (await Task.WhenAny(decision.Task, Task.Delay(TimeSpan.FromMinutes(2), _time)).ConfigureAwait(false) != decision.Task || !await decision.Task.ConfigureAwait(false))
        {
            _services.Notch.Show(new NotchState.Response("Okay, nothing was saved."));
            return;
        }

        try
        {
            _services.Notch.Show(new NotchState.Thinking("Saving"));
            await vault.WriteFileAsync(plan.NotePath, plan.NoteContent, CancellationToken.None).ConfigureAwait(false);
            foreach (var group in plan.Facts.GroupBy(fact => fact.Path))
            {
                var text = await vault.ReadFileAsync(group.Key, CancellationToken.None).ConfigureAwait(false);
                foreach (var fact in group)
                {
                    text = ProfileNotes.Add(text, fact.Fact, group.Key);
                }

                await vault.WriteFileAsync(group.Key, text!, CancellationToken.None).ConfigureAwait(false);
            }

            await vault.ReindexAsync(CancellationToken.None).ConfigureAwait(false); // findable for chat and forms straight away
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            _services.Notch.Show(new NotchState.Response($"Couldn't save it: {exception.Message}"));
            return;
        }

        _services.Privacy.Record(0, "Saved something you selected to your vault on this PC");
        var said = plan.Facts.Count == 0 ? $"Saved to {plan.NotePath}." : $"Saved to {plan.NotePath}, and {plan.Facts.Count} thing{(plan.Facts.Count == 1 ? string.Empty : "s")} about you to your profile.";
        var done = new NotchState.Response(said, Card: new ResponseCard("✓  In your vault"));
        _services.Notch.Show(done);
        await Task.Delay(TimeSpan.FromSeconds(_options.CurrentValue.CaptionSeconds), _time).ConfigureAwait(false);
        _services.Notch.RestIfShowing(done);
    }

    private static string Short(string source) => source.Length > 40 ? source[..39] + "…" : source;
}
