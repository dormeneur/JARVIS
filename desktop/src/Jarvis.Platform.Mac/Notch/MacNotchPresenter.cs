using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts.Notch;
using Jarvis.Core;
using Jarvis.Core.Cursor;
using Jarvis.Core.Notch;
using Microsoft.Extensions.Options;

namespace Jarvis.Platform.Mac.Notch;

/// <summary>
/// The notch on a Mac. The state rules are the Windows presenter's (NotchParking, collapse and
/// restore, click-away, Escape, hidden while paused or at rest under something fullscreen); the
/// frame and accent come from Jarvis.Core (NotchLayout, NotchAccent). Each change is sent to the
/// native SwiftUI notch as one JSON view, and its buttons come back as ids.
/// </summary>
public sealed class MacNotchPresenter : INotchPresenter
{
    /// <summary>Put aside longer than this, the next hover opens the home instead.</summary>
    private static readonly TimeSpan ParkedFor = TimeSpan.FromMinutes(30);
    private static MacNotchPresenter? _current;

    private readonly IOptionsMonitor<CursorOptions> _cursorOptions;
    private readonly Dictionary<int, Action> _actions = [];
    private Dictionary<string, string> _typed = [];
    private PauseSwitch? _pause;
    private NotchState _state = NotchState.Idle;
    private NotchState _shown = NotchState.Idle;
    private NotchState? _parked;
    private DateTimeOffset _parkedAt;
    private bool _hidden;
    private Timer? _visibilityCheck;

    public MacNotchPresenter(IOptionsMonitor<CursorOptions> cursorOptions)
    {
        _cursorOptions = cursorOptions;
    }

    /// <summary>The × and Escape outside the composer: cancel what's running (CompanionSession.Cancel).</summary>
    public Action? Escape { get; set; }

    /// <summary>NotchState.Settings opens the Settings window on this page (a Mac has room for a window).</summary>
    public Action<string?>? OpenSettings { get; set; }

    public NotchState Current => Volatile.Read(ref _shown);

