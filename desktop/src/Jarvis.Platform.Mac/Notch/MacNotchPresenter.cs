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

    /// <summary>The shelf strip under the home's buttons: header, gap, one row of tiles.</summary>
    private const double ShelfHeight = 16 + 8 + 70 + 10;

    private readonly IOptionsMonitor<CursorOptions> _cursorOptions;
    private readonly IOptionsMonitor<NotchOptions> _notchOptions;
    private readonly Dictionary<int, Action> _actions = [];
    private Dictionary<string, string> _typed = [];

    /// <summary>The form's Files page, once opened: its library, and the form it belongs to.</summary>
    private (FormActions Of, FormLibrary? Library)? _files;
    private PauseSwitch? _pause;
    private NotchState _state = NotchState.Idle;
    private NotchState _shown = NotchState.Idle;
    private NotchState? _parked;
    private DateTimeOffset _parkedAt;
    private bool _hidden;
    private Timer? _visibilityCheck;

    public MacNotchPresenter(IOptionsMonitor<CursorOptions> cursorOptions, IOptionsMonitor<NotchOptions> notchOptions)
    {
        _cursorOptions = cursorOptions;
        _notchOptions = notchOptions;
    }

    /// <summary>The × and Escape outside the composer: cancel what's running (CompanionSession.Cancel).</summary>
    public Action? Escape { get; set; }

    public NotchState Current => Volatile.Read(ref _shown);

    /// <summary>Files dropped on the notch; unset, the notch takes no drops.</summary>
    public NotchShelf? Shelf { get; set; }

    /// <summary>Raised on the main thread when files are dragged up to the sleeping notch: open the home to take them.</summary>
    public event Action? FilesDragged;

    /// <summary>Raised on the main thread with files dropped on the Vault target (or sent there from the shelf).</summary>
    public event Action<IReadOnlyList<string>>? VaultDropped;

    private bool _dropping;
    private bool _openedByDrag;
    private bool _hoverOpened;

    /// <summary>The next home was opened by resting on the notch (or a file drag), so leaving it closes it.</summary>
    public void OpenedByHover() => _hoverOpened = true;

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

        if (show.GetType() != _state.GetType())
        {
            _typed = [];
            _files = null;
        }

        if (show is not NotchState.Home)
        {
            _hoverOpened = false;
        }

        if (_state is NotchState.Home && show is not NotchState.Home)
        {
            (_dropping, _openedByDrag) = (false, false);
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
        var look = _notchOptions.CurrentValue;
        var view = new JsonObject
        {
            ["kind"] = Kind(state),
            ["width"] = frame.IsSliver ? frame.Width : Math.Round(frame.Width * look.SizeFactor),
            ["height"] = frame.Height,
            ["radius"] = NotchLayout.EffectiveCornerRadius(frame) * (frame.IsSliver ? 1 : look.CornerFactor),
            ["haptic"] = look.Haptics,
            ["sliver"] = frame.IsSliver,
            ["glowMoves"] = NotchAccent.GlowMoves(state),
            ["glowStrength"] = NotchAccent.GlowStrength(state),
            ["sweep"] = state is NotchState.Working,
            ["hidden"] = _hidden,
            ["close"] = state is NotchState.Response or NotchState.Home or NotchState.Composing or NotchState.EventDraft or NotchState.Settings,
            ["clickable"] = state is NotchState.Response or NotchState.Form or NotchState.Home or NotchState.EventDraft or NotchState.Composing or NotchState.Parked or NotchState.Settings,
            // Settings takes the keyboard too: its text fields (invite code, custom colour) need it.
            ["typing"] = state is NotchState.Composing or NotchState.Settings,
            ["leaveCloses"] = state is NotchState.Home && _hoverOpened && look.CloseOnLeave,
        };
        if (frame.Spring is { } spring)
        {
            view["response"] = spring.Response * _notchOptions.CurrentValue.SpeedFactor;
            view["damping"] = spring.DampingFraction;
        }

        if (look.Glow && NotchAccent.GlowFor(state) is { } glow)
        {
            // "cursor": the cursor's colour in all three blobs, so the notch matches it.
            var cursor = CursorColor.Parse(_cursorOptions.CurrentValue.Color);
            var colours = look.GlowColour.Equals("cursor", StringComparison.OrdinalIgnoreCase) ? glow.Select(_ => cursor) : glow;
            view["glow"] = new JsonArray(colours.Select(rgb => (JsonNode)rgb).ToArray());
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
                if (composing.Talk is { } talk)
                {
                    view["talk"] = Id(() => HoldToTalk(talk));
                }

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
                if (Shelf is { } shelf && (ShelfOn ? shelf.Items : []) is var items && (items.Count > 0 || _dropping))
                {
                    view["height"] = frame.Height + ShelfHeight;
                    view["shelf"] = new JsonObject
                    {
                        ["dropping"] = _dropping,
                        ["shelfOn"] = ShelfOn,
                        ["clear"] = Id(() => { shelf.Clear(); Send(); }),
                        ["items"] = new JsonArray(items.Select(path => (JsonNode)new JsonObject
                        {
                            ["path"] = path,
                            ["name"] = Path.GetFileName(path.TrimEnd('/')),
                            ["remove"] = Id(() => { shelf.Remove(path); Send(); }),
                            ["toVault"] = Id(() => VaultDropped?.Invoke([path])),
                        }).ToArray()),
                    };
                }

                break;
            case NotchState.Form form:
                view["form"] = FormView(form);
                break;
            case NotchState.Settings settings:
                view["section"] = settings.Section;
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

        JsonObject? files = null;
        if (actions.Files is { } library)
        {
            files = new JsonObject { ["open"] = Id(() => _ = LoadFilesAsync(form.Actions, library)) };
            if (_files is { Library: { } loaded } shown && ReferenceEquals(shown.Of, form.Actions))
            {
                static JsonArray List(IReadOnlyList<StoredFile> stored) => new(stored.Select(file => (JsonNode)new JsonObject
                {
                    ["path"] = file.Path,
                    ["name"] = file.Name,
                    ["modified"] = file.Modified?.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture),
                }).ToArray());
                files["resumes"] = List(loaded.Resumes);
                files["context"] = List(loaded.Context);
                files["history"] = List(loaded.History);
            }
        }

        return new JsonObject
        {
            ["files"] = files,
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

    /// <summary>
    /// The composer's mic: pressed, it starts listening (the notch turns into the listening pill, so
    /// the button never sees the release); the left button coming up is watched instead, as on Windows.
    /// </summary>
    private static void HoldToTalk(Action<bool> talk) => Task.Run(async () =>
    {
        talk(true);
        while ((Native.MouseButtons() & 1) != 0)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        talk(false);
    });

    private async Task LoadFilesAsync(FormActions of, FormFiles files)
    {
        var library = await files.Load().ConfigureAwait(false);
        MainThread.Post(() =>
        {
            _files = (of, library);
            Send();
        });
    }

    /// <summary>The Files page's picks and deletions; the answer goes in the panel's status line.</summary>
    private async Task FilesAsync(JsonElement request)
    {
        if (_state is not NotchState.Form { Actions.Files: { } files } form)
        {
            return;
        }

        var paths = request.TryGetProperty("paths", out var list) ? list.EnumerateArray().Select(path => path.GetString()).OfType<string>().ToList() : [];
        var said = request.GetProperty("kind").GetString() switch
        {
            "resume" when paths.Count > 0 => await files.AddResume(paths[0]).ConfigureAwait(false),
            "context" when paths.Count > 0 => await files.AddContext(paths).ConfigureAwait(false),
            "delete" when paths.Count > 0 => await files.Delete(paths[0]).ConfigureAwait(false),
            _ => null,
        };
        if (said is null)
        {
            return;
        }

        var library = await files.Load().ConfigureAwait(false);
        MainThread.Post(() =>
        {
            _files = (form.Actions, library);
            if (_state is NotchState.Form current && ReferenceEquals(current.Actions, form.Actions))
            {
                Apply(current with { Status = said });
            }
        });
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

    private static bool ClosesOnClickAway(NotchState state) => state is NotchState.Home or NotchState.Form or NotchState.Settings or NotchState.Response { KeepOnClickAway: false };

    /// <summary>A file drag reached the notch: open the home (or show its drop zone) to take it.</summary>
    private bool ShelfOn => Shelf is not null && _notchOptions.CurrentValue.Shelf;

    private void FilesNear()
    {
        // The Vault target is there even with the shelf turned off.
        if (Shelf is null || _pause?.IsPaused == true)
        {
            return;
        }

        if (_state is NotchState.Home)
        {
            _dropping = true;
            Send();
        }
        else if (_state is NotchState.IdleState or NotchState.Working or NotchState.Parked)
        {
            (_dropping, _openedByDrag, _hoverOpened) = (true, true, true);
            FilesDragged?.Invoke();
        }
    }

    /// <summary>The drag ended. Dropped elsewhere, a home that only opened for it closes again.</summary>
    private void DragEnded()
    {
        if (!_dropping)
        {
            return;
        }

        _dropping = false;
        if (_openedByDrag && _state is NotchState.Home)
        {
            _openedByDrag = false;
            Apply(NotchState.Idle);
        }
        else
        {
            Send();
        }
    }

    private void DroppedOnVault(IReadOnlyList<string> paths)
    {
        (_dropping, _openedByDrag) = (false, false);
        VaultDropped?.Invoke(paths);
    }

    private void Dropped(IEnumerable<string> paths)
    {
        Shelf?.Add(paths);
        (_dropping, _openedByDrag) = (false, false);
        if (_state is NotchState.Home)
        {
            Send();
        }
    }

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
            case NotchState.Settings settings:
                settings.Close();
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
            case -4:
                notch.FilesNear();
                break;
            case -5:
                notch.DragEnded();
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
        else if (kind == 5 && JsonSerializer.Deserialize<List<string>>(value) is { } dropped)
        {
            notch.Dropped(dropped);
        }
        else if (kind == 6 && JsonSerializer.Deserialize<List<string>>(value) is { } forVault)
        {
            notch.DroppedOnVault(forVault);
        }
        else if (kind == 7 && notch._state is NotchState.Settings open)
        {
            open.PopOut(value.Length == 0 ? null : value); // the page the notch was showing
        }
        else if (kind == 4)
        {
            var request = JsonDocument.Parse(value).RootElement.Clone();
            _ = notch.FilesAsync(request);
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