    /// <summary>Creates the native notch; call once, after the app loop started.</summary>
    public void Initialize(PauseSwitch pause)
    {
        _pause = pause;
        _current = this;
        MainThread.Post(() =>
        {
            StartNative();
            Apply(NotchState.Idle);
        });
        pause.Changed += _ => MainThread.Post(Send);
        _visibilityCheck = new Timer(_ => MainThread.Post(UpdateVisibility), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private static unsafe void StartNative() => Native.NotchStart(&OnAction, &OnText);

    public void Show(NotchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        MainThread.Post(() => Apply(state));
    }

    public bool Restore()
    {
        if (_parked is not { } parked)
        {
            return false;
        }

        _parked = null;
        if (DateTimeOffset.UtcNow - _parkedAt > ParkedFor && parked is not NotchState.Form)
        {
            return false;
        }

        Apply(parked);
        return true;
    }

    private void Apply(NotchState state)
    {
        // A form put aside keeps working behind its pill (NotchParking has the rules).
        var (show, keep) = NotchParking.Next(state, _parked, _state);
        _parked = keep;
        if (show is null)
        {
            return;
        }

        if (show is NotchState.Settings settings)
        {
            OpenSettings?.Invoke(settings.Section);
            show = NotchState.Idle;
        }

        if (show.GetType() != _state.GetType())
        {
            _typed = [];
        }

        _state = show;
        Volatile.Write(ref _shown, show);
        Send();
    }

    private void UpdateVisibility()
    {
        var hide = Hidden();
        if (hide != _hidden)
        {
            Send();
        }
    }

    private bool Hidden()
    {
        var atRest = _state is NotchState.IdleState or NotchState.Working;
        return _pause?.IsPaused == true || (atRest && _cursorOptions.CurrentValue.HideInFullscreen && Shell.MacActivityWatch.Front() is { Fullscreen: true });
    }

    private void Send()
    {
        _hidden = Hidden();
        _actions.Clear();
        Native.NotchShow(Describe(_state).ToJsonString());
    }

    private int Id(Action action)
    {
        var id = _actions.Count + 1;
        _actions[id] = action;
        return id;
    }

    private JsonObject Button(string label, Action action) => new() { ["id"] = Id(action), ["label"] = label };

    /// <summary>Off the main thread: actions may capture the screen or start the microphone.</summary>
    private static Action Later(Action action) => () => Task.Run(action);

    private JsonObject Describe(NotchState state)
    {
        var frame = NotchLayout.FrameFor(state);
        var view = new JsonObject
        {
            ["kind"] = Kind(state),
            ["width"] = frame.Width,
            ["height"] = frame.Height,
            ["radius"] = NotchLayout.EffectiveCornerRadius(frame),
            ["sliver"] = frame.IsSliver,
            ["glowMoves"] = NotchAccent.GlowMoves(state),
            ["glowStrength"] = NotchAccent.GlowStrength(state),
            ["sweep"] = state is NotchState.Working,
            ["hidden"] = _hidden,
            ["close"] = state is NotchState.Response or NotchState.Home or NotchState.Composing or NotchState.EventDraft,
            ["clickable"] = state is NotchState.Response or NotchState.Form or NotchState.Home or NotchState.EventDraft or NotchState.Composing or NotchState.Parked,
            ["typing"] = state is NotchState.Composing,
        };
        if (frame.Spring is { } spring)
        {
            view["response"] = spring.Response;
            view["damping"] = spring.DampingFraction;
        }

        if (NotchAccent.GlowFor(state) is { } glow)
        {
            view["glow"] = new JsonArray(glow.Select(rgb => (JsonNode)rgb).ToArray());
        }

        if (NotchAccent.GlowReach(state) is { } reach)
        {
            view["glowReach"] = reach;
        }

        switch (state)
        {
            case NotchState.Listening:
                view["caption"] = "Listening";
                break;
            case NotchState.Dictating:
                view["caption"] = "Dictating";
                break;
            case NotchState.Thinking thinking:
                view["caption"] = thinking.Caption ?? "Thinking";
                break;
            case NotchState.Speaking:
                view["caption"] = "Speaking";
                break;
            case NotchState.Parked parked:
                view["caption"] = parked.Caption;
                view["busy"] = parked.Busy;
                break;
            case NotchState.Response response:
                view["text"] = response.Text;
                view["actions"] = new JsonArray((response.Actions ?? []).Select(action => (JsonNode)Button(action.Label, Later(action.Invoke))).ToArray());
                break;
            case NotchState.Composing composing:
                view["history"] = new JsonArray((composing.History ?? []).Select(turn => (JsonNode)new JsonObject
                {
                    ["question"] = turn.Question,
                    ["answer"] = turn.Answer,
                    ["at"] = turn.At?.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture),
                }).ToArray());
                break;
            case NotchState.Home home:
                view["greeting"] = home.Greeting;
                view["hint"] = home.Hint;
                view["homeActions"] = new JsonArray(home.Actions.Select(item => (JsonNode)HomeItem(item)).ToArray());
                view["recent"] = new JsonArray(home.Recent.Select(item => (JsonNode)HomeItem(item)).ToArray());
                break;
            case NotchState.Form form:
                view["form"] = FormView(form);
                break;
            case NotchState.EventDraft draft:
                var start = draft.Event.Start.ToLocalTime();
                view["event"] = new JsonObject
                {
                    ["title"] = draft.Event.Title,
                    ["when"] = draft.Event.AllDay
                        ? start.ToString("dddd d MMMM", System.Globalization.CultureInfo.CurrentCulture)
                        : $"{start.ToString("ddd d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture)}–{draft.Event.End.ToLocalTime():HH:mm}",
                    ["location"] = draft.Event.Location,
                    ["destination"] = draft.Destination,
                    ["add"] = Id(Later(() => draft.Add(draft.Event))),
                    ["cancel"] = Id(Later(draft.Cancel)),
                };
                break;
        }

        return view;
    }

    private JsonObject HomeItem(HomeItem item)
    {
        var node = new JsonObject { ["id"] = Id(Later(item.Invoke)), ["glyph"] = item.Glyph, ["title"] = item.Title, ["detail"] = item.Detail };
        if (item.Second is { } second)
        {
            node["second"] = Button(second.Label, Later(second.Invoke));
        }

        return node;
    }

    /// <summary>
    /// The one primary button the Windows panel has (Fill it in, Stop, Update answers, Try again,
    /// Done), plus Save to vault for typed answers and Answer again.
    /// </summary>
    private JsonObject FormView(NotchState.Form form)
    {
        var actions = form.Actions;
        var anyFilled = form.Cards.Any(card => card.Filled);
        var buttons = new JsonArray();
        if (actions.StopFilling is { } stop)
        {
            buttons.Add(Button("Stop", Later(stop)));
        }
        else if (form.Fillable > 0 || _typed.Count > 0)
        {
            buttons.Add(Button(anyFilled ? "Update answers" : "Fill it in", Later(() => FillIn(form))));
        }
        else if (form.Retry > 0)
        {
            buttons.Add(Button("Try again", Later(() => FillIn(form))));
        }
        else if (anyFilled)
        {
            buttons.Add(Button("Done", Later(actions.Dismiss)));
        }

        if (actions.Reload is { } reload)
        {
            buttons.Add(Button("Answer again", Later(() => reload(null))));
        }

        buttons.Add(Button("Save to vault", () => _ = SaveAsync(form)));
        if (buttons.Count == 0 || !anyFilled)
        {
            buttons.Add(Button("Close", Later(actions.Dismiss)));
        }

        return new JsonObject
        {
            ["title"] = form.Title,
            ["status"] = form.Status,
            ["progress"] = form.FillProgress,
            ["buttons"] = buttons,
            ["cards"] = new JsonArray(form.Cards.Select(card => (JsonNode)new JsonObject
            {
                ["id"] = card.Id,
                ["question"] = card.Question,
                ["answer"] = card.Answer,
                ["source"] = card.Source,
                ["state"] = card.State.ToString().ToLowerInvariant(),
                ["note"] = card.Note,
                ["filled"] = card.Filled,
                ["choices"] = card.Choices is null ? null : new JsonArray(card.Choices.Select(choice => (JsonNode)choice).ToArray()),
            }).ToArray()),
        };
    }

    private void FillIn(NotchState.Form form)
    {
        foreach (var (id, text) in _typed)
        {
            form.Actions.Used?.Invoke(id, text); // what the user typed is what goes in
        }

        form.Actions.FillIn?.Invoke();
    }

    private async Task SaveAsync(NotchState.Form form)
    {
        var edits = _typed.Select(pair => form.Cards.FirstOrDefault(card => card.Id == pair.Key) is { } card ? new FormEdit(card.Question, pair.Value) : null)
            .OfType<FormEdit>().ToList();
        if (edits.Count == 0)
        {
            return;
        }

        var said = await Task.Run(() => form.Actions.Save(edits)).ConfigureAwait(false);
        MainThread.Post(() =>
        {
            if (_state is NotchState.Form current && ReferenceEquals(current.Actions, form.Actions))
            {
                Apply(current with { Status = said });
            }
        });
    }

    private static string Kind(NotchState state) => state switch
    {
        NotchState.IdleState => "idle",
        NotchState.Working => "working",
        NotchState.Listening => "listening",
        NotchState.Dictating => "dictating",
        NotchState.Thinking => "thinking",
        NotchState.Speaking => "speaking",
        NotchState.Parked => "parked",
        NotchState.Composing => "composing",
        NotchState.Response => "response",
        NotchState.Form => "form",
        NotchState.Home => "home",
        NotchState.EventDraft => "event",
        _ => "idle",
    };

    private static bool ClosesOnClickAway(NotchState state) => state is NotchState.Home or NotchState.Form or NotchState.Response { KeepOnClickAway: false };

    /// <summary>Esc or a click outside: the chat is put aside as it is, a form shrinks to its pill, anything else closes.</summary>
    private void Collapse()
    {
        switch (_state)
        {
            case NotchState.Composing:
                _parked = _state;
                _parkedAt = DateTimeOffset.UtcNow;
                Apply(NotchState.Idle);
                break;
            case NotchState.Form form:
                _parked = form;
                _parkedAt = DateTimeOffset.UtcNow;
                Apply(NotchParking.PillFor(form));
                break;
            default:
                Apply(NotchState.Idle);
                break;
        }
    }

    /// <summary>The ×, or Escape while the notch has the keyboard.</summary>
    private void CloseNow()
    {
        switch (_state)
        {
            case NotchState.Composing composing:
                _state = NotchState.Idle; // so Dismiss's own Show(Idle) isn't a no-op race
                Task.Run(composing.Dismiss);
                break;
            case NotchState.Form form:
                Collapse();
                break;
            default:
                if (Escape is { } escape)
                {
                    Task.Run(escape);
                }
                else
                {
                    Apply(NotchState.Idle);
                }

                break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnAction(int id) => MainThread.Guard(() =>
    {
        if (_current is not { } notch)
        {
            return;
        }

        switch (id)
        {
            case -1:
                notch.CloseNow();
                break;
            case -2 when ClosesOnClickAway(notch._state):
                notch.Collapse();
                break;
            case -3:
                notch.Restore();
                break;
            default:
                if (notch._actions.TryGetValue(id, out var action))
                {
                    action();
                }

                break;
        }
    });

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnText(int kind, byte* text) => MainThread.Guard(() =>
    {
        var value = Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
        if (_current is not { } notch)
        {
            return;
        }

        if (kind == 0 && notch._state is NotchState.Composing composing)
        {
            Task.Run(() => composing.Submit(value)); // off the UI thread: a turn starts with a screen capture
        }
        else if (kind == 1 && JsonSerializer.Deserialize<Dictionary<string, string>>(value) is { } typed)
        {
            foreach (var (id, answer) in typed)
            {
                notch._typed[id] = answer;
            }
        }
    });
}
